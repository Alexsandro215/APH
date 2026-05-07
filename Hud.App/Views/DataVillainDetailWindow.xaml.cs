using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hud.App.Services;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;

namespace Hud.App.Views
{
    public partial class DataVillainDetailWindow : Window
    {
        private static readonly string[] Ranks = { "A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2" };
        private static readonly Regex HandStartRx = new(@"^PokerStars Hand #", RegexOptions.Compiled);
        private static readonly Regex HeaderTimestampRx =
            new(@"-\s*(?<stamp>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]+)", RegexOptions.Compiled);
        private static readonly Regex SeatRx =
            new(@"^(?:Seat|Asiento)\s+(?<seat>\d+):\s+(?<name>[^(\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ButtonRx =
            new(@"(?:Seat|Asiento)\s+#?(?<seat>\d+)\s+(?:is the button|es el bot[oó]n)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActorRx =
            new(@"^(?<actor>[^:]+):\s+(?<action>.+)$", RegexOptions.Compiled);
        private static readonly Regex ShowCardsRx =
            new(@"^(?<name>[^:]+):\s+(?:shows|muestra)\s+\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SummaryShownRx =
            new(@"^Seat\s+\d+:\s+(?<name>\S+).*\[(?<cards>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BoardCardsRx =
            new(@"\[(?<cards>[^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex CollectedRx =
            new(@"^(?<name>[^:]+?)\s+(?:collected|recoge|cobra|cobro|se lleva el bote)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ReturnedRx =
            new(@"^(?:Uncalled bet|Apuesta no pagada)\s+\(\$?(?<amount>[\d,.]+)\)\s+(?:returned to|devuelta a)\s+(?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RaiseToRx =
            new(@"(?:raises|sube)\s+\$?[\d,.]+\s+(?:to|hasta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ActionAmountRx =
            new(@":\s+(?:posts (?:small blind|big blind|the ante)|pone ciega chica|pone ciega grande|calls|bets|paga|apuesta)\s+\$?(?<amount>[\d,.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly DataVillainViewModel _viewModel;
        private readonly IReadOnlyList<MainWindow.TableSessionStats> _tables;

        private static readonly VillainAction[] ActionDisplayOrder =
        {
            VillainAction.Fold,
            VillainAction.Check,
            VillainAction.Call,
            VillainAction.Bet,
            VillainAction.Raise,
            VillainAction.ThreeBet,
            VillainAction.FourBetPlus,
            VillainAction.AllIn
        };

        public DataVillainDetailWindow(
            DataVillainsWindow.DataVillainRow villain,
            IEnumerable<MainWindow.TableSessionStats> tables)
        {
            InitializeComponent();
            FitToWorkArea();
            _tables = tables.ToList();
            _viewModel = DataVillainViewModel.Build(villain, _tables);
            DataContext = _viewModel;
            Title = $"APH - {villain.Name}";
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(1460, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        private void HandSummaryGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid || grid.SelectedItem is not HandSummaryRow row)
                return;

            var table = _tables.FirstOrDefault(t =>
                string.Equals(t.SourcePath, row.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (table is null || row.HandNumber <= 0)
                return;

            var window = new TableDetailWindow(table, row.HandNumber, _viewModel.VillainName)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private void RangeCell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: RangeCell cell })
                return;

            SelectedRangeHandText.Text = cell.HandCode;
            SelectedRangeStreetText.Text = StreetLabel(cell.Street);
            SelectedRangeActionSummaryList.ItemsSource = cell.SummaryItems;
            ExactHandsTitle.Text = cell.Count == 0
                ? $"{cell.HandCode} | {StreetLabel(cell.Street)} | Sin manos exactas"
                : $"{cell.HandCode} | {StreetLabel(cell.Street)} | Manos exactas";
            ExactHandsGrid.ItemsSource = cell.ExactHands;
        }

        private void ExactHandsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ExactHandsGrid.SelectedItem is not ExactVillainHandRow row)
                return;

            var table = _tables.FirstOrDefault(t =>
                string.Equals(t.SourcePath, row.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (table is null || row.HandNumber <= 0)
                return;

            var window = new TableDetailWindow(table, row.HandNumber, _viewModel.VillainName)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private void ProfitColorToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
                return;

            _viewModel.ColorRangeByProfit = ProfitColorToggle.IsChecked == true;
        }

        private void SummaryModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
                return;

            _viewModel.ShowAllDataSummary = SummaryModeToggle.IsChecked == true;
        }

        private void BtnDictionary_Click(object sender, RoutedEventArgs e)
        {
            var window = new DataVillainDictionaryWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private sealed class DataVillainViewModel : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly DataVillainsWindow.DataVillainRow _villain;
            private readonly IReadOnlyList<KnownVillainHand> _knownHands;
            private bool _colorRangeByProfit;
            private bool _showAllDataSummary;

            private DataVillainViewModel(
                DataVillainsWindow.DataVillainRow villain,
                IReadOnlyList<KnownVillainHand> knownHands,
                PlayerStats? heroStats,
                PlayerStats? villainStats)
            {
                _villain = villain;
                _knownHands = knownHands;
                VillainName = villain.Name;
                RecentTable = string.IsNullOrWhiteSpace(villain.RecentTable) ? "-" : villain.RecentTable;
                Profile = villain.Profile;
                GameFormat = string.IsNullOrWhiteSpace(villain.GameFormat) ? "-" : villain.GameFormat;
                MoneyType = villain.IsCash ? "Cash" : "Fichas";
                TotalHandsVsHero = villain.TotalHandsVsHero;
                KnownHandsCount = knownHands.Select(hand => hand.HandIdentity).Distinct(StringComparer.Ordinal).Count();
                TotalNetBbLabel = $"{villain.TotalNetBb:+0.#;-0.#;0} bb";
                TotalNetBrush = villain.TotalNetBb >= 0 ? BrushFrom(33, 192, 122) : BrushFrom(226, 78, 91);
                Subtitle = $"Muestra conocida: {KnownHandsCount} manos reveladas | Total vs heroe: {TotalHandsVsHero} manos";

                Tags = BuildTags(villain, knownHands).ToList();
                ComparisonRows = BuildComparisonRows(villain, heroStats, villainStats).ToList();
                PreflopCells = BuildRangeCells("PREFLOP", knownHands).ToList();
                FlopCells = BuildRangeCells("FLOP", knownHands).ToList();
                TurnCells = BuildRangeCells("TURN", knownHands).ToList();
                RiverCells = BuildRangeCells("RIVER", knownHands).ToList();
                BestHands = BuildHandSummaries(knownHands, descending: true).ToList();
                WorstHands = BuildHandSummaries(knownHands, descending: false).ToList();
            }

            public string VillainName { get; }
            public string RecentTable { get; }
            public string Subtitle { get; }
            public string Profile { get; }
            public string GameFormat { get; }
            public string MoneyType { get; }
            public int TotalHandsVsHero { get; }
            public int KnownHandsCount { get; }
            public string TotalNetBbLabel { get; }
            public Brush TotalNetBrush { get; }
            public string SummaryTitle => ShowAllDataSummary ? "ALL DATA" : "RESUMEN DE MUESTRA";
            public string SummaryHandsLabel => ShowAllDataSummary ? "Manos totales villano" : "Manos vs Heroe";
            public string SummaryHandsValue => ShowAllDataSummary
                ? _villain.TotalHands.ToString(CultureInfo.InvariantCulture)
                : TotalHandsVsHero.ToString(CultureInfo.InvariantCulture);
            public string SummaryKnownLabel => ShowAllDataSummary ? "Manos vs Heroe" : "Cartas conocidas";
            public string SummaryKnownValue => ShowAllDataSummary
                ? TotalHandsVsHero.ToString(CultureInfo.InvariantCulture)
                : KnownHandsCount.ToString(CultureInfo.InvariantCulture);
            public string SummaryResultLabel => "Mi resultado vs villano";
            public IReadOnlyList<TagViewModel> Tags { get; }
            public IReadOnlyList<ComparisonRow> ComparisonRows { get; }
            public IReadOnlyList<RangeCell> PreflopCells { get; }
            public IReadOnlyList<RangeCell> FlopCells { get; }
            public IReadOnlyList<RangeCell> TurnCells { get; }
            public IReadOnlyList<RangeCell> RiverCells { get; }
            public IReadOnlyList<HandSummaryRow> BestHands { get; }
            public IReadOnlyList<HandSummaryRow> WorstHands { get; }

            public bool ColorRangeByProfit
            {
                get => _colorRangeByProfit;
                set
                {
                    if (_colorRangeByProfit == value)
                        return;

                    _colorRangeByProfit = value;
                    foreach (var cell in PreflopCells.Concat(FlopCells).Concat(TurnCells).Concat(RiverCells))
                        cell.ColorByProfit = value;
                    OnPropertyChanged(nameof(ColorRangeByProfit));
                }
            }

            public bool ShowAllDataSummary
            {
                get => _showAllDataSummary;
                set
                {
                    if (_showAllDataSummary == value)
                        return;

                    _showAllDataSummary = value;
                    OnPropertyChanged(nameof(ShowAllDataSummary));
                    OnPropertyChanged(nameof(SummaryTitle));
                    OnPropertyChanged(nameof(SummaryHandsLabel));
                    OnPropertyChanged(nameof(SummaryHandsValue));
                    OnPropertyChanged(nameof(SummaryKnownLabel));
                    OnPropertyChanged(nameof(SummaryKnownValue));
                    OnPropertyChanged(nameof(SummaryResultLabel));
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

            public static DataVillainViewModel Build(
                DataVillainsWindow.DataVillainRow villain,
                IReadOnlyList<MainWindow.TableSessionStats> tables)
            {
                var files = tables
                    .Where(table => File.Exists(table.SourcePath))
                    .GroupBy(table => table.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(table => table.LastPlayedAt).First())
                    .ToList();

                var agg = new StatsAggregator();
                var parser = new PokerStarsParser(agg);
                foreach (var table in files)
                    parser.FeedLines(File.ReadLines(table.SourcePath), () => { });

                var heroName = files.Select(table => table.HeroName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";
                agg.Players.TryGetValue(heroName, out var heroStats);
                agg.Players.TryGetValue(villain.Name, out var villainStats);

                var knownHands = LoadKnownHands(villain.Name, files).ToList();
                return new DataVillainViewModel(villain, knownHands, heroStats, villainStats);
            }

            private static IEnumerable<ComparisonRow> BuildComparisonRows(
                DataVillainsWindow.DataVillainRow villain,
                PlayerStats? heroStats,
                PlayerStats? villainStats)
            {
                if (heroStats is not null)
                    yield return ComparisonRow.FromStats("Heroe", heroStats, villain.TotalNetBb, villain.Stake);

                if (villainStats is not null)
                    yield return ComparisonRow.FromStats(villain.Name, villainStats, -villain.TotalNetBb, villain.Stake);
            }

            private static IEnumerable<TagViewModel> BuildTags(
                DataVillainsWindow.DataVillainRow villain,
                IReadOnlyList<KnownVillainHand> knownHands)
            {
                yield return TagViewModel.Neutral(villain.Profile, "Perfil base segun VPIP/PFR/3Bet/AF.");

                if (villain.TotalHands < 30)
                    yield return TagViewModel.Neutral("Sin muestra", "Menos de 30 manos totales.");
                if (villain.TotalHandsVsHero >= 50)
                    yield return TagViewModel.Neutral("Rival frecuente", $"{villain.TotalHandsVsHero} manos contra el heroe.");
                if (villain.TotalNetBb > 50)
                    yield return TagViewModel.Positive("Pierde vs Hero", $"{villain.TotalNetBb:0.#} bb a favor del heroe en manos compartidas.");
                if (villain.TotalNetBb < -50)
                    yield return TagViewModel.Negative("Gana vs Hero", $"{Math.Abs(villain.TotalNetBb):0.#} bb a favor del villano en manos compartidas.");
                if (villain.VPIPPct >= 35)
                    yield return TagViewModel.Negative("Juega muchas manos", $"VPIP {villain.VPIPPct:0.#}%.");
                if (villain.AF >= 4 || villain.AFqPct >= 65)
                    yield return TagViewModel.Negative("Agresor", $"AF {villain.AF:0.#} | AFq {villain.AFqPct:0.#}%.");
                if (villain.ThreeBetPct >= 10)
                    yield return TagViewModel.Negative("3Bet alto", $"3Bet {villain.ThreeBetPct:0.#}%.");
                if (villain.FoldVsCBetFlopPct >= 65)
                    yield return TagViewModel.Positive("Foldea mucho a CBet", $"FvCB {villain.FoldVsCBetFlopPct:0.#}%.");
                if (villain.FoldVsCBetFlopPct > 0 && villain.FoldVsCBetFlopPct <= 30)
                    yield return TagViewModel.Negative("No foldea CBet", $"FvCB {villain.FoldVsCBetFlopPct:0.#}%.");
                if (villain.WTSDPct >= 35)
                    yield return TagViewModel.Negative("Va mucho a showdown", $"WTSD {villain.WTSDPct:0.#}%.");
                if (villain.VPIPPct >= 30 && villain.PFRPct < 12 && villain.AF < 1.5)
                    yield return TagViewModel.Positive("Calling station", "VPIP alto, PFR bajo y agresion baja.");
                if (villain.VPIPPct < 14 && villain.PFRPct < 10 && villain.TotalHands >= 50)
                    yield return TagViewModel.Neutral("Roca", "Rango cerrado con muestra suficiente.");

                var knownUnique = knownHands
                    .GroupBy(hand => hand.HandIdentity, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                if (knownUnique.Count == 0)
                    yield break;

                var premium = knownUnique.Count(hand => IsPremium(hand.HandCode));
                var low = knownUnique.Count(hand => IsLowHand(hand.HandCode));
                var suitedConnectors = knownUnique.Count(hand => IsSuitedConnector(hand.HandCode));
                var categories = new[]
                {
                    premium > 0,
                    low > 0,
                    suitedConnectors > 0,
                    knownUnique.Any(hand => hand.HandCode.EndsWith("o", StringComparison.Ordinal)),
                    knownUnique.Any(hand => hand.HandCode.EndsWith("s", StringComparison.Ordinal))
                }.Count(value => value);

                if (premium >= 3 && premium * 100.0 / knownUnique.Count >= 30)
                    yield return TagViewModel.Neutral($"Amante premium - {premium}/{knownUnique.Count}", "Muchas cartas conocidas son rango premium.");
                if (low >= 3 && low * 100.0 / knownUnique.Count >= 25)
                    yield return TagViewModel.Neutral($"Manos bajas - {low}/{knownUnique.Count}", "Muestra tendencia a mostrar/jugar manos bajas.");
                if (suitedConnectors >= 3 && suitedConnectors * 100.0 / knownUnique.Count >= 20)
                    yield return TagViewModel.Neutral($"Suited connectors - {suitedConnectors}/{knownUnique.Count}", "Muestra suited connectors con frecuencia.");
                if (categories >= 4 && knownUnique.Count >= 10)
                    yield return TagViewModel.Neutral("Mixto", "Muestra variedad amplia de categorias conocidas.");

                var trapSignals = knownHands.Count(hand => hand.Street is "TURN" or "RIVER" && IsPremium(hand.HandCode) && hand.Action is VillainAction.Raise or VillainAction.AllIn);
                if (trapSignals >= 2)
                    yield return TagViewModel.Negative($"Trampero - {trapSignals}", "Manos fuertes conocidas con agresion tardia.");

                var allIns = knownHands.Where(hand => hand.Action == VillainAction.AllIn).ToList();
                var strongAllIns = allIns.Count(hand => IsPremium(hand.HandCode) || IsPair(hand.HandCode) || IsSuitedConnector(hand.HandCode));
                if (allIns.Count >= 3 && strongAllIns * 100.0 / allIns.Count >= 60)
                    yield return TagViewModel.Negative($"All-in equity - {strongAllIns}/{allIns.Count}", "All-ins conocidos con rangos fuertes o conectados.");
                foreach (var tag in BuildWinningPatternTags(knownUnique))
                    yield return tag;
            }

            private static IEnumerable<TagViewModel> BuildWinningPatternTags(IReadOnlyList<KnownVillainHand> knownUnique)
            {
                var winning = knownUnique
                    .Where(hand => hand.NetBb > 0 && !string.IsNullOrWhiteSpace(hand.BoardCards))
                    .ToList();
                if (winning.Count == 0)
                    yield break;

                foreach (var tag in WinningPatternTag("Color lover", "Gana muchas bb conectando color.", winning, IsFlushWin))
                    yield return tag;
                foreach (var tag in WinningPatternTag("Set lover", "Gana muchas bb ligando set con par en mano.", winning, IsSetWin))
                    yield return tag;
                foreach (var tag in WinningPatternTag("Trips lover", "Gana muchas bb conectando trips con una carta en mano y par en mesa.", winning, IsTripsWin))
                    yield return tag;
                foreach (var tag in WinningPatternTag("Double par", "Gana muchas bb conectando doble par.", winning, IsTwoPairWin))
                    yield return tag;
                foreach (var tag in WinningPatternTag("Par alto", "Gana muchas bb con par alto.", winning, IsHighPairWin))
                    yield return tag;
                foreach (var tag in WinningPatternTag("Escalera lover", "Gana muchas bb conectando escalera.", winning, IsStraightWin))
                    yield return tag;
            }

            private static IEnumerable<TagViewModel> WinningPatternTag(
                string label,
                string reason,
                IReadOnlyList<KnownVillainHand> winning,
                Func<KnownVillainHand, bool> predicate)
            {
                var matches = winning.Where(predicate).ToList();
                if (matches.Count == 0)
                    yield break;

                var totalBb = matches.Sum(hand => hand.NetBb);
                if (matches.Count < 2 && totalBb < 20)
                    yield break;

                yield return TagViewModel.Negative(
                    $"{label} - {matches.Count}/{winning.Count}",
                    $"{reason} Muestra: {totalBb:+0.#;-0.#;0} bb en {matches.Count} manos ganadas conocidas.");
            }

            private static IEnumerable<KnownVillainHand> LoadKnownHands(
                string villainName,
                IReadOnlyList<MainWindow.TableSessionStats> tables)
            {
                foreach (var table in tables)
                {
                    var handNumber = 0;
                    foreach (var hand in SplitHands(File.ReadLines(table.SourcePath)))
                    {
                        handNumber++;
                        if (!TryGetKnownCards(hand, villainName, out var exactCards))
                            continue;

                        var handCode = NormalizeHand(exactCards);
                        var position = BuildPositionMap(hand).TryGetValue(villainName, out var pos)
                            ? pos
                            : InferPositionFromActions(hand, villainName);
                        var playedAt = ExtractTimestamp(hand) ?? table.LastPlayedAt;
                        var netBb = table.BigBlind > 0 ? EstimateNetForPlayer(hand, villainName) / table.BigBlind : 0;
                        var handIdentity = $"{table.SourcePath}:{handNumber}";
                        var finalBoardCards = ExtractBoardCardsForStreet(hand, "RIVER");

                        foreach (var street in new[] { "PREFLOP", "FLOP", "TURN", "RIVER" })
                        {
                            var action = DetectAction(hand, villainName, street);
                            if (action is null)
                                continue;

                            yield return new KnownVillainHand(
                                handIdentity,
                                table.TableName,
                                table.SourcePath,
                                handNumber,
                                street,
                                handCode,
                                exactCards,
                                position,
                                action.Value,
                                netBb,
                                finalBoardCards,
                                ExtractBoardCardsForStreet(hand, street),
                                playedAt);
                        }
                    }
                }
            }

            private static IReadOnlyList<RangeCell> BuildRangeCells(string street, IReadOnlyList<KnownVillainHand> hands)
            {
                var grouped = hands
                    .Where(hand => hand.Street == street)
                    .GroupBy(hand => hand.HandCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

                var result = new List<RangeCell>(169);
                for (var row = 0; row < Ranks.Length; row++)
                {
                    for (var col = 0; col < Ranks.Length; col++)
                    {
                        var handCode = HandCode(row, col);
                        result.Add(grouped.TryGetValue(handCode, out var examples)
                            ? RangeCell.FromExamples(street, handCode, examples)
                            : RangeCell.Empty(street, handCode));
                    }
                }

                return result;
            }

            private static IEnumerable<HandSummaryRow> BuildHandSummaries(IReadOnlyList<KnownVillainHand> hands, bool descending)
            {
                var uniqueHands = hands
                    .GroupBy(hand => hand.HandIdentity, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                var query = uniqueHands
                    .GroupBy(hand => hand.HandCode, StringComparer.OrdinalIgnoreCase)
                    .Select(group => HandSummaryRow.FromGroup(group.Key, group.ToList(), descending));

                query = descending
                    ? query.OrderByDescending(row => row.TotalBb).ThenByDescending(row => row.Count)
                    : query.OrderBy(row => row.TotalBb).ThenByDescending(row => row.Count);

                return query.Take(10);
            }
        }

        private static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
        {
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (HandStartRx.IsMatch(line) && current.Count > 0)
                {
                    yield return current.ToList();
                    current.Clear();
                }

                if (HandStartRx.IsMatch(line) || current.Count > 0)
                    current.Add(line);
            }

            if (current.Count > 0)
                yield return current;
        }

        private static bool TryGetKnownCards(IReadOnlyList<string> hand, string villainName, out string cards)
        {
            foreach (var line in hand)
            {
                var show = ShowCardsRx.Match(line);
                if (show.Success && string.Equals(show.Groups["name"].Value.Trim(), villainName, StringComparison.Ordinal))
                {
                    cards = show.Groups["cards"].Value.Trim();
                    return true;
                }

                var summary = SummaryShownRx.Match(line);
                if (summary.Success && string.Equals(summary.Groups["name"].Value.Trim(), villainName, StringComparison.Ordinal))
                {
                    cards = summary.Groups["cards"].Value.Trim();
                    return true;
                }
            }

            cards = "";
            return false;
        }

        private static string ExtractBoardCardsForStreet(IReadOnlyList<string> hand, string targetStreet)
        {
            if (targetStreet == "PREFLOP")
                return "";

            var cards = new List<string>();
            foreach (var line in hand)
            {
                var street = StreetFromBoardLine(line);
                if (street is null)
                    continue;

                cards = ExtractBoardStateFromLine(line);

                if (street == targetStreet)
                    return string.Join(" ", cards);
            }

            return string.Join(" ", cards);
        }

        private static string? StreetFromBoardLine(string line)
        {
            if (line.StartsWith("*** FLOP ***", StringComparison.Ordinal))
                return "FLOP";
            if (line.StartsWith("*** TURN ***", StringComparison.Ordinal))
                return "TURN";
            if (line.StartsWith("*** RIVER ***", StringComparison.Ordinal))
                return "RIVER";

            return null;
        }

        private static List<string> ExtractBoardStateFromLine(string line)
        {
            var matches = BoardCardsRx.Matches(line).Cast<Match>().ToList();
            if (matches.Count == 0)
                return new List<string>();

            var cards = matches[0].Groups["cards"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (matches.Count > 1)
            {
                cards.AddRange(matches[^1].Groups["cards"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            return cards;
        }

        private static VillainAction? DetectAction(IReadOnlyList<string> hand, string playerName, string street)
        {
            var raisesBeforePlayer = 0;
            foreach (var line in GetStreetLines(hand, street))
            {
                var match = ActorRx.Match(line);
                if (!match.Success)
                    continue;

                var actor = match.Groups["actor"].Value.Trim();
                var action = match.Groups["action"].Value.Trim();
                var isRaise = action.StartsWith("raises ", StringComparison.OrdinalIgnoreCase) ||
                    action.StartsWith("sube ", StringComparison.OrdinalIgnoreCase);

                if (string.Equals(actor, playerName, StringComparison.Ordinal))
                {
                    if (action.Contains("all-in", StringComparison.OrdinalIgnoreCase))
                        return VillainAction.AllIn;
                    if (action.StartsWith("folds", StringComparison.OrdinalIgnoreCase) || action.StartsWith("se retira", StringComparison.OrdinalIgnoreCase))
                        return VillainAction.Fold;
                    if (action.StartsWith("checks", StringComparison.OrdinalIgnoreCase) || action.StartsWith("pasa", StringComparison.OrdinalIgnoreCase))
                        return VillainAction.Check;
                    if (action.StartsWith("calls", StringComparison.OrdinalIgnoreCase) || action.StartsWith("paga", StringComparison.OrdinalIgnoreCase))
                        return VillainAction.Call;
                    if (action.StartsWith("bets", StringComparison.OrdinalIgnoreCase) || action.StartsWith("apuesta", StringComparison.OrdinalIgnoreCase))
                        return VillainAction.Bet;
                    if (isRaise)
                    {
                        if (street == "PREFLOP")
                            return raisesBeforePlayer switch
                            {
                                0 => VillainAction.Raise,
                                1 => VillainAction.ThreeBet,
                                _ => VillainAction.FourBetPlus
                            };

                        return VillainAction.Raise;
                    }
                }

                if (isRaise)
                    raisesBeforePlayer++;
            }

            return null;
        }

        private static IEnumerable<string> GetStreetLines(IReadOnlyList<string> hand, string street)
        {
            var start = street == "PREFLOP" ? 0 : FindStreetIndex(hand, street);
            if (start < 0)
                return Enumerable.Empty<string>();

            var end = hand.Count;
            foreach (var next in street switch
            {
                "PREFLOP" => new[] { "FLOP", "TURN", "RIVER", "SUMMARY" },
                "FLOP" => new[] { "TURN", "RIVER", "SUMMARY" },
                "TURN" => new[] { "RIVER", "SUMMARY" },
                _ => new[] { "SUMMARY" }
            })
            {
                var idx = FindStreetIndex(hand, next, start + 1);
                if (idx >= 0)
                {
                    end = idx;
                    break;
                }
            }

            return hand.Skip(start).Take(end - start);
        }

        private static int FindStreetIndex(IReadOnlyList<string> hand, string street, int startAt = 0)
        {
            for (var i = startAt; i < hand.Count; i++)
            {
                var line = hand[i];
                if (street == "SUMMARY" && line.StartsWith("*** SUMMARY ***", StringComparison.Ordinal))
                    return i;
                if (line.StartsWith($"*** {street} ***", StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static Dictionary<string, string> BuildPositionMap(IReadOnlyList<string> hand)
        {
            var seats = new SortedDictionary<int, string>();
            var buttonSeat = 0;

            foreach (var line in hand)
            {
                var button = ButtonRx.Match(line);
                if (button.Success)
                    int.TryParse(button.Groups["seat"].Value, out buttonSeat);

                var seat = SeatRx.Match(line);
                if (seat.Success && int.TryParse(seat.Groups["seat"].Value, out var seatNo))
                    seats[seatNo] = seat.Groups["name"].Value.Trim();
            }

            if (buttonSeat == 0 || seats.Count == 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var orderedSeats = seats.Keys.OrderBy(seat => seat).ToList();
            var buttonIndex = orderedSeats.IndexOf(buttonSeat);
            if (buttonIndex < 0)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < orderedSeats.Count; i++)
            {
                var offset = (i - buttonIndex + orderedSeats.Count) % orderedSeats.Count;
                result[seats[orderedSeats[i]]] = PositionFromOffset(offset, orderedSeats.Count);
            }

            return result;
        }

        private static string InferPositionFromActions(IReadOnlyList<string> hand, string playerName)
        {
            var prefix = playerName + ":";
            foreach (var line in hand)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (line.Contains("small blind", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("ciega chica", StringComparison.OrdinalIgnoreCase))
                    return "SB";
                if (line.Contains("big blind", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("ciega grande", StringComparison.OrdinalIgnoreCase))
                    return "BB";
            }

            var playerCount = ExtractPlayersFromSeats(hand).Count;
            var preflopActors = GetStreetLines(hand, "PREFLOP")
                .Select(line => ActorRx.Match(line))
                .Where(match => match.Success)
                .Select(match => new
                {
                    Name = match.Groups["actor"].Value.Trim(),
                    Action = match.Groups["action"].Value.Trim()
                })
                .Where(row =>
                    !row.Action.Contains("small blind", StringComparison.OrdinalIgnoreCase) &&
                    !row.Action.Contains("big blind", StringComparison.OrdinalIgnoreCase) &&
                    !row.Action.Contains("ciega chica", StringComparison.OrdinalIgnoreCase) &&
                    !row.Action.Contains("ciega grande", StringComparison.OrdinalIgnoreCase))
                .Select(row => row.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var index = preflopActors.FindIndex(name => string.Equals(name, playerName, StringComparison.Ordinal));
            if (index < 0)
                return "?";

            return playerCount switch
            {
                <= 2 => index == 0 ? "BTN/SB" : "BB",
                3 => index switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                4 => index switch { 0 => "CO", 1 => "BTN", 2 => "SB", 3 => "BB", _ => "?" },
                5 => index switch { 0 => "UTG", 1 => "CO", 2 => "BTN", 3 => "SB", 4 => "BB", _ => "?" },
                _ => index switch { 0 => "UTG", 1 => "HJ", 2 => "CO", 3 => "BTN", 4 => "SB", 5 => "BB", _ => "MP" }
            };
        }

        private static IReadOnlyList<string> ExtractPlayersFromSeats(IReadOnlyList<string> hand)
        {
            var result = new List<string>();
            foreach (var line in hand)
            {
                var seat = SeatRx.Match(line);
                if (seat.Success)
                    result.Add(seat.Groups["name"].Value.Trim());
            }

            return result;
        }

        private static string PositionFromOffset(int offset, int playerCount) =>
            playerCount switch
            {
                <= 2 => offset == 0 ? "BTN/SB" : "BB",
                3 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                4 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "CO", _ => "?" },
                5 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "CO", _ => "?" },
                _ => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "HJ", 5 => "CO", _ => "MP" }
            };

        private static DateTime? ExtractTimestamp(IReadOnlyList<string> hand)
        {
            foreach (var line in hand)
            {
                var match = HeaderTimestampRx.Match(line);
                if (!match.Success)
                    continue;

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

        private static double EstimateNetForPlayer(IReadOnlyList<string> hand, string playerName)
        {
            var net = 0.0;
            var committedThisStreet = 0.0;
            var prefix = playerName + ":";

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
                    string.Equals(returned.Groups["name"].Value.Trim(), playerName, StringComparison.Ordinal) &&
                    TryParseAmount(returned.Groups["amount"].Value, out var returnedAmount))
                {
                    net += returnedAmount;
                    continue;
                }

                var collected = CollectedRx.Match(line);
                if (collected.Success &&
                    string.Equals(collected.Groups["name"].Value.Trim(), playerName, StringComparison.Ordinal) &&
                    TryParseAmount(collected.Groups["amount"].Value, out var collectedAmount))
                {
                    net += collectedAmount;
                    continue;
                }

                if (!line.StartsWith(prefix, StringComparison.Ordinal))
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
            var clean = raw.Replace("$", "", StringComparison.Ordinal)
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("\u00A0", "", StringComparison.Ordinal)
                .Trim();

            if (clean.Contains(',') && clean.Contains('.'))
                clean = clean.Replace(",", "", StringComparison.Ordinal);
            else
                clean = clean.Replace(',', '.');

            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string NormalizeHand(string cards)
        {
            var parts = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "--";

            var first = ParseCard(parts[0]);
            var second = ParseCard(parts[1]);
            if (first.rank == second.rank)
                return $"{first.rank}{second.rank}";

            var firstIndex = Array.IndexOf(Ranks, first.rank);
            var secondIndex = Array.IndexOf(Ranks, second.rank);
            var high = firstIndex < secondIndex ? first : second;
            var low = firstIndex < secondIndex ? second : first;
            return $"{high.rank}{low.rank}{(high.suit == low.suit ? "s" : "o")}";
        }

        private static (string rank, char suit) ParseCard(string raw)
        {
            var card = raw.Trim();
            if (card.Length < 2)
                return (card, '?');

            return (card[..^1].ToUpperInvariant(), char.ToLowerInvariant(card[^1]));
        }

        private static string HandCode(int row, int col)
        {
            if (row == col)
                return $"{Ranks[row]}{Ranks[col]}";
            if (row < col)
                return $"{Ranks[row]}{Ranks[col]}s";
            return $"{Ranks[col]}{Ranks[row]}o";
        }

        private static bool IsPremium(string handCode)
        {
            var code = handCode.Replace("s", "", StringComparison.Ordinal).Replace("o", "", StringComparison.Ordinal);
            return code is "AA" or "KK" or "QQ" or "JJ" or "AK" or "AQ";
        }

        private static bool IsLowHand(string handCode)
        {
            var code = handCode.Replace("s", "", StringComparison.Ordinal).Replace("o", "", StringComparison.Ordinal);
            if (code.Length < 2)
                return false;

            if (code is "22" or "33" or "44" or "55" or "66")
                return true;
            return code.StartsWith("A", StringComparison.Ordinal) && (code[1] is '2' or '3' or '4' or '5');
        }

        private static bool IsPair(string handCode) =>
            handCode.Length >= 2 && handCode[0] == handCode[1];

        private static bool IsSuitedConnector(string handCode)
        {
            if (!handCode.EndsWith("s", StringComparison.Ordinal) || handCode.Length < 3)
                return false;

            var r1 = RankValue(handCode[0]);
            var r2 = RankValue(handCode[1]);
            return r1 <= 10 && r2 >= 4 && Math.Abs(r1 - r2) <= 1;
        }

        private static int RankValue(char rank) => rank switch
        {
            'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10,
            '9' => 9, '8' => 8, '7' => 7, '6' => 6, '5' => 5, '4' => 4, '3' => 3, '2' => 2,
            _ => 0
        };

        private static bool IsFlushWin(KnownVillainHand hand)
        {
            var hole = ParseCards(hand.ExactCards);
            var board = ParseCards(hand.BoardCards);
            return hole.Concat(board)
                .GroupBy(card => card.Suit)
                .Any(group => group.Count() >= 5 && hole.Any(card => card.Suit == group.Key));
        }

        private static bool IsSetWin(KnownVillainHand hand)
        {
            var hole = ParseCards(hand.ExactCards);
            var board = ParseCards(hand.BoardCards);
            return hole.Count == 2 &&
                hole[0].Rank == hole[1].Rank &&
                board.Any(card => card.Rank == hole[0].Rank);
        }

        private static bool IsTripsWin(KnownVillainHand hand)
        {
            var hole = ParseCards(hand.ExactCards);
            var board = ParseCards(hand.BoardCards);
            return hole.Any(holeCard =>
                board.Count(boardCard => boardCard.Rank == holeCard.Rank) >= 2);
        }

        private static bool IsTwoPairWin(KnownVillainHand hand)
        {
            if (IsSetWin(hand) || IsTripsWin(hand))
                return false;

            var hole = ParseCards(hand.ExactCards);
            var board = ParseCards(hand.BoardCards);
            var all = hole.Concat(board).ToList();
            var pairRanks = all
                .GroupBy(card => card.Rank)
                .Where(group => group.Count() >= 2)
                .Select(group => group.Key)
                .ToList();

            return pairRanks.Count >= 2 && pairRanks.Any(rank => hole.Any(card => card.Rank == rank));
        }

        private static bool IsHighPairWin(KnownVillainHand hand)
        {
            if (IsFlushWin(hand) || IsStraightWin(hand) || IsSetWin(hand) || IsTripsWin(hand) || IsTwoPairWin(hand))
                return false;

            var hole = ParseCards(hand.ExactCards);
            var board = ParseCards(hand.BoardCards);
            var all = hole.Concat(board).ToList();
            return all
                .GroupBy(card => card.Rank)
                .Any(group => group.Count() >= 2 &&
                    group.Key >= 11 &&
                    hole.Any(card => card.Rank == group.Key));
        }

        private static bool IsStraightWin(KnownVillainHand hand)
        {
            var hole = ParseCards(hand.ExactCards);
            var ranks = hole.Concat(ParseCards(hand.BoardCards))
                .Select(card => card.Rank)
                .Distinct()
                .ToHashSet();

            if (ranks.Contains(14))
                ranks.Add(1);

            for (var start = 1; start <= 10; start++)
            {
                var straightRanks = Enumerable.Range(start, 5).ToHashSet();
                if (straightRanks.All(ranks.Contains) &&
                    hole.Any(card => straightRanks.Contains(card.Rank == 14 ? 1 : card.Rank) || straightRanks.Contains(card.Rank)))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<CardInfo> ParseCards(string cards) =>
            cards.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseCardInfo)
                .Where(card => card.Rank > 0 && card.Suit != '?')
                .ToList();

        private static CardInfo ParseCardInfo(string raw)
        {
            var card = raw.Trim();
            if (card.Length < 2)
                return new CardInfo(0, '?');

            return new CardInfo(RankValue(card[..^1].ToUpperInvariant()[0]), char.ToLowerInvariant(card[^1]));
        }

        private static string FormatCardsForDisplay(string cards) =>
            string.Join(" ", cards
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(FormatCardForDisplay));

        private static string FormatSpotForDisplay(string cards)
        {
            var formatted = cards
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(FormatCardForDisplay)
                .ToList();

            return formatted.Count == 0 ? "-" : string.Join(" - ", formatted);
        }

        private static string FormatCardForDisplay(string card)
        {
            if (card.Length < 2)
                return card;

            var rank = card[..^1].ToUpperInvariant();
            var suit = char.ToLowerInvariant(card[^1]) switch
            {
                'h' => "\u2665",
                'd' => "\u2666",
                'c' => "\u2663",
                's' => "\u2660",
                _ => ""
            };

            return suit.Length == 0 ? card : $"{rank}{suit}";
        }

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        private static Brush BrushForBb(double bb)
        {
            var abs = Math.Abs(bb);
            if (abs < 0.05)
                return BrushFrom(80, 86, 96);
            if (bb > 0)
            {
                if (abs < 10) return BrushFrom(34, 116, 101);
                if (abs < 30) return BrushFrom(39, 156, 92);
                if (abs < 50) return BrushFrom(76, 184, 4);
                return BrushFrom(184, 181, 4);
            }

            if (abs < 10) return BrushFrom(112, 48, 55);
            if (abs < 30) return BrushFrom(178, 43, 52);
            if (abs < 50) return BrushFrom(210, 34, 45);
            return BrushFrom(156, 0, 0);
        }

        private static Brush BrushForAction(VillainAction action) => action switch
        {
            VillainAction.Fold => BrushFrom(80, 86, 96),
            VillainAction.Check => BrushFrom(75, 102, 122),
            VillainAction.Call => BrushFrom(0, 148, 198),
            VillainAction.Bet => BrushFrom(64, 184, 4),
            VillainAction.Raise => BrushFrom(184, 181, 4),
            VillainAction.ThreeBet => BrushFrom(226, 137, 0),
            VillainAction.FourBetPlus => BrushFrom(255, 115, 115),
            VillainAction.AllIn => BrushFrom(156, 0, 0),
            _ => Brushes.Transparent
        };

        private static string LabelForAction(VillainAction action) => action switch
        {
            VillainAction.Fold => "Fold",
            VillainAction.Check => "Check",
            VillainAction.Call => "Call",
            VillainAction.Bet => "Bet",
            VillainAction.Raise => "Raise",
            VillainAction.ThreeBet => "3Bet",
            VillainAction.FourBetPlus => "4Bet+",
            VillainAction.AllIn => "All-in",
            _ => action.ToString()
        };

        private static string StreetLabel(string street) => street switch
        {
            "PREFLOP" => "PRE-FLOP",
            "FLOP" => "FLOP",
            "TURN" => "TURN",
            "RIVER" => "RIVER",
            _ => street
        };

        private enum VillainAction
        {
            Fold,
            Check,
            Call,
            Bet,
            Raise,
            ThreeBet,
            FourBetPlus,
            AllIn
        }

        private sealed record CardInfo(int Rank, char Suit);

        private sealed record KnownVillainHand(
            string HandIdentity,
            string TableName,
            string SourcePath,
            int HandNumber,
            string Street,
            string HandCode,
            string ExactCards,
            string Position,
            VillainAction Action,
            double NetBb,
            string BoardCards,
            string SpotCards,
            DateTime LastSeen);

        private sealed record ComparisonRow(
            string Name,
            int Hands,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetPct,
            double FvCBPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            StakeProfile Stake,
            double BbVsValue)
        {
            public string BbVs => $"{BbVsValue:+0.#;-0.#;0} bb";
            public string BbTrendIcon => BbVsValue >= 0 ? "\u25B2" : "\u25BC";
            public Brush BbBrush => BbVsValue >= 0
                ? BrushFrom(33, 192, 122)
                : BrushFrom(226, 78, 91);

            public static ComparisonRow FromStats(string name, PlayerStats stats, double bbVsValue, StakeProfile stake) =>
                new(
                    name,
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
                    stake,
                    bbVsValue);
        }

        private sealed record TagViewModel(string Label, Brush Background, Brush Border, Brush Foreground, string Reason)
        {
            public static TagViewModel Positive(string label, string reason) =>
                new(label, BrushFrom(16, 76, 52), BrushFrom(33, 192, 122), Brushes.White, reason);

            public static TagViewModel Negative(string label, string reason) =>
                new(label, BrushFrom(98, 21, 32), BrushFrom(226, 78, 91), Brushes.White, reason);

            public static TagViewModel Neutral(string label, string reason) =>
                new(label, BrushFrom(28, 40, 54), BrushFrom(86, 108, 132), Brushes.White, reason);
        }

        private sealed class RangeCell : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _colorByProfit;

            private RangeCell(
                string street,
                string handCode,
                int count,
                IReadOnlyList<ActionSegment> segments,
                Brush foreground,
                string detail,
                IReadOnlyList<ActionSummaryItem> summaryItems,
                IReadOnlyList<ExactVillainHandRow> exactHands,
                Brush profitBrush)
            {
                Street = street;
                HandCode = handCode;
                Count = count;
                Segments = segments;
                Foreground = foreground;
                Detail = detail;
                SummaryItems = summaryItems;
                ExactHands = exactHands;
                ProfitBrush = profitBrush;
            }

            public string Street { get; }
            public string HandCode { get; }
            public int Count { get; }
            public IReadOnlyList<ActionSegment> Segments { get; }
            public Brush Foreground { get; }
            public string Detail { get; }
            public IReadOnlyList<ActionSummaryItem> SummaryItems { get; }
            public IReadOnlyList<ExactVillainHandRow> ExactHands { get; }
            public Brush ProfitBrush { get; }
            public Brush ProfitOverlayBrush => ColorByProfit ? ProfitBrush : Brushes.Transparent;
            public double ProfitOverlayOpacity => ColorByProfit ? 1.0 : 0.0;
            public bool ColorByProfit
            {
                get => _colorByProfit;
                set
                {
                    if (_colorByProfit == value)
                        return;

                    _colorByProfit = value;
                    OnPropertyChanged(nameof(ColorByProfit));
                    OnPropertyChanged(nameof(ProfitOverlayBrush));
                    OnPropertyChanged(nameof(ProfitOverlayOpacity));
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

            public static RangeCell Empty(string street, string handCode) =>
                new(
                    street,
                    handCode,
                    0,
                    new[] { new ActionSegment(BrushFrom(38, 43, 50), 100, new Thickness(0)) },
                    BrushFrom(135, 145, 156),
                    "Sin muestra conocida",
                    new[] { new ActionSummaryItem("Sin muestra", "0 manos", BrushFrom(80, 86, 96)) },
                    Array.Empty<ExactVillainHandRow>(),
                    BrushFrom(38, 43, 50));

            public static RangeCell FromExamples(string street, string handCode, IReadOnlyList<KnownVillainHand> examples)
            {
                var avg = examples.Average(hand => hand.NetBb);
                var actionGroups = ActionDisplayOrder
                    .Where(action => examples.Any(hand => hand.Action == action))
                    .Select(action => new
                    {
                        Action = action,
                        Hands = examples.Where(hand => hand.Action == action).ToList()
                    })
                    .ToList();
                var segments = actionGroups
                    .Select((group, index) => new ActionSegment(
                        BrushForAction(group.Action),
                        Math.Max(4, group.Hands.Count * 100.0 / examples.Count),
                        index == 0 ? new Thickness(0) : new Thickness(1, 0, 0, 0)))
                    .ToList();
                var summaryItems = actionGroups
                    .Select(group =>
                    {
                        var pct = group.Hands.Count * 100.0 / examples.Count;
                        var groupAvg = group.Hands.Average(hand => hand.NetBb);
                        return new ActionSummaryItem(
                            $"{LabelForAction(group.Action)} {pct:0.#}%",
                            $"{group.Hands.Count} manos | {groupAvg:+0.#;-0.#;0} bb media",
                            BrushForAction(group.Action));
                    })
                    .ToList();
                var exactHands = examples
                    .GroupBy(hand => hand.HandIdentity, StringComparer.Ordinal)
                    .Select(group => ExactVillainHandRow.FromHand(group.First()))
                    .OrderBy(row => row.TableName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.HandNumber)
                    .ToList();
                return new RangeCell(
                    street,
                    handCode,
                    examples.Count,
                    segments,
                    Brushes.Black,
                    $"{examples.Count} acciones | {avg:+0.#;-0.#;0} bb media | {string.Join(", ", summaryItems.Select(item => item.Label))}",
                    summaryItems,
                    exactHands,
                    BrushForBb(avg));
            }
        }

        private sealed record ActionSegment(Brush Background, double Width, Thickness BorderThickness);

        private sealed record ActionSummaryItem(string Label, string Detail, Brush Color);

        private sealed record ExactVillainHandRow(
            string TableName,
            string SourcePath,
            int HandNumber,
            DateTime LastSeen,
            string ExactCards,
            string Spot,
            string Position,
            VillainAction Action,
            double NetBb)
        {
            public string LastSeenLabel => LastSeen == DateTime.MinValue
                ? "-"
                : LastSeen.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            public string ActionLabel => LabelForAction(Action);
            public string NetBbLabel => $"{NetBb:+0.#;-0.#;0} bb";
            public string NetTrendIcon => NetBb >= 0 ? "\u25B2" : "\u25BC";
            public Brush NetTrendBrush => NetBb >= 0
                ? BrushFrom(33, 192, 122)
                : BrushFrom(226, 78, 91);
            public IReadOnlyList<CardChipViewModel> ExactCardChips =>
                CardChipViewModel.FromCards(FormatCardsForDisplay(ExactCards));
            public IReadOnlyList<CardChipViewModel> SpotCardChips =>
                CardChipViewModel.FromCards(FormatSpotForDisplay(Spot));

            public static ExactVillainHandRow FromHand(KnownVillainHand hand) =>
                new(
                    hand.TableName,
                    hand.SourcePath,
                    hand.HandNumber,
                    hand.LastSeen,
                    hand.ExactCards,
                    hand.BoardCards,
                    hand.Position,
                    hand.Action,
                    hand.NetBb);
        }

        private sealed record HandSummaryRow(
            string HandCode,
            string BestPosition,
            int Count,
            double TotalBb,
            double AverageBb,
            DateTime LastSeen,
            string SourcePath,
            int HandNumber)
        {
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string AverageBbLabel => $"{AverageBb:+0.#;-0.#;0} bb";
            public string LastSeenLabel => LastSeen == DateTime.MinValue ? "-" : LastSeen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            public static HandSummaryRow FromGroup(string handCode, IReadOnlyList<KnownVillainHand> hands, bool descending)
            {
                var bestPosition = hands
                    .GroupBy(hand => hand.Position, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        Position = group.Key,
                        Average = group.Average(hand => hand.NetBb),
                        Count = group.Count()
                    })
                    .OrderByDescending(row => row.Average)
                    .ThenByDescending(row => row.Count)
                    .FirstOrDefault()?.Position ?? "?";
                var targetHand = descending
                    ? hands.OrderByDescending(hand => hand.NetBb).ThenByDescending(hand => hand.LastSeen).First()
                    : hands.OrderBy(hand => hand.NetBb).ThenByDescending(hand => hand.LastSeen).First();

                return new HandSummaryRow(
                    handCode,
                    bestPosition,
                    hands.Count,
                    hands.Sum(hand => hand.NetBb),
                    hands.Average(hand => hand.NetBb),
                    hands.Max(hand => hand.LastSeen),
                    targetHand.SourcePath,
                    targetHand.HandNumber);
            }
        }
    }
}
