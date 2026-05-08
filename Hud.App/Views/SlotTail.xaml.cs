using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hud.App.Services;
using HandReader.Core.Models;

namespace Hud.App.Views
{
    public partial class SlotTail : UserControl
    {
        private static readonly Regex BlindsInHeaderRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{1,2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)",
                RegexOptions.Compiled);
        private static readonly Regex TableNameRx =
            new(@"(?:Table\s+'(?<table>[^']+)'|Mesa\s+(?<table>.+?)\s+\d+-max)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly HandReaderService _service = new();
        private CancellationTokenSource? _cts;
        private long _lines;
        private string? _path;
        private readonly Queue<string> _buffer = new(3);
        private TextBlock? _startTimeText;
        private DateTime? _firstHandTime;
        private DateTime? _lastHandTime;

        public SlotTail()
        {
            InitializeComponent();
            DataContext = _service; // binding directo al servicio (Players, HeroName)
            AddStartTimeLabel();
        }

        private void Pick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Hand Histories|*.txt;*.log;*.*",
                Title = "Selecciona el archivo .txt con la mesa"
            };
            if (dlg.ShowDialog() == true)
            {
                _path = dlg.FileName;
                PathText.Text = ExtractTableLabelFromFile(_path);
            }
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                Pick_Click(sender, e);
                if (string.IsNullOrWhiteSpace(_path)) return;
            }
            Start(_path!);
        }

        public void StartAuto(string path, string tableName, string? heroName)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            _path = path;
            PathText.Text = tableName;
            if (!string.IsNullOrWhiteSpace(heroName))
                _service.HeroName = heroName.Trim().TrimEnd(':').Trim();

            Start(path);
        }

        public bool IsRunning => _cts is not null;

        public string? HeroName => _service.HeroName;

        public IReadOnlyList<PlayerStats> GetPlayersSnapshot() =>
            _service.Players.ToList();

        public void ClearSlot()
        {
            Stop();
            _path = null;
            PathText.Text = "(mesa)";
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

        private void PlayersGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid)
                return;
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row?.Item is not PlayerStats player)
                return;
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                return;
            if (string.Equals(player.Name, _service.HeroName, StringComparison.Ordinal))
                return;

            var tables = Array.Empty<MainWindow.TableSessionStats>() as IReadOnlyList<MainWindow.TableSessionStats>;
            DataVillainsWindow.DataVillainRow villain;
            if (VillainHistoryStore.TryGet(player.Name, out var historical))
            {
                villain = historical;
                tables = VillainHistoryStore.Tables;
            }
            else
            {
                var hero = FindHeroStats();
                var table = BuildCurrentTableStats(hero);
                villain = BuildVillainRow(player, table);
                tables = new[] { table };
            }

            var window = new DataVillainDetailWindow(villain, tables)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            grid.SelectedItem = null;
        }

        private static T? FindAncestor<T>(DependencyObject? current)
            where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Start(string path)
        {
            Stop();
            _lines = 0;
            Lines.Text = "0";
            _firstHandTime = null;
            _lastHandTime = null;
            UpdateTimeLabels();
            Last.Text = "—";
            LastLines.ItemsSource = Array.Empty<string>();

            // Muestra solo el nombre de mesa (o filename si no se detecta)
            PathText.Text = ExtractTableLabelFromFile(path);

            _service.Start(path);

            // bucle pequeño solo para contador/últimas líneas en UI
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    long last = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        if (!File.Exists(path)) { await System.Threading.Tasks.Task.Delay(200, ct); continue; }

                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Length < last) last = 0;
                        fs.Position = last;

                        using var rd = new StreamReader(fs);
                        string? line;
                        bool any = false;
                        while (!rd.EndOfStream && (line = await rd.ReadLineAsync()) != null)
                        {
                            any = true;
                            last = fs.Position;
                            Interlocked.Increment(ref _lines);
                            PushLast3(line);
                            TrackHandTimestamp(line);
                        }

                        if (any)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Lines.Text = _lines.ToString();
                                UpdateTimeLabels();
                                LastLines.ItemsSource = null;
                                LastLines.ItemsSource = _buffer.ToArray();
                            });
                        }

                        await System.Threading.Tasks.Task.Delay(200, ct);
                    }
                }
                catch
                {
                    // cancel
                }
            }, ct);
        }

        private void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _service.StopAndClear();
            _lines = 0;
            _buffer.Clear();

            Lines.Text = "0";
            _firstHandTime = null;
            _lastHandTime = null;
            UpdateTimeLabels();
            LastLines.ItemsSource = Array.Empty<string>();
        }

        private void PushLast3(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length > 96) line = line[..95] + "…";
            if (_buffer.Count == 3) _buffer.Dequeue();
            _buffer.Enqueue(line);
        }

        private void AddStartTimeLabel()
        {
            if (Last.Parent is not StackPanel bar) return;

            var label = new TextBlock
            {
                Text = "Inicio:",
                Foreground = (System.Windows.Media.Brush)FindResource("FgDim"),
                Margin = new Thickness(12, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            _startTimeText = new TextBlock
            {
                Text = "-",
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var insertAt = Math.Max(0, bar.Children.IndexOf(Last) - 1);
            bar.Children.Insert(insertAt, label);
            bar.Children.Insert(insertAt + 1, _startTimeText);
        }

        private void TrackHandTimestamp(string line)
        {
            if (!TryParseHandTimestamp(line, out var handTime)) return;

            _firstHandTime ??= handTime;
            _lastHandTime = handTime;
        }

        private void UpdateTimeLabels()
        {
            if (_startTimeText != null)
                _startTimeText.Text = FormatTimestamp(_firstHandTime);

            Last.Text = FormatTimestamp(_lastHandTime);
        }

        private static bool TryParseHandTimestamp(string line, out DateTime timestamp)
        {
            timestamp = default;
            var match = HeaderTimestampRx.Match(line);
            return match.Success
                && DateTime.TryParseExact(
                    match.Groups["stamp"].Value,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out timestamp);
        }

        private static string FormatTimestamp(DateTime? timestamp) =>
            timestamp.HasValue ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";

        private static string ExtractTableLabelFromFile(string path)
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            var tableName = fileName;
            string? blinds = null;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var rd = new StreamReader(fs);
                for (int i = 0; i < 200 && !rd.EndOfStream; i++)
                {
                    var line = rd.ReadLine() ?? "";

                    var tableMatch = TableNameRx.Match(line);
                    if (tableMatch.Success)
                        tableName = tableMatch.Groups["table"].Value.Trim();

                    if (blinds is null)
                    {
                        var blindsMatch = BlindsInHeaderRx.Match(line);
                        if (blindsMatch.Success)
                            blinds = $"{FormatBlind(blindsMatch.Groups["sb"].Value)}/{FormatBlind(blindsMatch.Groups["bb"].Value)}";
                    }

                    if (blinds is not null && tableName != fileName)
                        break;
                }
            }
            catch { }

            return blinds is null ? tableName : $"{tableName} ({blinds})";
        }

        private static string FormatBlind(string raw) =>
            PokerAmountParser.FormatBlind(raw);

        private PlayerStats? FindHeroStats()
        {
            foreach (var player in _service.Players)
            {
                if (string.Equals(player.Name, _service.HeroName, StringComparison.Ordinal))
                    return player;
            }

            return null;
        }

        private MainWindow.TableSessionStats BuildCurrentTableStats(PlayerStats? hero)
        {
            var tableName = PathText.Text;
            var lastPlayed = _lastHandTime ?? DateTime.Now;
            var bigBlind = ExtractBigBlindFromLabel(tableName);
            return new MainWindow.TableSessionStats(
                tableName,
                "NLH",
                lastPlayed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                lastPlayed,
                _path ?? "",
                _service.HeroName ?? "",
                bigBlind,
                Stake,
                hero?.HandsReceived ?? 0,
                hero?.VPIPPct ?? 0,
                hero?.PFRPct ?? 0,
                hero?.ThreeBetPct ?? 0,
                hero?.AF ?? 0,
                hero?.AFqPct ?? 0,
                hero?.CBetFlopPct ?? 0,
                hero?.FoldVsCBetFlopPct ?? 0,
                hero?.WTSDPct ?? 0,
                hero?.WSDPct ?? 0,
                hero?.WWSFPct ?? 0,
                0,
                0,
                tableName.Contains('$'),
                "0");
        }

        private DataVillainsWindow.DataVillainRow BuildVillainRow(PlayerStats player, MainWindow.TableSessionStats table) =>
            new(
                player.Name,
                table.TableName,
                table.GameFormat,
                table.IsCash,
                player.HandsReceived,
                player.HandsReceived,
                player.HandsReceived,
                player.VPIPPct,
                player.PFRPct,
                player.ThreeBetPct,
                player.AF,
                player.AFqPct,
                player.CBetFlopPct,
                player.FoldVsCBetFlopPct,
                player.WTSDPct,
                player.WSDPct,
                player.WWSFPct,
                0,
                0,
                table.Stake,
                _lastHandTime ?? DateTime.Now);

        private static double ExtractBigBlindFromLabel(string label)
        {
            var match = Regex.Match(label, @"\((?:\$)?(?<sb>[\d,.]+)\/(?:\$)?(?<bb>[\d,.]+)\)");
            return match.Success && PokerAmountParser.TryParse(match.Groups["bb"].Value, out var bb)
                ? bb
                : 1;
        }

        /// <summary>
        /// Busca en el archivo la línea con "Table '...'" y devuelve el nombre entre comillas.
        /// </summary>
        /// 
        public StakeProfile Stake { get; set; } = StakeProfile.Low; // setéalo al detectar BB de la mesa

        private static string? ExtractTableNameFromFile(string path)
        {
            try
            {
                // lee las primeras ~200 líneas buscando la mesa
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var rd = new StreamReader(fs);
                for (int i = 0; i < 200 && !rd.EndOfStream; i++)
                {
                    var l = rd.ReadLine() ?? "";
                    var match = TableNameRx.Match(l);
                    if (match.Success)
                        return match.Groups["table"].Value.Trim();
                }
            }
            catch { }
            return null;
        }
    }
}

