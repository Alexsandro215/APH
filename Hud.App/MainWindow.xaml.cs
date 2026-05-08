using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;
using Hud.App.Views;
using Microsoft.Win32;

namespace Hud.App
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private static readonly Regex BlindsRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);
        private static readonly Regex TableRx =
            new(@"(?:Table\s+'(?<table>[^']+)'|Mesa\s+(?<table>.+?)\s+\d+-max)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HeaderGameRx =
            new(@"(?:PokerStars Hand #\d+|Mano #\d+ de PokerStars):\s+(?<game>.+?)\s+\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MaxTableRx =
            new(@"(?<max>\d+)-max", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeatRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+(?<seat>\d+):", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HandStartRx =
            new(@"^(?:PokerStars Hand #|Mano #)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DealtToRx =
            new(@"^(?:Dealt to|Repartido a)\s+(?<hero>.+?)\s+\[", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CollectedRx =
            PokerStarsHandHistory.CollectedRx;
        private static readonly Regex ReturnedRx =
            PokerStarsHandHistory.ReturnedRx;
        private static readonly Regex RaiseToRx =
            PokerStarsHandHistory.RaiseToRx;
        private static readonly Regex ActionAmountRx =
            PokerStarsHandHistory.ActionAmountRx;
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)", RegexOptions.Compiled);

        private StakeProfile _dashboardStake = StakeProfile.Low;

        public ObservableCollection<PlayerStats> DashboardPlayers { get; } = new();
        public ObservableCollection<TableSessionStats> RecentTables { get; } = new();

        public StakeProfile DashboardStake
        {
            get => _dashboardStake;
            private set
            {
                if (_dashboardStake == value) return;
                _dashboardStake = value;
                OnPropertyChanged(nameof(DashboardStake));
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            Title = LocalizationManager.Text("App.Title");
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            var settings = AppSettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                Directory.Exists(settings.PokerStarsHandHistoryFolder))
            {
                await AnalyzeAndLoadFolderAsync(settings.PokerStarsHandHistoryFolder, "Carpeta predefinida cargada desde configuracion.");
            }
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderGlobal");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new GlobalAnalysisWindow(RecentTables, DashboardInfoText.Text)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusGlobalOpen");
        }

        private void BtnOne_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderTables");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new MisTablasWindow(RecentTables, DashboardInfoText.Text)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusTablesOpen");
        }

        private void BtnBestWorst_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderVillains");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new DataVillainsWindow(RecentTables, DashboardInfoText.Text)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusVillainsOpen");
        }

        private void BtnLeakFinder_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderLeaks");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new LeakFinderWindow(RecentTables, DashboardInfoText.Text)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusLeaksOpen");
        }

        private void BtnHeroProfile_Click(object sender, RoutedEventArgs e)
        {
            var hero = DashboardPlayers.FirstOrDefault();
            if (hero is null || RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderHero");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new HeroProfileWindow(hero, RecentTables, DashboardStake, DashboardInfoText.Text)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = string.Format(LocalizationManager.Text("Common.StatusHeroOpen"), hero.Name);
        }

        private void BtnRT_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.Resources["HandReaderService"] is HandReaderService handService)
            {
                var rtWindow = new RealTimeWindow
                {
                    DataContext = handService,
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                rtWindow.Show();
                InfoText.Text = LocalizationManager.Text("Common.StatusRtOpen");
            }
            else
            {
                MessageBox.Show(
                    "El servicio HandReaderService no esta inicializado.\n" +
                    "Reinicia la aplicacion o revisa App.xaml.cs.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationManager.Text("Common.SelectHandFolderTitle")
            };

            var settings = AppSettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                Directory.Exists(settings.PokerStarsHandHistoryFolder))
            {
                dlg.InitialDirectory = settings.PokerStarsHandHistoryFolder;
            }

            if (dlg.ShowDialog(this) != true)
                return;

            var folder = dlg.FolderName;
            settings.PokerStarsHandHistoryFolder = folder;
            AppSettingsService.Save(settings);

            await AnalyzeAndLoadFolderAsync(folder, "Dashboard global actualizado.");
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow
            {
                Owner = this,
                ShowInTaskbar = false
            };

            if (window.ShowDialog() == true)
            {
                Title = LocalizationManager.Text("App.Title");
                var folder = window.SavedSettings.PokerStarsHandHistoryFolder;
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    await AnalyzeAndLoadFolderAsync(folder, "Configuracion guardada y carpeta cargada.");
                else
                    InfoText.Text = LocalizationManager.Text("Common.SettingsSaved");
            }
        }

        private async Task AnalyzeAndLoadFolderAsync(string folder, string successMessage)
        {
            DashboardInfoText.Text = string.Format(LocalizationManager.Text("Common.AnalyzingFolder"), folder);
            DashboardPlayers.Clear();
            RecentTables.Clear();

            try
            {
                var result = await Task.Run(() => AnalyzeFolder(folder));

                DashboardStake = result.Stake;
                if (result.Hero is not null)
                    DashboardPlayers.Add(result.Hero);
                foreach (var table in result.Tables)
                    RecentTables.Add(table);

                await Task.Run(() => VillainHistoryStore.RebuildFromTables(result.Tables));

                DashboardInfoText.Text =
                    result.Hero is null
                        ? $"{LocalizationManager.Text("Common.Folder")}: {folder} | {LocalizationManager.Text("Common.Files")}: {result.FileCount} | {LocalizationManager.Text("Common.NoHandsDetected")}"
                        : $"{LocalizationManager.Text("Common.Folder")}: {folder} | {LocalizationManager.Text("Common.Files")}: {result.FileCount} | {LocalizationManager.Text("Common.Hero")}: {result.Hero.Name} | {LocalizationManager.Text("Common.Hands")}: {result.Hero.HandsReceived} | Stake: {result.Stake}";
                InfoText.Text = successMessage;
            }
            catch (Exception ex)
            {
                DashboardInfoText.Text = LocalizationManager.Text("Common.FolderAnalysisFailed");
                MessageBox.Show(ex.Message, LocalizationManager.Text("Common.AnalyzeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static DashboardResult AnalyzeFolder(string folder)
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var agg = new StatsAggregator();
            var parser = new PokerStarsParser(agg);
            var stakeVotes = new Dictionary<StakeProfile, int>();

            foreach (var file in files)
            {
                VoteStake(stakeVotes, DetectStakeFromFile(file));
                parser.FeedLines(File.ReadLines(file), () => { });
            }

            var stake = stakeVotes.Count == 0
                ? StakeProfile.Low
                : stakeVotes.OrderByDescending(kv => kv.Value).First().Key;

            var players = agg.Players.Values
                .OrderByDescending(p => p.HandsReceived)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hero = players.FirstOrDefault();

            var tables = hero is null
                ? new List<TableSessionStats>()
                : files.Select(file => AnalyzeTableFile(file, hero.Name))
                    .Where(table => table is not null)
                    .Cast<TableSessionStats>()
                    .OrderByDescending(table => table.LastPlayedAt)
                    .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return new DashboardResult(files.Count, stake, hero, tables);
        }

        private static void VoteStake(Dictionary<StakeProfile, int> votes, StakeProfile stake)
        {
            votes.TryGetValue(stake, out var count);
            votes[stake] = count + 1;
        }

        private static StakeProfile DetectStakeFromFile(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path).Take(200))
                {
                    var match = BlindsRx.Match(line);
                    if (!match.Success) continue;

                    if (!TryParseAmount(match.Groups["bb"].Value, out var bb))
                        continue;

                    if (bb <= 0.10) return StakeProfile.Low;
                    if (bb >= 2.00) return StakeProfile.High;
                    return StakeProfile.Mid;
                }
            }
            catch { }

            return StakeProfile.Low;
        }

        private static TableSessionStats? AnalyzeTableFile(string path, string heroName)
        {
            var lines = File.ReadLines(path).ToList();
            var agg = new StatsAggregator();
            var parser = new PokerStarsParser(agg);
            parser.FeedLines(lines, () => { });

            if (!agg.Players.TryGetValue(heroName, out var heroStats) || heroStats.HandsReceived == 0)
                return null;

            var (tableName, blindsLabel, bigBlind) = DetectTableInfo(path, lines);
            var lastPlayedAt = DetectLastPlayedAt(lines);
            var netAmount = EstimateHeroNet(lines, heroName);
            var netBb = bigBlind > 0 ? netAmount / bigBlind : 0;
            var isCash = blindsLabel.Contains('$');

            return new TableSessionStats(
                $"{tableName} ({blindsLabel})",
                DetectGameFormat(lines),
                lastPlayedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                lastPlayedAt,
                path,
                heroName,
                bigBlind,
                GetStakeFromBigBlind(bigBlind),
                heroStats.HandsReceived,
                heroStats.VPIPPct,
                heroStats.PFRPct,
                heroStats.ThreeBetPct,
                heroStats.AF,
                heroStats.AFqPct,
                heroStats.CBetFlopPct,
                heroStats.FoldVsCBetFlopPct,
                heroStats.WTSDPct,
                heroStats.WSDPct,
                heroStats.WWSFPct,
                netBb,
                netAmount,
                isCash,
                FormatNetAmount(netAmount, isCash));
        }

        private static (string TableName, string BlindsLabel, double BigBlind) DetectTableInfo(string path, IReadOnlyList<string> lines)
        {
            var tableName = Path.GetFileNameWithoutExtension(path);
            var blindsLabel = "?/?";
            double bigBlind = 1;

            foreach (var line in lines.Take(200))
            {
                var tableMatch = TableRx.Match(line);
                if (tableMatch.Success)
                    tableName = tableMatch.Groups["table"].Value.Trim();

                var blindsMatch = BlindsRx.Match(line);
                if (blindsMatch.Success)
                {
                    var sb = FormatBlind(blindsMatch.Groups["sb"].Value);
                    var bb = FormatBlind(blindsMatch.Groups["bb"].Value);
                    blindsLabel = $"{sb}/{bb}";
                    if (TryParseAmount(bb, out var parsedBb) && parsedBb > 0)
                        bigBlind = parsedBb;
                }

                if (tableName != Path.GetFileNameWithoutExtension(path) && blindsLabel != "?/?")
                    break;
            }

            return (tableName, blindsLabel, bigBlind);
        }

        private static StakeProfile GetStakeFromBigBlind(double bigBlind)
        {
            if (bigBlind <= 0.10) return StakeProfile.Low;
            if (bigBlind >= 2.00) return StakeProfile.High;
            return StakeProfile.Mid;
        }

        private static DateTime DetectLastPlayedAt(IReadOnlyList<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var match = HeaderTimestampRx.Match(lines[i]);
                if (!match.Success) continue;

                if (DateTime.TryParseExact(
                    match.Groups["stamp"].Value,
                    "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
                {
                    return timestamp;
                }
            }

            return DateTime.MinValue;
        }

        private static string DetectGameFormat(IReadOnlyList<string> lines)
        {
            var game = "Unknown";
            int? maxPlayers = null;
            var maxSeatSeen = 0;

            foreach (var line in lines.Take(200))
            {
                if (game == "Unknown")
                {
                    var gameMatch = HeaderGameRx.Match(line);
                    if (gameMatch.Success)
                        game = NormalizeGameName(gameMatch.Groups["game"].Value);
                }

                if (maxPlayers is null)
                {
                    var maxMatch = MaxTableRx.Match(line);
                    if (maxMatch.Success && int.TryParse(maxMatch.Groups["max"].Value, out var parsedMax))
                        maxPlayers = parsedMax;
                }

                var seatMatch = SeatRx.Match(line);
                if (seatMatch.Success && int.TryParse(seatMatch.Groups["seat"].Value, out var seat))
                    maxSeatSeen = Math.Max(maxSeatSeen, seat);
            }

            var tableSize = maxPlayers ?? (maxSeatSeen > 0 ? maxSeatSeen : 0);
            return tableSize > 0 ? $"{game}-{tableSize}Max" : game;
        }

        private static string NormalizeGameName(string raw)
        {
            raw = raw.Trim();

            if (raw.Contains("Hold'em", StringComparison.OrdinalIgnoreCase))
            {
                if (raw.Contains("No Limit", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("sin limite", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("sin limite", StringComparison.OrdinalIgnoreCase)) return "NLH";
                if (raw.Contains("Pot Limit", StringComparison.OrdinalIgnoreCase)) return "PLH";
                if (raw.Contains("Limit", StringComparison.OrdinalIgnoreCase)) return "LH";
                return "H";
            }

            if (raw.Contains("Omaha", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = raw.Contains("Hi/Lo", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("H/L", StringComparison.OrdinalIgnoreCase)
                        ? "8"
                        : "";

                if (raw.Contains("Pot Limit", StringComparison.OrdinalIgnoreCase)) return $"PLO{suffix}";
                if (raw.Contains("No Limit", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("sin limite", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("sin limite", StringComparison.OrdinalIgnoreCase)) return $"NLO{suffix}";
                if (raw.Contains("Limit", StringComparison.OrdinalIgnoreCase)) return $"LO{suffix}";
                return $"O{suffix}";
            }

            return raw;
        }

        private static double EstimateHeroNet(IReadOnlyList<string> lines, string heroName)
        {
            var total = 0.0;
            var hand = new List<string>();

            foreach (var line in lines)
            {
                if (HandStartRx.IsMatch(line) && hand.Count > 0)
                {
                    total += EstimateHeroNetForHand(hand, heroName);
                    hand.Clear();
                }

                hand.Add(line);
            }

            if (hand.Count > 0)
                total += EstimateHeroNetForHand(hand, heroName);

            return total;
        }

        private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
        {
            if (!hand.Any(line =>
            {
                var dealt = DealtToRx.Match(line);
                return dealt.Success && PokerStarsHandHistory.SamePlayer(dealt.Groups["hero"].Value, heroName);
            }))
            {
                return 0;
            }

            return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
        }

        private static bool TryParseAmount(string raw, out double value)
        {
            if (PokerAmountParser.TryParse(raw, out value))
                return true;

            raw = raw.Replace("$", "")
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\u00A0", "")
                .Replace(" ", "")
                .Replace(",", ".");
            return double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatBlind(string raw) =>
            PokerAmountParser.FormatBlind(raw);

        private static bool IsCashBlind(string raw) =>
            raw.Contains('$') ||
            raw.Contains("€", StringComparison.Ordinal) ||
            raw.Contains("US", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains('.') ||
            raw.Contains(',');

        private static string FormatNetAmount(double amount, bool isCash)
        {
            var sign = amount >= 0 ? "+" : "-";
            var absolute = Math.Abs(amount);

            return isCash
                ? $"{sign}${absolute.ToString("0.00", CultureInfo.InvariantCulture)}"
                : $"{sign}{absolute.ToString("0", CultureInfo.InvariantCulture)} fichas";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private sealed record DashboardResult(int FileCount, StakeProfile Stake, PlayerStats? Hero, List<TableSessionStats> Tables);

        public sealed record TableSessionStats(
            string TableName,
            string GameFormat,
            string PlayedDate,
            DateTime LastPlayedAt,
            string SourcePath,
            string HeroName,
            double BigBlind,
            StakeProfile Stake,
            int HandsReceived,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetFlopPct,
            double FoldVsCBetFlopPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            double NetBb,
            double NetAmount,
            bool IsCash,
            string NetAmountLabel)
        {
            public bool IsWin => NetBb >= 100;
            public bool IsProfitable => NetBb >= 0;
            public string TrendIcon => IsWin ? "\u25B2" : "\u25BC";
        }
    }
}


