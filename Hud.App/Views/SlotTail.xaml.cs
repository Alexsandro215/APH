using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class SlotTail : UserControl
    {
        private static readonly Regex BlindsInHeaderRx =
            new(@"\((?<sb>(?:US)?[$€]?\s*\d+(?:[\.,]\d{1,2})?\s*(?:US)?[$€]?)\s*/\s*(?<bb>(?:US)?[$€]?\s*\d+(?:[\.,]\d{1,2})?\s*(?:US)?[$€]?)(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)",
                RegexOptions.Compiled);

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

        private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

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

                    var key = "Table '";
                    var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var start = idx + key.Length;
                        var end = line.IndexOf('\'', start);
                        if (end > start)
                            tableName = line.Substring(start, end - start);
                    }

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

        private static string FormatBlind(string raw)
        {
            var isCash = raw.Contains('$') ||
                raw.Contains('€') ||
                raw.Contains("US", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains('.') ||
                raw.Contains(',');

            raw = raw.Replace("$", "")
                .Replace("€", "")
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\u00A0", "")
                .Replace(" ", "")
                .Replace(",", ".");

            return double.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? $"{(isCash ? "$" : "")}{value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"
                : raw.Trim();
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
                    var key = "Table '";
                    var idx = l.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var start = idx + key.Length;
                        var end = l.IndexOf('\'', start);
                        if (end > start) return l.Substring(start, end - start);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
