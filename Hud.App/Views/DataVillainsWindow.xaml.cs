using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class DataVillainsWindow : Window
    {
        private static readonly Regex HandStartRx = PokerStarsHandHistory.HandStartRx;
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)", RegexOptions.Compiled);
        private static readonly Regex SeatRx =
            PokerStarsHandHistory.SeatRx;
        private static readonly Regex ActionNameRx =
            new(@"^(?<name>.+?):+\s+", RegexOptions.Compiled);
        private static readonly Regex DealtToRx =
            PokerStarsHandHistory.DealtRx;
        private static readonly Regex CollectedRx =
            PokerStarsHandHistory.CollectedRx;
        private static readonly Regex ReturnedRx =
            PokerStarsHandHistory.ReturnedRx;
        private static readonly Regex RaiseToRx =
            PokerStarsHandHistory.RaiseToRx;
        private static readonly Regex ActionAmountRx =
            PokerStarsHandHistory.ActionAmountRx;

        private readonly IReadOnlyList<MainWindow.TableSessionStats> _tables;
        private readonly List<DataVillainRow> _allVillains = new();
        private bool _filtersReady;

        public ObservableCollection<DataVillainRow> Villains { get; } = new();

        public DataVillainsWindow(IEnumerable<MainWindow.TableSessionStats> tables, string summary)
        {
            InitializeComponent();
            FitToWorkArea();
            _tables = tables.ToList();
            DataContext = this;
            SummaryText.Text = summary;

            LoadVillains();
            ConfigureFilters();
            ApplyFilters();
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            const double desiredWidth = 1660;

            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(desiredWidth, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        private void LoadVillains()
        {
            Villains.Clear();
            _allVillains.Clear();

            var rows = BuildHistory(_tables, out var sourceTables);
            _allVillains.AddRange(rows);
            VillainHistoryStore.Replace(_allVillains, sourceTables);
        }

        public static IReadOnlyList<DataVillainRow> BuildHistory(
            IEnumerable<MainWindow.TableSessionStats> tables,
            out IReadOnlyList<MainWindow.TableSessionStats> sourceTables)
        {
            var builtSourceTables = tables
                .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath) && File.Exists(table.SourcePath))
                .GroupBy(table => table.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(table => table.LastPlayedAt).First())
                .ToList();
            sourceTables = builtSourceTables;

            if (builtSourceTables.Count == 0)
                return Array.Empty<DataVillainRow>();

            var totalAgg = new StatsAggregator();
            var parser = new PokerStarsParser(totalAgg);

            foreach (var table in builtSourceTables)
                parser.FeedLines(File.ReadLines(table.SourcePath), () => { });

            var heroName = builtSourceTables
                .Select(table => table.HeroName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (string.IsNullOrWhiteSpace(heroName))
                return Array.Empty<DataVillainRow>();

            var villainData = new Dictionary<string, VillainAccumulator>(StringComparer.Ordinal);

            foreach (var table in builtSourceTables)
            {
                var lines = File.ReadAllLines(table.SourcePath);
                foreach (var hand in SplitHands(lines))
                {
                    if (!HandContainsHero(hand, heroName))
                        continue;

                    var players = ExtractPlayers(hand);
                    if (!players.Any(player => PokerStarsHandHistory.SamePlayer(player, heroName)))
                        continue;

                    var timestamp = ExtractTimestamp(hand) ?? table.LastPlayedAt;
                    var netBb = table.BigBlind > 0
                        ? EstimateHeroNetForHand(hand, heroName) / table.BigBlind
                        : 0;

                    foreach (var villain in players.Where(player => !PokerStarsHandHistory.SamePlayer(player, heroName)))
                    {
                        if (!HandHasPlayerAction(hand, villain))
                            continue;

                        if (!villainData.TryGetValue(villain, out var acc))
                        {
                            acc = new VillainAccumulator(villain);
                            villainData[villain] = acc;
                        }

                        acc.TotalHandsVsHero++;
                        acc.TotalNetBb += netBb;
                        acc.LastSeen = Max(acc.LastSeen, timestamp);

                        var session = acc.GetSession(table.SourcePath, table.TableName);
                        session.HandsVsHero++;
                        session.NetBb += netBb;
                        session.LastSeen = Max(session.LastSeen, timestamp);
                    }
                }
            }

            return villainData.Values
                .Select(acc => BuildRow(acc, totalAgg.Players, builtSourceTables))
                .Where(row => row is not null)
                .Cast<DataVillainRow>()
                .OrderByDescending(row => row.LastSeen)
                .ThenByDescending(row => row.SessionHandsVsHero)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ConfigureFilters()
        {
            ConfigureOptionCombo(DateFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Key("LAST_7", "Filter.Last7"),
                LocalizedOption.Key("LAST_30", "Filter.Last30"),
                LocalizedOption.Key("CURRENT_MONTH", "Filter.CurrentMonth")
            });
            ConfigureOptionCombo(MoneyTypeFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Raw("Cash"),
                LocalizedOption.Key("CHIPS", "Common.Chips")
            });
            ConfigureOptionCombo(HandsFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Raw("10+"),
                LocalizedOption.Raw("30+"),
                LocalizedOption.Raw("50+"),
                LocalizedOption.Raw("100+"),
                LocalizedOption.Raw("200+"),
                LocalizedOption.Raw("500+"),
                LocalizedOption.Raw("1000+"),
                LocalizedOption.Raw("5000+"),
                LocalizedOption.Raw("10000+")
            });
            ConfigureOptionCombo(ResultFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.AllMale"),
                LocalizedOption.Key("I_WIN", "Filter.IWin"),
                LocalizedOption.Key("THEY_WIN", "Filter.TheyWin"),
                LocalizedOption.Key("EVEN", "Filter.Even")
            });
            var bbOptions = new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Key("PLUS_50", "Filter.Plus50"),
                LocalizedOption.Key("PLUS_10_50", "Filter.Plus10To50"),
                LocalizedOption.Key("MINUS_10_50", "Filter.Minus10To50"),
                LocalizedOption.Key("MINUS_50", "Filter.Minus50")
            };
            ConfigureOptionCombo(SessionBbFilter, bbOptions);
            ConfigureOptionCombo(TotalBbFilter, bbOptions);
            ConfigureOptionCombo(SortFilter, new[]
            {
                LocalizedOption.Key("RECENT", "Filter.MostRecent"),
                LocalizedOption.Key("MOST_VS_HERO", "Filter.MostVsHero"),
                LocalizedOption.Key("BEST_VS_ME", "Filter.BestVsMe"),
                LocalizedOption.Key("WORST_VS_ME", "Filter.WorstVsMe"),
                LocalizedOption.Key("VPIP_HIGH", "Filter.HighVpip"),
                LocalizedOption.Key("THREEBET_HIGH", "Filter.High3Bet")
            });

            ConfigureOptionCombo(FormatFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_allVillains.Select(row => row.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());

            ConfigureOptionCombo(ProfileFilter, new[] { LocalizedOption.Key("ALL", "Common.AllMale") }
                .Concat(_allVillains.Select(row => row.Profile)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());

            DateFilter.SelectedIndex = 0;
            FormatFilter.SelectedIndex = 0;
            MoneyTypeFilter.SelectedIndex = 0;
            HandsFilter.SelectedIndex = 0;
            ResultFilter.SelectedIndex = 0;
            SessionBbFilter.SelectedIndex = 0;
            TotalBbFilter.SelectedIndex = 0;
            ProfileFilter.SelectedIndex = 0;
            SortFilter.SelectedIndex = 0;
            _filtersReady = true;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_filtersReady)
                ApplyFilters();
        }

        private void VillainsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VillainsGrid.SelectedItem is not DataVillainRow row)
                return;

            var window = new DataVillainDetailWindow(row, _tables)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            VillainsGrid.SelectedItem = null;
            window.Show();
        }

        private void ApplyFilters()
        {
            var query = _allVillains.AsEnumerable();
            var search = SearchBox.Text?.Trim();

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(row =>
                    row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.RecentTable.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            query = ApplyDateFilter(query, SelectedText(DateFilter));
            query = ApplyFormatFilter(query, SelectedText(FormatFilter));
            query = ApplyMoneyTypeFilter(query, SelectedText(MoneyTypeFilter));
            query = ApplyHandsFilter(query, SelectedText(HandsFilter));
            query = ApplyResultFilter(query, SelectedText(ResultFilter));
            query = ApplyBbFilter(query, SelectedText(SessionBbFilter), row => row.SessionNetBb);
            query = ApplyBbFilter(query, SelectedText(TotalBbFilter), row => row.TotalNetBb);
            query = ApplyProfileFilter(query, SelectedText(ProfileFilter));
            query = ApplySort(query, SelectedText(SortFilter));

            Villains.Clear();
            foreach (var row in query)
                Villains.Add(row);

            SummaryText.Text = $"{SummaryBaseText()} | {Hud.App.Services.LocalizationManager.Text("Common.TotalVillains")}: {Villains.Count}/{_allVillains.Count}";
        }

        private string SummaryBaseText()
        {
            var text = SummaryText.Text ?? "";
            var marker = $" | {Hud.App.Services.LocalizationManager.Text("Common.TotalVillains")}:";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? text[..index] : text;
        }

        private static IEnumerable<DataVillainRow> ApplyDateFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            var today = DateTime.Today;

            return filter switch
            {
                "LAST_7" => query.Where(row => row.LastSeen >= today.AddDays(-7)),
                "LAST_30" => query.Where(row => row.LastSeen >= today.AddDays(-30)),
                "CURRENT_MONTH" => query.Where(row => row.LastSeen.Year == today.Year && row.LastSeen.Month == today.Month),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyFormatFilter(IEnumerable<DataVillainRow> query, string filter) =>
            filter == "ALL"
                ? query
                : query.Where(row => string.Equals(row.GameFormat, filter, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<DataVillainRow> ApplyMoneyTypeFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "Cash" => query.Where(row => row.IsCash),
                "CHIPS" => query.Where(row => !row.IsCash),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyHandsFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            if (filter == "ALL")
                return query;

            return int.TryParse(filter.TrimEnd('+'), out var minimum)
                ? query.Where(row => row.TotalHands >= minimum)
                : query;
        }

        private static IEnumerable<DataVillainRow> ApplyResultFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "I_WIN" => query.Where(row => row.SessionNetBb > 0),
                "THEY_WIN" => query.Where(row => row.SessionNetBb < 0),
                "EVEN" => query.Where(row => Math.Abs(row.SessionNetBb) < 5),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyBbFilter(
            IEnumerable<DataVillainRow> query,
            string filter,
            Func<DataVillainRow, double> valueSelector)
        {
            return filter switch
            {
                "PLUS_50" => query.Where(row => valueSelector(row) >= 50),
                "PLUS_10_50" => query.Where(row => valueSelector(row) >= 10 && valueSelector(row) < 50),
                "MINUS_10_50" => query.Where(row => valueSelector(row) <= -10 && valueSelector(row) > -50),
                "MINUS_50" => query.Where(row => valueSelector(row) <= -50),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyProfileFilter(IEnumerable<DataVillainRow> query, string filter) =>
            filter == "ALL"
                ? query
                : query.Where(row => string.Equals(row.Profile, filter, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<DataVillainRow> ApplySort(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "MOST_VS_HERO" => query.OrderByDescending(row => row.TotalHandsVsHero)
                    .ThenByDescending(row => row.LastSeen),
                "BEST_VS_ME" => query.OrderByDescending(row => row.TotalNetBb)
                    .ThenByDescending(row => row.LastSeen),
                "WORST_VS_ME" => query.OrderBy(row => row.TotalNetBb)
                    .ThenByDescending(row => row.LastSeen),
                "VPIP_HIGH" => query.OrderByDescending(row => row.VPIPPct)
                    .ThenByDescending(row => row.TotalHands),
                "THREEBET_HIGH" => query.OrderByDescending(row => row.ThreeBetPct)
                    .ThenByDescending(row => row.TotalHands),
                _ => query.OrderByDescending(row => row.LastSeen)
                    .ThenByDescending(row => row.SessionHandsVsHero)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void ConfigureOptionCombo(ComboBox comboBox, IEnumerable<LocalizedOption> options)
        {
            comboBox.DisplayMemberPath = nameof(LocalizedOption.Label);
            comboBox.SelectedValuePath = nameof(LocalizedOption.Value);
            comboBox.ItemsSource = options.ToList();
        }

        private static string SelectedText(ComboBox comboBox) =>
            comboBox.SelectedValue?.ToString() ?? "ALL";

        private static DataVillainRow? BuildRow(
            VillainAccumulator acc,
            IReadOnlyDictionary<string, PlayerStats> players,
            IReadOnlyList<MainWindow.TableSessionStats> tables)
        {
            if (!players.TryGetValue(acc.Name, out var stats))
                return null;

            var recentSession = acc.Sessions.Values
                .OrderByDescending(session => session.LastSeen)
                .FirstOrDefault();
            if (recentSession is null)
                return null;

            var recentTable = tables
                .FirstOrDefault(table => string.Equals(table.SourcePath, recentSession.SourcePath, StringComparison.OrdinalIgnoreCase));
            var stake = recentTable?.Stake ?? StakeProfile.Low;

            return new DataVillainRow(
                acc.Name,
                recentSession.TableName,
                recentTable?.GameFormat ?? "",
                recentTable?.IsCash ?? false,
                recentSession.HandsVsHero,
                acc.TotalHandsVsHero,
                stats.HandsReceived,
                stats.VPIPPct,
                stats.PFRPct,
                stats.ThreeBetPct,
                stats.AF,
                stats.AFqPct,
                stats.CBetFlopPct,
                stats.FoldVsCBetFlopPct,
                stats.WTSDPct,
                stats.WSDPct,
                stats.WWSFPct,
                recentSession.NetBb,
                acc.TotalNetBb,
                stake,
                acc.LastSeen);
        }

        private static IEnumerable<IReadOnlyList<string>> SplitHands(IReadOnlyList<string> lines)
        {
            return PokerStarsHandHistory.SplitHands(lines);
        }

        private static bool HandContainsHero(IReadOnlyList<string> hand, string heroName) =>
            hand.Any(line =>
            {
                var dealt = DealtToRx.Match(line);
                return dealt.Success &&
                    PokerStarsHandHistory.SamePlayer(dealt.Groups["hero"].Value, heroName);
            });

        private static HashSet<string> ExtractPlayers(IReadOnlyList<string> hand)
        {
            var players = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in hand)
            {
                var seat = SeatRx.Match(line);
                if (seat.Success)
                    players.Add(PokerStarsHandHistory.NormalizeName(seat.Groups["name"].Value));
            }

            if (players.Count > 0)
                return players;

            foreach (var line in hand)
            {
                var action = ActionNameRx.Match(line);
                if (action.Success)
                    players.Add(PokerStarsHandHistory.NormalizeName(action.Groups["name"].Value));
            }

            return players;
        }

        private static bool HandHasPlayerAction(IReadOnlyList<string> hand, string playerName)
        {
            return PokerStarsHandHistory.HandHasPlayerActivity(hand, playerName);
        }

        private static DateTime? ExtractTimestamp(IReadOnlyList<string> hand)
        {
            foreach (var line in hand)
            {
                var match = HeaderTimestampRx.Match(line);
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

            return null;
        }

        private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
        {
            return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
        }

        private static bool TryParseAmount(string raw, out double value)
        {
            if (PokerAmountParser.TryParse(raw, out value))
                return true;

            raw = raw.Replace("$", "")
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\u00A0", "")
                .Replace(" ", "")
                .Replace(",", ".");
            return double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static DateTime Max(DateTime left, DateTime right) =>
            left >= right ? left : right;

        private sealed class VillainAccumulator
        {
            public VillainAccumulator(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public int TotalHandsVsHero { get; set; }
            public double TotalNetBb { get; set; }
            public DateTime LastSeen { get; set; } = DateTime.MinValue;
            public Dictionary<string, VillainSessionAccumulator> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);

            public VillainSessionAccumulator GetSession(string sourcePath, string tableName)
            {
                if (!Sessions.TryGetValue(sourcePath, out var session))
                {
                    session = new VillainSessionAccumulator(sourcePath, tableName);
                    Sessions[sourcePath] = session;
                }

                return session;
            }
        }

        private sealed class VillainSessionAccumulator
        {
            public VillainSessionAccumulator(string sourcePath, string tableName)
            {
                SourcePath = sourcePath;
                TableName = tableName;
            }

            public string SourcePath { get; }
            public string TableName { get; }
            public int HandsVsHero { get; set; }
            public double NetBb { get; set; }
            public DateTime LastSeen { get; set; } = DateTime.MinValue;
        }

        public sealed record DataVillainRow(
            string Name,
            string RecentTable,
            string GameFormat,
            bool IsCash,
            int SessionHandsVsHero,
            int TotalHandsVsHero,
            int TotalHands,
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
            double SessionNetBb,
            double TotalNetBb,
            StakeProfile Stake,
            DateTime LastSeen)
        {
            public string LastSeenLabel => LastSeen == DateTime.MinValue
                ? "-"
                : LastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            public string Profile => ClassifyProfile(TotalHands, VPIPPct, PFRPct, ThreeBetPct, AF);
            public string SessionTrendIcon => SessionNetBb >= 0 ? "\u25B2" : "\u25BC";
            public string TotalTrendIcon => TotalNetBb >= 0 ? "\u25B2" : "\u25BC";
            public Brush SessionTrendBrush => SessionNetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
            public Brush TotalTrendBrush => TotalNetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < 30) return LocalizationManager.Text("Tag.NoSample");
            if (vpip >= 40 && pfr <= 10 && af < 1.5) return "Fish";
            if (vpip >= 45 || af >= 5 || threeBet >= 15) return "Maniac";
                if (vpip >= 35 && pfr < 15) return LocalizationManager.Text("Tag.LoosePassive");
            if (vpip >= 28 && pfr >= 20) return "LAG";
            if (vpip >= 18 && vpip < 29 && pfr >= 13 && pfr < 24) return "TAG";
            if (vpip < 14 && pfr < 10) return "Nit";
            if (vpip < 22 && pfr < 15) return "Tight";
                if (af < 1.2) return LocalizationManager.Text("Tag.Passive");
                return LocalizationManager.Text("Tag.Regular");
        }
    }
}

