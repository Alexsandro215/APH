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

namespace Hud.App.Views
{
    public partial class LeakFinderWindow : Window
    {
        private LeakFinderViewModel? _viewModel;

        public LeakFinderWindow(IEnumerable<MainWindow.TableSessionStats> tables, string summary)
        {
            InitializeComponent();
            Height = Math.Max(760, SystemParameters.WorkArea.Height - 36);
            MaxHeight = SystemParameters.WorkArea.Height;

            var tableList = tables.ToList();
            // Buscar fugas lee todos los historiales: en segundo plano.
            BackgroundLoad.Run(this, () => new LeakFinderViewModel(tableList, summary), viewModel =>
            {
                _viewModel = viewModel;
                DataContext = viewModel;
            });
        }

        private void LeakGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as DataGrid)?.SelectedItem is not LeakSummaryViewModel leak)
                return;

            OpenSpotWindow($"{leak.TitlePrefix}: {leak.Key}", leak.SummaryLabel, leak.MatchingHands);
        }

        private void LeakBars_ItemClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Charts.DivergingBarList)?.LastClicked?.Payload is LeakSummaryViewModel leak)
                OpenSpotWindow($"{leak.TitlePrefix}: {leak.Key}", leak.SummaryLabel, leak.MatchingHands);
        }

        private void BtnViewAllReview_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
                return;

            OpenSpotWindow("MANOS PARA REVISAR", _viewModel.ReviewSummary, _viewModel.ReviewHands);
        }

        private void OpenSpotWindow(string title, string summary, IEnumerable<LeakSpotRow> hands)
        {
            var window = new LeakSpotListWindow(title, summary, hands)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };
            AppNavigator.Open(window);
        }

        private void BtnSessionAnalysis_Click(object sender, RoutedEventArgs e)
        {
            // Esta ventana puede estar mostrada como pagina (nunca abierta): el dueno es la ventana visible.
            var window = new SessionLeakAnalysisWindow() { Owner = (sender as DependencyObject is { } source ? Window.GetWindow(source) : null) ?? Application.Current.MainWindow };
            window.ShowDialog();
        }

        private sealed class LeakFinderViewModel
        {
            private static readonly string[] PositionSummaryOrder =
            {
                "UTG",
                "MP",
                "CO",
                "BTN",
                "SB",
                "BB",
                "BTN/SB"
            };

            public LeakFinderViewModel(IReadOnlyList<MainWindow.TableSessionStats> tables, string summary)
            {
                Summary = summary;
                TableCount = tables.Count;
                HeroHands = tables.Sum(table => table.HandsReceived);
                TotalBb = tables.Sum(table => table.NetBb);

                var hands = tables
                    .SelectMany(LoadTableHands)
                    .OrderBy(hand => hand.DateTime)
                    .ThenBy(hand => hand.TableName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(hand => hand.HandIndex)
                    .ToList();

                CriticalHands = hands.Count(hand => hand.NetBb <= -10);
                BiggestLeakLabel = hands.Count == 0
                    ? "Sin datos"
                    : hands.GroupBy(hand => NormalizePositionLabel(hand.Position))
                        .Select(group => BuildLeakSummary("Posicion", group.Key, group))
                        .OrderBy(item => item.TotalBb)
                        .FirstOrDefault()?.Key ?? "Sin datos";

                PositionLeaks = BuildPositionSummaries(hands);
                ActionLeaks = BuildSummaries(Hud.App.Services.LocalizationManager.Text("Common.Action"), hands, hand => hand.Action, Hud.App.Services.LocalizationManager.Text("Common.NoAction"), 10);
                PotLeaks = BuildSummaries(Hud.App.Services.LocalizationManager.Text("Common.PotType"), hands, hand => hand.PotType, Hud.App.Services.LocalizationManager.Text("Common.NoPot"), 10);
                ComboLeaks = BuildSummaries(Hud.App.Services.LocalizationManager.Text("Common.Hand"), hands, hand => hand.Combo, Hud.App.Services.LocalizationManager.Text("Common.NoHand"), 16);
                BoardLeaks = BuildSummaries("Board", hands.Where(hand => hand.BoardTexture != "Preflop"), hand => hand.BoardTexture, "Sin board", 10);
                SessionLeaks = new ObservableCollection<LeakSummaryViewModel>(
                    hands.GroupBy(hand => hand.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .Select(group =>
                        {
                            var summaryItem = BuildLeakSummary("Sesion", group.Key, group);
                            summaryItem.WorstCombo = summaryItem.TotalBb < 0 ? "Sesion perdedora" : "Sesion ganadora";
                            return summaryItem;
                        })
                        .OrderByDescending(item => item.Key)
                        .Take(20));

                ReviewHands = new ObservableCollection<LeakSpotRow>(
                    hands.OrderBy(hand => hand.NetBb)
                        .ThenByDescending(hand => hand.AbsNetBb)
                        .Take(80));

                WorstReviewHandLabel = ReviewHands.FirstOrDefault() is { } worst
                    ? $"{worst.Combo} {worst.NetBbLabel}"
                    : "Sin datos";
                WorstReviewActionLabel = BuildWorstLabel(ReviewHands, hand => hand.Action);
                WorstReviewPotLabel = BuildWorstLabel(ReviewHands, hand => hand.PotType);
                ReviewCountLabel = string.Format(CultureInfo.InvariantCulture, Hud.App.Services.LocalizationManager.Text("Common.CountOfHands"), ReviewHands.Count, hands.Count);
                ReviewSummary = string.Format(Hud.App.Services.LocalizationManager.Text("Common.ReviewSummary"), ReviewHands.Count);
                Conclusions = BuildConclusions().ToList();
            }

            /// <summary>Las fugas claras (grupos de 30 manos o mas que pierden) en frases, con que hacer.</summary>
            public IReadOnlyList<string> Conclusions { get; }

            private IEnumerable<string> BuildConclusions()
            {
                var text = Hud.App.Services.LocalizationManager.Text;
                string Bb(double value) => value.ToString("+0;-0;0", CultureInfo.CurrentCulture);
                LeakSummaryViewModel? Worst(IEnumerable<LeakSummaryViewModel> items) =>
                    items.Where(item => item.Count >= 30 && item.TotalBb <= -20).OrderBy(item => item.TotalBb).FirstOrDefault();

                var any = false;
                if (Worst(PositionLeaks) is { } position)
                {
                    any = true;
                    yield return string.Format(CultureInfo.CurrentCulture, text("Leak.Conc.Position"), position.Key, Bb(position.TotalBb),
                        position.Count, Bb(position.TotalBb * 100 / Math.Max(1, position.Count)));
                }
                if (Worst(PotLeaks) is { } pot)
                {
                    any = true;
                    yield return string.Format(CultureInfo.CurrentCulture, text("Leak.Conc.Pot"), pot.Key, Bb(pot.TotalBb), pot.Count);
                }
                if (Worst(BoardLeaks) is { } board)
                {
                    any = true;
                    yield return string.Format(CultureInfo.CurrentCulture, text("Leak.Conc.Board"), board.Key, Bb(board.TotalBb), board.Count);
                }
                if (!any)
                    yield return text("Leak.Conc.None");
                if (ComboLeaks.OrderBy(item => item.TotalBb).FirstOrDefault() is { } combo && combo.TotalBb < 0 && combo.Count < 20)
                    yield return string.Format(CultureInfo.CurrentCulture, text("Leak.Conc.Combo"), combo.Key, Bb(combo.TotalBb), combo.Count);
            }

            public string Summary { get; }
            public int TableCount { get; }
            public int HeroHands { get; }
            public int CriticalHands { get; }
            public double TotalBb { get; }
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string BiggestLeakLabel { get; }
            public Brush TotalBbBrush => TotalBb >= 0
                ? BrushFrom(33, 192, 122)
                : BrushFrom(226, 78, 91);

            public ObservableCollection<LeakSummaryViewModel> PositionLeaks { get; }
            public ObservableCollection<LeakSummaryViewModel> ActionLeaks { get; }
            public ObservableCollection<LeakSummaryViewModel> PotLeaks { get; }
            public ObservableCollection<LeakSummaryViewModel> ComboLeaks { get; }
            public ObservableCollection<LeakSummaryViewModel> BoardLeaks { get; }
            public ObservableCollection<LeakSummaryViewModel> SessionLeaks { get; }

            // Barras para leer las fugas de un vistazo: rojo donde se pierde, verde donde se gana.
            // Posiciones y sesiones en su orden natural; el resto, lo peor arriba.
            public IReadOnlyList<Charts.BarDatum> PositionBars => ToBars(PositionLeaks, keepOrder: true);
            public IReadOnlyList<Charts.BarDatum> ActionBars => ToBars(ActionLeaks, keepOrder: false);
            public IReadOnlyList<Charts.BarDatum> PotBars => ToBars(PotLeaks, keepOrder: false);
            public IReadOnlyList<Charts.BarDatum> ComboBars => ToBars(ComboLeaks, keepOrder: false);
            public IReadOnlyList<Charts.BarDatum> BoardBars => ToBars(BoardLeaks, keepOrder: false);
            public IReadOnlyList<Charts.BarDatum> SessionBars => ToBars(SessionLeaks, keepOrder: true);

            private static IReadOnlyList<Charts.BarDatum> ToBars(IEnumerable<LeakSummaryViewModel> items, bool keepOrder)
            {
                var text = Hud.App.Services.LocalizationManager.Text;
                var ordered = keepOrder ? items : items.OrderBy(item => item.TotalBb);
                return ordered
                    .Select(item => new Charts.BarDatum(
                        item.Key,
                        item.TotalBb,
                        item.TotalBbLabel,
                        $"{item.Count} {text("Visual.Hands")}",
                        $"{item.TitlePrefix}: {item.Key}\n{item.SummaryLabel}\n{item.WorstCombo}",
                        item))
                    .ToList();
            }
            public ObservableCollection<LeakSpotRow> ReviewHands { get; }
            public string WorstReviewHandLabel { get; }
            public string WorstReviewActionLabel { get; }
            public string WorstReviewPotLabel { get; }
            public string ReviewCountLabel { get; }
            public string ReviewSummary { get; }

            private static ObservableCollection<LeakSummaryViewModel> BuildSummaries(
                string titlePrefix,
                IEnumerable<LeakSpotRow> hands,
                Func<LeakSpotRow, string> keySelector,
                string fallback,
                int take)
            {
                return new ObservableCollection<LeakSummaryViewModel>(
                    hands.GroupBy(hand =>
                        string.IsNullOrWhiteSpace(keySelector(hand)) ? fallback : keySelector(hand))
                        .Select(group => BuildLeakSummary(titlePrefix, group.Key, group))
                        .OrderBy(item => item.TotalBb)
                        .ThenByDescending(item => item.Count)
                        .Take(take));
            }

            private static ObservableCollection<LeakSummaryViewModel> BuildPositionSummaries(
                IReadOnlyCollection<LeakSpotRow> hands)
            {
                var groups = hands
                    .GroupBy(hand => NormalizePositionLabel(hand.Position))
                    .ToDictionary(group => group.Key, group => group.AsEnumerable(), StringComparer.OrdinalIgnoreCase);

                return new ObservableCollection<LeakSummaryViewModel>(
                    PositionSummaryOrder.Select(position =>
                        BuildLeakSummary(
                            "Posicion",
                            position,
                            groups.TryGetValue(position, out var positionHands)
                                ? positionHands
                                : Enumerable.Empty<LeakSpotRow>())));
            }

            private static LeakSummaryViewModel BuildLeakSummary(string titlePrefix, string key, IEnumerable<LeakSpotRow> group)
            {
                var hands = group.ToList();
                var worst = hands.OrderBy(hand => hand.NetBb).FirstOrDefault();
                var worstAction = hands
                    .GroupBy(hand => hand.Action)
                    .Select(actionGroup => new
                    {
                        Action = actionGroup.Key,
                        Total = actionGroup.Sum(hand => hand.NetBb),
                        Count = actionGroup.Count()
                    })
                    .OrderBy(item => item.Total)
                    .ThenByDescending(item => item.Count)
                    .FirstOrDefault();

                return new LeakSummaryViewModel
                {
                    TitlePrefix = titlePrefix,
                    Key = key,
                    Count = hands.Count,
                    TotalBb = hands.Sum(hand => hand.NetBb),
                    WorstCombo = worst is null ? "-" : $"{worst.Combo} {worst.Position}",
                    WorstAction = worstAction is null ? "-" : worstAction.Action,
                    MatchingHands = hands
                };
            }

            private static string BuildWorstLabel(IEnumerable<LeakSpotRow> hands, Func<LeakSpotRow, string> keySelector)
            {
                var worst = hands
                    .GroupBy(keySelector)
                    .Select(group => new { Key = group.Key, Total = group.Sum(hand => hand.NetBb), Count = group.Count() })
                    .OrderBy(item => item.Total)
                    .FirstOrDefault();

                return worst is null ? "Sin datos" : $"{worst.Key} {worst.Total:+0.#;-0.#;0} bb";
            }

            private static IEnumerable<LeakSpotRow> LoadTableHands(MainWindow.TableSessionStats table)
            {
                if (!File.Exists(table.SourcePath))
                    yield break;

                var lines = File.ReadLines(table.SourcePath).ToList();
                var chunks = SplitHands(lines);
                var cumulative = 0.0;

                for (var i = 0; i < chunks.Count; i++)
                {
                    var hand = chunks[i];
                    var cards = ExtractHeroCards(hand, table.HeroName);
                    if (string.IsNullOrWhiteSpace(cards))
                        continue;

                    var netAmount = EstimateHeroNetForHand(hand, table.HeroName);
                    var netBb = table.BigBlind > 0 ? netAmount / table.BigBlind : 0;
                    cumulative += netBb;

                    var positionMap = BuildPositions(hand);
                    var position = positionMap.TryGetValue(table.HeroName, out var pos) && pos != "?"
                        ? NormalizePositionLabel(pos)
                        : NormalizePositionLabel(InferPositionFromActions(hand, table.HeroName));
                    var action = DetectHeroAction(hand, table.HeroName);
                    var board = ExtractBoard(hand);
                    var boardTexture = DetectBoardTexture(board);
                    var potType = DetectPotType(hand);
                    var dateTime = ExtractHandDateTime(hand, table.LastPlayedAt);
                    var combo = NormalizeCombo(cards);
                    var streets = BuildStreetInfo(hand, table.HeroName);
                    var summaryInfo = PokerStarsHandHistory.ExtractHandSummaryInfo(hand, table.HeroName);
                    var draw = DetectDraw(cards, board);

                    yield return new LeakSpotRow(
                        table,
                        table.TableName,
                        dateTime,
                        i + 1,
                        cards,
                        combo,
                        position,
                        action,
                        potType,
                        boardTexture,
                        streets.FlopCards,
                        streets.TurnCard,
                        streets.RiverCard,
                        streets.PreflopLine,
                        streets.FlopLine,
                        streets.TurnLine,
                        streets.RiverLine,
                        summaryInfo.Combination,
                        summaryInfo.Result,
                        summaryInfo.VillainName,
                        summaryInfo.VillainCards,
                        summaryInfo.VillainCombination,
                        draw,
                        netBb,
                        cumulative,
                        BuildReason(netBb, position, action, potType, combo, boardTexture));
                }
            }

            private static List<List<string>> SplitHands(IReadOnlyList<string> lines)
            {
                return PokerStarsHandHistory.SplitHands(lines).Select(hand => hand.ToList()).ToList();
            }

            private static string ExtractHeroCards(IReadOnlyList<string> hand, string heroName)
            {
                return PokerStarsHandHistory.TryGetDealtCards(hand, heroName, out var cards)
                    ? cards
                    : "";
            }

            private static DateTime ExtractHandDateTime(IReadOnlyList<string> hand, DateTime fallback)
            {
                return PokerStarsHandHistory.ExtractTimestamp(hand) ?? fallback;
            }

            private static Dictionary<string, string> BuildPositions(IReadOnlyList<string> hand)
            {
                var positions = PokerStarsHandHistory.BuildPositionMap(hand);
                if (positions.Count > 0)
                    return positions;

                var seats = new SortedDictionary<int, string>();
                var buttonSeat = 0;

                foreach (var line in hand)
                {
                    var button = Regex.Match(
                        line,
                        @"(?:Seat|Asiento)\s+#?(?<seat>\d+)\s+(?:is the button|es el bot[o\u00F3]n)",
                        RegexOptions.IgnoreCase);
                    if (button.Success)
                        int.TryParse(button.Groups["seat"].Value, out buttonSeat);

                    var seat = Regex.Match(
                        line,
                        @"^(?:Seat|Asiento)\s+(?<seat>\d+):\s+(?<name>.+?)\s+\([^)]*\bin chips\)",
                        RegexOptions.IgnoreCase);
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
                    result[seats[orderedSeats[i]]] = AsSummaryPosition(PokerStarsHandHistory.PositionFromOffset(offset, orderedSeats.Count));
                }

                ApplyBlindPositionOverrides(hand, result, orderedSeats.Count);

                return result;
            }

            private static void ApplyBlindPositionOverrides(
                IReadOnlyList<string> hand,
                IDictionary<string, string> positions,
                int playerCount)
            {
                foreach (var line in hand)
                {
                    var match = Regex.Match(line, @"^(?<name>[^:]+):\s+(?<action>.+)$", RegexOptions.IgnoreCase);
                    if (!match.Success)
                        continue;

                    var name = match.Groups["name"].Value.Trim();
                    var action = match.Groups["action"].Value;

                    if (action.Contains("small blind", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("ciega chica", StringComparison.OrdinalIgnoreCase))
                    {
                        positions[name] = playerCount <= 2 ? "BTN/SB" : "SB";
                        continue;
                    }

                    if (action.Contains("big blind", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("ciega grande", StringComparison.OrdinalIgnoreCase))
                    {
                        positions[name] = "BB";
                    }
                }
            }

            // La tabla comun llama HJ al asiento previo al CO; las copias de esta ventana lo
            // llamaban MP, que es lo que espera PositionSummaryOrder.
            private static string AsSummaryPosition(string position) =>
                position == "HJ" ? "MP" : position;

            private static string InferPositionFromActions(IReadOnlyList<string> hand, string playerName)
            {
                var playerCount = CountPlayersFromSeats(hand);

                foreach (var line in hand)
                {
                    var match = PokerStarsHandHistory.ActorRx.Match(line);
                    if (!match.Success || !PokerStarsHandHistory.SamePlayer(match.Groups["actor"].Value, playerName))
                        continue;

                    if (line.Contains("small blind", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("ciega chica", StringComparison.OrdinalIgnoreCase))
                        return playerCount <= 2 ? "BTN/SB" : "SB";

                    if (line.Contains("big blind", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("ciega grande", StringComparison.OrdinalIgnoreCase))
                        return "BB";
                }

                var preflopActors = new List<string>();
                foreach (var line in hand)
                {
                    if (line.StartsWith("*** FLOP", StringComparison.Ordinal))
                        break;

                    var match = PokerStarsHandHistory.ActorRx.Match(line);
                    if (!match.Success)
                        continue;

                    var action = match.Groups["action"].Value;
                    if (action.Contains("small blind", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("big blind", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("ciega chica", StringComparison.OrdinalIgnoreCase) ||
                        action.Contains("ciega grande", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var actor = PokerStarsHandHistory.NormalizeName(match.Groups["actor"].Value);
                    if (!preflopActors.Contains(actor, StringComparer.Ordinal))
                        preflopActors.Add(actor);
                }

                var index = preflopActors.FindIndex(name => PokerStarsHandHistory.SamePlayer(name, playerName));
                if (index < 0)
                    return "?";

                // Un indice fuera de la mesa sigue dando UTG desde 6 jugadores, como la tabla anterior.
                if (playerCount >= 6 && index >= playerCount)
                    return "UTG";

                return AsSummaryPosition(PokerStarsHandHistory.PositionFromPreflopIndex(index, playerCount));
            }

            private static string NormalizePositionLabel(string position) =>
                position switch
                {
                    "BU" => "BTN",
                    "EP" => "UTG",
                    // La tabla de posiciones agrupa el HJ como MP. Sin esto, las manos en HJ no
                    // salian en "Perdidas por posicion" (MP quedaba en 0) pero si como "Mayor fuga: HJ".
                    "HJ" => "MP",
                    "SB/BTN" => "BTN/SB",
                    "" => "?",
                    _ => position
                };

            private static int CountPlayersFromSeats(IReadOnlyList<string> hand)
            {
                return PokerStarsHandHistory.ExtractPlayers(hand).Count;
            }

            private static string DetectHeroAction(IReadOnlyList<string> hand, string heroName)
            {
                var actions = hand
                    .Where(line =>
                    {
                        var actor = PokerStarsHandHistory.ActorRx.Match(line);
                        return actor.Success && PokerStarsHandHistory.SamePlayer(actor.Groups["actor"].Value, heroName);
                    })
                    .Select(ClassifyActionLine)
                    .Where(action => action != "Blind")
                    .ToList();

                if (actions.Contains("All-in", StringComparer.OrdinalIgnoreCase))
                    return "All-in";
                if (actions.Contains("4Bet+", StringComparer.OrdinalIgnoreCase))
                    return "4Bet+";
                if (actions.Contains("3Bet", StringComparer.OrdinalIgnoreCase))
                    return "3Bet";
                if (actions.Contains("Raise", StringComparer.OrdinalIgnoreCase))
                    return "Raise";
                if (actions.Contains("Bet", StringComparer.OrdinalIgnoreCase))
                    return "Bet";
                if (actions.Contains("Call", StringComparer.OrdinalIgnoreCase))
                    return "Call";
                if (actions.Contains("Check", StringComparer.OrdinalIgnoreCase))
                    return "Check";
                if (actions.Contains("Fold", StringComparer.OrdinalIgnoreCase))
                    return "Fold";

                return Hud.App.Services.LocalizationManager.Text("Common.NoAction");
            }

            private static StreetInfo BuildStreetInfo(IReadOnlyList<string> hand, string heroName)
            {
                var preflop = new List<string>();
                var flop = new List<string>();
                var turn = new List<string>();
                var river = new List<string>();
                var current = preflop;
                var flopCards = "";
                var turnCard = "";
                var riverCard = "";

                foreach (var line in hand)
                {
                    if (line.StartsWith("*** FLOP ***", StringComparison.Ordinal))
                    {
                        current = flop;
                        var cards = ExtractStreetCards(line);
                        flopCards = string.Join(" ", cards.Take(3));
                        continue;
                    }
                    if (line.StartsWith("*** TURN ***", StringComparison.Ordinal))
                    {
                        current = turn;
                        var cards = ExtractStreetCards(line);
                        turnCard = cards.LastOrDefault() ?? "";
                        continue;
                    }
                    if (line.StartsWith("*** RIVER ***", StringComparison.Ordinal))
                    {
                        current = river;
                        var cards = ExtractStreetCards(line);
                        riverCard = cards.LastOrDefault() ?? "";
                        continue;
                    }

                    var actor = PokerStarsHandHistory.ActorRx.Match(line);
                    if (actor.Success && PokerStarsHandHistory.SamePlayer(actor.Groups["actor"].Value, heroName))
                        current.Add(ClassifyActionLine(line));
                }

                return new StreetInfo(
                    string.IsNullOrWhiteSpace(flopCards) ? "-" : flopCards,
                    string.IsNullOrWhiteSpace(turnCard) ? "-" : turnCard,
                    string.IsNullOrWhiteSpace(riverCard) ? "-" : riverCard,
                    BuildLineCode(preflop),
                    BuildLineCode(flop),
                    BuildLineCode(turn),
                    BuildLineCode(river));
            }

            private static IReadOnlyList<string> ExtractStreetCards(string line)
            {
                var matches = Regex.Matches(line, @"\[(?<cards>[^\]]+)\]");
                if (matches.Count == 0)
                    return Array.Empty<string>();

                return matches[^1].Groups["cards"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }

            private static string BuildLineCode(IReadOnlyList<string> actions)
            {
                if (actions.Count == 0)
                    return "XX";

                return string.Concat(actions.Select(action => action switch
                {
                    "Fold" => "F",
                    "Check" => "X",
                    "Call" => "C",
                    "Bet" => "B",
                    "Raise" => "R",
                    "3Bet" => "3",
                    "4Bet+" => "4",
                    "All-in" => "A",
                    _ => ""
                })).Trim();
            }

            private static string ClassifyActionLine(string line)
            {
                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                var action = actor.Success ? actor.Groups["action"].Value : line;
                var normalized = PokerStarsHandHistory.NormalizeAction(action);

                if (line.Contains("is all-in", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("all-in", StringComparison.OrdinalIgnoreCase))
                    return "All-in";
                if (normalized == "posts")
                    return "Blind";
                if (normalized == "folds")
                    return "Fold";
                if (normalized == "checks")
                    return "Check";
                if (normalized == "calls")
                    return "Call";
                if (normalized == "bets")
                    return "Bet";
                if (normalized == "raises")
                    return "Raise";

                return "Otra";
            }

            private static string DetectPotType(IReadOnlyList<string> hand)
            {
                var preflop = TakeStreet(hand, "*** FLOP ***");
                var raises = preflop.Count(line =>
                {
                    var actor = PokerStarsHandHistory.ActorRx.Match(line);
                    return actor.Success && PokerStarsHandHistory.NormalizeAction(actor.Groups["action"].Value) == "raises";
                });
                var allIn = preflop.Any(line => line.Contains("all-in", StringComparison.OrdinalIgnoreCase));

                if (allIn)
                    return "All-in preflop";
                if (raises >= 3)
                    return "4Bet+ pot";
                if (raises == 2)
                    return "3Bet pot";
                if (raises == 1)
                    return "Single raised";

                return preflop.Any(line =>
                {
                    var actor = PokerStarsHandHistory.ActorRx.Match(line);
                    return actor.Success && PokerStarsHandHistory.NormalizeAction(actor.Groups["action"].Value) == "calls";
                })
                    ? "Limped pot"
                    : "Blind pot";
            }

            private static List<string> TakeStreet(IReadOnlyList<string> hand, string untilMarker)
            {
                var result = new List<string>();
                foreach (var line in hand)
                {
                    if (line.StartsWith(untilMarker, StringComparison.Ordinal))
                        break;
                    result.Add(line);
                }
                return result;
            }

            private static List<string> ExtractBoard(IReadOnlyList<string> hand)
            {
                var board = new List<string>();
                foreach (var line in hand)
                {
                    var match = Regex.Match(line, @"^\*\*\*\s+(?:FLOP|TURN|RIVER)\s+\*\*\*\s+\[(?<cards>.+?)\]", RegexOptions.IgnoreCase);
                    if (!match.Success)
                        continue;

                    board = match.Groups["cards"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                return board;
            }

            private static string DetectBoardTexture(IReadOnlyList<string> board)
            {
                if (board.Count == 0)
                    return "Preflop";

                var suits = board.Select(CardSuit).Where(suit => suit.Length > 0).ToList();
                var ranks = board.Select(CardRank).Where(rank => rank.Length > 0).ToList();
                var paired = ranks.GroupBy(rank => rank).Any(group => group.Count() >= 2);
                var mono = suits.Count > 0 && suits.GroupBy(suit => suit).Any(group => group.Count() >= 3);
                var connected = IsConnected(ranks);
                var broadway = ranks.Count(rank => "AKQJT".Contains(rank, StringComparison.Ordinal)) >= 2;

                if (mono)
                    return "Monocolor";
                if (paired)
                    return "Pareado";
                if (connected)
                    return "Coordinado";
                if (broadway)
                    return "Broadway";

                return "Seco";
            }

            private static string DetectMadeHand(string cards, IReadOnlyList<string> board)
            {
                if (board.Count == 0)
                    return "preflop";

                var allRanks = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(board)
                    .Select(CardRank)
                    .Where(rank => rank.Length > 0)
                    .ToList();
                var rankGroups = allRanks.GroupBy(rank => rank).Select(group => group.Count()).OrderByDescending(count => count).ToList();

                if (rankGroups.FirstOrDefault() >= 4)
                    return "poker";
                if (rankGroups.Count >= 2 && rankGroups[0] == 3 && rankGroups[1] >= 2)
                    return "full house";
                if (HasFlush(cards, board))
                    return "flush";
                if (HasStraight(allRanks))
                    return "straight";
                if (rankGroups.FirstOrDefault() == 3)
                    return "trips";
                if (rankGroups.Count(count => count >= 2) >= 2)
                    return "two pair";
                if (rankGroups.FirstOrDefault() == 2)
                    return "pair";

                return "nothing";
            }

            private static string DetectDraw(string cards, IReadOnlyList<string> board)
            {
                if (board.Count == 0 || board.Count >= 5)
                    return "no draw";

                var allCards = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat(board).ToList();
                var suits = allCards.Select(CardSuit).Where(suit => suit.Length > 0).ToList();
                if (suits.GroupBy(suit => suit).Any(group => group.Count() == 4))
                    return "FD";

                var values = allCards.Select(card => RankValue(CardRank(card))).Where(value => value > 0).Distinct().OrderBy(value => value).ToList();
                for (var i = 0; i < values.Count; i++)
                {
                    var window = values.Where(value => value >= values[i] && value <= values[i] + 4).Count();
                    if (window >= 4)
                        return "OESD/gutshot";
                }

                return "no draw";
            }

            private static bool HasFlush(string cards, IReadOnlyList<string> board)
            {
                var suits = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(board)
                    .Select(CardSuit)
                    .Where(suit => suit.Length > 0);

                return suits.GroupBy(suit => suit).Any(group => group.Count() >= 5);
            }

            private static bool HasStraight(IEnumerable<string> ranks)
            {
                var values = ranks.Select(RankValue).Where(value => value > 0).Distinct().OrderBy(value => value).ToList();
                if (values.Contains(14))
                    values.Insert(0, 1);

                var streak = 1;
                for (var i = 1; i < values.Count; i++)
                {
                    streak = values[i] == values[i - 1] + 1 ? streak + 1 : 1;
                    if (streak >= 5)
                        return true;
                }

                return false;
            }

            private static bool IsConnected(IEnumerable<string> ranks)
            {
                var values = ranks.Select(RankValue).Where(value => value > 0).Distinct().OrderBy(value => value).ToList();
                if (values.Count < 3)
                    return false;

                for (var i = 0; i <= values.Count - 3; i++)
                {
                    if (values[i + 2] - values[i] <= 4)
                        return true;
                }

                return values.Contains(14) && values.Contains(2) && values.Contains(3);
            }

            private static int RankValue(string rank) => rank switch
            {
                "A" => 14,
                "K" => 13,
                "Q" => 12,
                "J" => 11,
                "T" => 10,
                _ => int.TryParse(rank, out var value) ? value : 0
            };

            private static string NormalizeCombo(string cards)
            {
                var parts = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return cards;

                var firstRank = CardRank(parts[0]);
                var secondRank = CardRank(parts[1]);
                var firstValue = RankValue(firstRank);
                var secondValue = RankValue(secondRank);
                var suited = CardSuit(parts[0]) == CardSuit(parts[1]);

                if (firstValue < secondValue)
                    (firstRank, secondRank) = (secondRank, firstRank);

                if (firstRank == secondRank)
                    return $"{firstRank}{secondRank}";

                return $"{firstRank}{secondRank}{(suited ? "s" : "o")}";
            }

            private static string CardRank(string card) => string.IsNullOrWhiteSpace(card) ? "" : card[0].ToString().ToUpperInvariant();
            private static string CardSuit(string card) => string.IsNullOrWhiteSpace(card) || card.Length < 2 ? "" : card[^1].ToString();

            private static string BuildReason(double netBb, string position, string action, string potType, string combo, string boardTexture)
            {
                if (netBb <= -50)
                    return string.Format(CultureInfo.InvariantCulture, Hud.App.Services.LocalizationManager.Text("LeakReason.BigLoss"), combo, position, action, potType);
                if (netBb <= -10)
                    return $"Spot repetible: {position}, {action}, {potType}, board {boardTexture}.";
                if (netBb < 0)
                    return Hud.App.Services.LocalizationManager.Text("LeakReason.SmallLoss");

                return Hud.App.Services.LocalizationManager.Text("LeakReason.WinCompare");
            }

            private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
            {
                return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
            }

            private static bool TryParseAmount(string raw, out double amount)
            {
                if (Hud.App.Services.PokerAmountParser.TryParse(raw, out amount))
                    return true;

                var clean = raw.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
                return double.TryParse(clean, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
            }

            private static Brush BrushFrom(byte r, byte g, byte b) =>
                BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
        }

        private sealed record StreetInfo(
            string FlopCards,
            string TurnCard,
            string RiverCard,
            string PreflopLine,
            string FlopLine,
            string TurnLine,
            string RiverLine);

        private sealed class LeakSummaryViewModel
        {
            public string TitlePrefix { get; init; } = "";
            public string Key { get; init; } = "";
            public int Count { get; init; }
            public double TotalBb { get; init; }
            public string WorstCombo { get; set; } = "-";
            public string WorstAction { get; init; } = "-";
            public IReadOnlyList<LeakSpotRow> MatchingHands { get; init; } = Array.Empty<LeakSpotRow>();
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string AverageBbLabel => Count == 0 ? "0 bb" : $"{TotalBb / Count:+0.#;-0.#;0} bb";
            public double AverageBb => Count == 0 ? 0 : TotalBb / Count;
            public string TotalBbTrendIcon => TrendIcon(TotalBb);
            public Brush TotalBbTrendBrush => TrendBrush(TotalBb);
            public string AverageBbTrendIcon => TrendIcon(AverageBb);
            public Brush AverageBbTrendBrush => TrendBrush(AverageBb);
            public string SummaryLabel => string.Format(CultureInfo.InvariantCulture, Hud.App.Services.LocalizationManager.Text("Common.SummaryHandsBbAverage"), Count, TotalBb, AverageBbLabel);
        }

        private static string TrendIcon(double value) =>
            value switch
            {
                > 0 => "\u25B2",
                < 0 => "\u25BC",
                _ => "\u25AC"
            };

        private static Brush TrendBrush(double value) =>
            value switch
            {
                > 0 => BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(33, 192, 122))),
                < 0 => BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(226, 78, 91))),
                _ => BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(135, 145, 156)))
            };
    }
}

