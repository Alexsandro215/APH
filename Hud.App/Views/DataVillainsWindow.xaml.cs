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
        private static readonly Regex HandStartRx = new(@"^PokerStars Hand #", RegexOptions.Compiled);
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)", RegexOptions.Compiled);
        private static readonly Regex SeatRx =
            new(@"^Seat\s+(?<seat>\d+):\s+(?<name>[^(\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActionNameRx =
            new(@"^(?<name>[^:]+):\s+", RegexOptions.Compiled);
        private static readonly Regex DealtToRx =
            new(@"^Dealt to\s+(?<hero>.+?)\s+\[", RegexOptions.Compiled);
        private static readonly Regex CollectedRx =
            new(@"^(?<name>[^:]+?)\s+(?:collected|recoge|cobra|cobro|se lleva el bote)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReturnedRx =
            new(@"^(?:Uncalled bet|Apuesta no pagada)\s+\(\$?(?<amount>[\d,.]+)\)\s+(?:returned to|devuelta a)\s+(?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RaiseToRx =
            new(@"(?:raises|sube)\s+\$?[\d,.]+\s+(?:to|hasta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActionAmountRx =
            new(@":\s+(?:posts (?:small blind|big blind|the ante)|pone ciega chica|pone ciega grande|calls|bets|paga|apuesta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            var sourceTables = _tables
                .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath) && File.Exists(table.SourcePath))
                .GroupBy(table => table.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(table => table.LastPlayedAt).First())
                .ToList();

            if (sourceTables.Count == 0)
                return;

            var totalAgg = new StatsAggregator();
            var parser = new PokerStarsParser(totalAgg);

            foreach (var table in sourceTables)
                parser.FeedLines(File.ReadLines(table.SourcePath), () => { });

            var heroName = sourceTables
                .Select(table => table.HeroName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (string.IsNullOrWhiteSpace(heroName))
                return;

            var villainData = new Dictionary<string, VillainAccumulator>(StringComparer.Ordinal);

            foreach (var table in sourceTables)
            {
                var lines = File.ReadAllLines(table.SourcePath);
                foreach (var hand in SplitHands(lines))
                {
                    if (!HandContainsHero(hand, heroName))
                        continue;

                    var players = ExtractPlayers(hand);
                    if (!players.Contains(heroName))
                        continue;

                    var timestamp = ExtractTimestamp(hand) ?? table.LastPlayedAt;
                    var netBb = table.BigBlind > 0
                        ? EstimateHeroNetForHand(hand, heroName) / table.BigBlind
                        : 0;

                    foreach (var villain in players.Where(player => !string.Equals(player, heroName, StringComparison.Ordinal)))
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

            var rows = villainData.Values
                .Select(acc => BuildRow(acc, totalAgg.Players, sourceTables))
                .Where(row => row is not null)
                .Cast<DataVillainRow>()
                .OrderByDescending(row => row.LastSeen)
                .ThenByDescending(row => row.SessionHandsVsHero)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _allVillains.AddRange(rows);
            VillainHistoryStore.Replace(_allVillains, sourceTables);
        }

        private void ConfigureFilters()
        {
            DateFilter.ItemsSource = new[] { "Todas", "Ultimos 7 dias", "Ultimos 30 dias", "Mes actual" };
            MoneyTypeFilter.ItemsSource = new[] { "Todas", "Cash", "Fichas" };
            HandsFilter.ItemsSource = new[] { "Todas", "10+", "30+", "50+", "100+", "200+", "500+", "1000+", "5000+", "10000+" };
            ResultFilter.ItemsSource = new[] { "Todos", "Les gano", "Me ganan", "Parejos" };
            SessionBbFilter.ItemsSource = new[] { "Todas", "+50bb o mas", "+10 a +50", "-10 a -50", "-50bb o peor" };
            TotalBbFilter.ItemsSource = new[] { "Todas", "+50bb o mas", "+10 a +50", "-10 a -50", "-50bb o peor" };
            SortFilter.ItemsSource = new[]
            {
                "Mas recientes",
                "Mas manos vs heroe",
                "Mayor ganancia vs mi",
                "Mayor perdida vs mi",
                "VPIP alto",
                "3Bet alto"
            };

            FormatFilter.ItemsSource = new[] { "Todas" }
                .Concat(_allVillains.Select(row => row.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();

            ProfileFilter.ItemsSource = new[] { "Todos" }
                .Concat(_allVillains.Select(row => row.Profile)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();

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

            SummaryText.Text = $"{SummaryBaseText()} | Villanos: {Villains.Count}/{_allVillains.Count}";
        }

        private string SummaryBaseText()
        {
            var text = SummaryText.Text ?? "";
            var marker = " | Villanos:";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? text[..index] : text;
        }

        private static IEnumerable<DataVillainRow> ApplyDateFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            var today = DateTime.Today;

            return filter switch
            {
                "Ultimos 7 dias" => query.Where(row => row.LastSeen >= today.AddDays(-7)),
                "Ultimos 30 dias" => query.Where(row => row.LastSeen >= today.AddDays(-30)),
                "Mes actual" => query.Where(row => row.LastSeen.Year == today.Year && row.LastSeen.Month == today.Month),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyFormatFilter(IEnumerable<DataVillainRow> query, string filter) =>
            filter == "Todas"
                ? query
                : query.Where(row => string.Equals(row.GameFormat, filter, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<DataVillainRow> ApplyMoneyTypeFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "Cash" => query.Where(row => row.IsCash),
                "Fichas" => query.Where(row => !row.IsCash),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyHandsFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            if (filter == "Todas")
                return query;

            return int.TryParse(filter.TrimEnd('+'), out var minimum)
                ? query.Where(row => row.TotalHands >= minimum)
                : query;
        }

        private static IEnumerable<DataVillainRow> ApplyResultFilter(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "Les gano" => query.Where(row => row.SessionNetBb > 0),
                "Me ganan" => query.Where(row => row.SessionNetBb < 0),
                "Parejos" => query.Where(row => Math.Abs(row.SessionNetBb) < 5),
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
                "+50bb o mas" => query.Where(row => valueSelector(row) >= 50),
                "+10 a +50" => query.Where(row => valueSelector(row) >= 10 && valueSelector(row) < 50),
                "-10 a -50" => query.Where(row => valueSelector(row) <= -10 && valueSelector(row) > -50),
                "-50bb o peor" => query.Where(row => valueSelector(row) <= -50),
                _ => query
            };
        }

        private static IEnumerable<DataVillainRow> ApplyProfileFilter(IEnumerable<DataVillainRow> query, string filter) =>
            filter == "Todos"
                ? query
                : query.Where(row => string.Equals(row.Profile, filter, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<DataVillainRow> ApplySort(IEnumerable<DataVillainRow> query, string filter)
        {
            return filter switch
            {
                "Mas manos vs heroe" => query.OrderByDescending(row => row.TotalHandsVsHero)
                    .ThenByDescending(row => row.LastSeen),
                "Mayor ganancia vs mi" => query.OrderByDescending(row => row.TotalNetBb)
                    .ThenByDescending(row => row.LastSeen),
                "Mayor perdida vs mi" => query.OrderBy(row => row.TotalNetBb)
                    .ThenByDescending(row => row.LastSeen),
                "VPIP alto" => query.OrderByDescending(row => row.VPIPPct)
                    .ThenByDescending(row => row.TotalHands),
                "3Bet alto" => query.OrderByDescending(row => row.ThreeBetPct)
                    .ThenByDescending(row => row.TotalHands),
                _ => query.OrderByDescending(row => row.LastSeen)
                    .ThenByDescending(row => row.SessionHandsVsHero)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string SelectedText(ComboBox comboBox) =>
            comboBox.SelectedItem?.ToString() ?? "Todas";

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
            var hand = new List<string>();

            foreach (var line in lines)
            {
                if (HandStartRx.IsMatch(line) && hand.Count > 0)
                {
                    yield return hand.ToList();
                    hand.Clear();
                }

                if (HandStartRx.IsMatch(line) || hand.Count > 0)
                    hand.Add(line);
            }

            if (hand.Count > 0)
                yield return hand;
        }

        private static bool HandContainsHero(IReadOnlyList<string> hand, string heroName) =>
            hand.Any(line =>
            {
                var dealt = DealtToRx.Match(line);
                return dealt.Success &&
                    string.Equals(dealt.Groups["hero"].Value.Trim(), heroName, StringComparison.Ordinal);
            });

        private static HashSet<string> ExtractPlayers(IReadOnlyList<string> hand)
        {
            var players = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in hand)
            {
                var seat = SeatRx.Match(line);
                if (seat.Success)
                    players.Add(seat.Groups["name"].Value.Trim());
            }

            if (players.Count > 0)
                return players;

            foreach (var line in hand)
            {
                var action = ActionNameRx.Match(line);
                if (action.Success)
                    players.Add(action.Groups["name"].Value.Trim());
            }

            return players;
        }

        private static bool HandHasPlayerAction(IReadOnlyList<string> hand, string playerName)
        {
            var prefix = playerName + ":";
            return hand.Any(line =>
                line.StartsWith(prefix, StringComparison.Ordinal) ||
                CollectedRx.Match(line) is { Success: true } collected &&
                string.Equals(collected.Groups["name"].Value.Trim(), playerName, StringComparison.Ordinal) ||
                ReturnedRx.Match(line) is { Success: true } returned &&
                string.Equals(returned.Groups["name"].Value.Trim(), playerName, StringComparison.Ordinal));
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
            var net = 0.0;
            var committedThisStreet = 0.0;
            var heroPrefix = heroName + ":";

            foreach (var line in hand)
            {
                if (line.StartsWith("*** FLOP", StringComparison.Ordinal) ||
                    line.StartsWith("*** TURN", StringComparison.Ordinal) ||
                    line.StartsWith("*** RIVER", StringComparison.Ordinal) ||
                    line.StartsWith("*** SHOW DOWN", StringComparison.Ordinal))
                {
                    committedThisStreet = 0;
                }

                var returned = ReturnedRx.Match(line);
                if (returned.Success &&
                    string.Equals(returned.Groups["name"].Value.Trim(), heroName, StringComparison.Ordinal) &&
                    TryParseAmount(returned.Groups["amount"].Value, out var returnedAmount))
                {
                    net += returnedAmount;
                    continue;
                }

                var collected = CollectedRx.Match(line);
                if (collected.Success &&
                    string.Equals(collected.Groups["name"].Value.Trim(), heroName, StringComparison.Ordinal) &&
                    TryParseAmount(collected.Groups["amount"].Value, out var collectedAmount))
                {
                    net += collectedAmount;
                    continue;
                }

                if (!line.StartsWith(heroPrefix, StringComparison.Ordinal))
                    continue;

                var raise = RaiseToRx.Match(line);
                if (raise.Success && TryParseAmount(raise.Groups["amount"].Value, out var raiseTo))
                {
                    var delta = Math.Max(0, raiseTo - committedThisStreet);
                    committedThisStreet += delta;
                    net -= delta;
                    continue;
                }

                var action = ActionAmountRx.Match(line);
                if (action.Success && TryParseAmount(action.Groups["amount"].Value, out var amount))
                {
                    committedThisStreet += amount;
                    net -= amount;
                }
            }

            return net;
        }

        private static bool TryParseAmount(string raw, out double value)
        {
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
            public string SessionTrendIcon => SessionNetBb >= 0 ? "▲" : "▼";
            public string TotalTrendIcon => TotalNetBb >= 0 ? "▲" : "▼";
            public Brush SessionTrendBrush => SessionNetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
            public Brush TotalTrendBrush => TotalNetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < 30) return "Sin muestra";
            if (vpip >= 40 && pfr <= 10 && af < 1.5) return "Fish";
            if (vpip >= 45 || af >= 5 || threeBet >= 15) return "Maniac";
            if (vpip >= 35 && pfr < 15) return "Loose pasivo";
            if (vpip >= 28 && pfr >= 20) return "LAG";
            if (vpip >= 18 && vpip < 29 && pfr >= 13 && pfr < 24) return "TAG";
            if (vpip < 14 && pfr < 10) return "Nit";
            if (vpip < 22 && pfr < 15) return "Tight";
            if (af < 1.2) return "Pasivo";
            return "Regular";
        }
    }
}
