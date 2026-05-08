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
using HandReader.Core.Models;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class HeroProfileWindow : Window
    {
        private readonly HeroProfileViewModel _viewModel;

        public HeroProfileWindow(
            PlayerStats hero,
            IEnumerable<MainWindow.TableSessionStats> tables,
            StakeProfile stake,
            string summary)
        {
            InitializeComponent();
            FitToWorkArea();
            _viewModel = HeroProfileViewModel.Build(hero, tables.ToList(), stake, summary);
            DataContext = _viewModel;
            Title = $"APH - Perfil de {hero.Name}";
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(1480, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        private void HandGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if ((sender as DataGrid)?.SelectedItem is not HeroHandRow row)
                return;

            var table = _viewModel.Tables.FirstOrDefault(t =>
                string.Equals(t.SourcePath, row.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return;

            var window = new TableDetailWindow(table, row.HandNumber)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private void BtnDictionary_Click(object sender, RoutedEventArgs e)
        {
            var window = new HeroProfileDictionaryWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private sealed class HeroProfileViewModel
        {
            private static readonly string[] PositionOrder =
            {
                "UTG",
                "MP",
                "CO",
                "BTN",
                "SB",
                "BB",
                "BTN/SB"
            };

            private HeroProfileViewModel(
                PlayerStats hero,
                IReadOnlyList<MainWindow.TableSessionStats> tables,
                StakeProfile stake,
                string summary,
                IReadOnlyList<HeroHandRow> hands)
            {
                Hero = hero;
                Tables = tables;
                HeroTitle = $"PERFIL DEL HEROE - {hero.Name}";
                Summary = summary;
                HandsLabel = hero.HandsReceived.ToString(CultureInfo.InvariantCulture);
                TotalBb = tables.Sum(table => table.NetBb);
                BbPer100 = hero.HandsReceived == 0 ? 0 : TotalBb * 100.0 / hero.HandsReceived;
                TotalBbLabel = $"{TotalBb:+0.#;-0.#;0} bb";
                BbPer100Label = $"{BbPer100:+0.#;-0.#;0} bb/100";
                TotalBbBrush = TotalBb >= 0 ? BrushFrom(33, 192, 122) : BrushFrom(226, 78, 91);

                StatMetrics = BuildStatMetrics(hero, stake).ToList();
                PositionRows = BuildPositionRows(hands).ToList();
                TopCombos = BuildComboRows(hands, hero.HandsReceived).ToList();
                ActionRows = BuildActionRows(hands).ToList();
                BestHands = hands.OrderByDescending(hand => hand.NetBb).Take(10).ToList();
                WorstHands = hands.OrderBy(hand => hand.NetBb).Take(10).ToList();
                BestPositionLabel = PositionRows.Where(row => row.Hands > 0)
                    .OrderByDescending(row => row.TotalBb)
                    .FirstOrDefault()?.Position ?? "-";
                WorstPositionLabel = PositionRows.Where(row => row.Hands > 0)
                    .OrderBy(row => row.TotalBb)
                    .FirstOrDefault()?.Position ?? "-";
                Tags = BuildTags(hero, PositionRows, TopCombos).ToList();
                Notes = BuildNotes(hero, PositionRows, ActionRows, TopCombos).ToList();
            }

            public PlayerStats Hero { get; }
            public IReadOnlyList<MainWindow.TableSessionStats> Tables { get; }
            public string HeroTitle { get; }
            public string Summary { get; }
            public string HandsLabel { get; }
            public double TotalBb { get; }
            public double BbPer100 { get; }
            public string TotalBbLabel { get; }
            public string BbPer100Label { get; }
            public Brush TotalBbBrush { get; }
            public string BestPositionLabel { get; }
            public string WorstPositionLabel { get; }
            public IReadOnlyList<StatMetricRow> StatMetrics { get; }
            public IReadOnlyList<TagViewModel> Tags { get; }
            public IReadOnlyList<PositionRow> PositionRows { get; }
            public IReadOnlyList<ComboRow> TopCombos { get; }
            public IReadOnlyList<ActionRow> ActionRows { get; }
            public IReadOnlyList<HeroHandRow> BestHands { get; }
            public IReadOnlyList<HeroHandRow> WorstHands { get; }
            public IReadOnlyList<string> Notes { get; }

            public static HeroProfileViewModel Build(
                PlayerStats hero,
                IReadOnlyList<MainWindow.TableSessionStats> tables,
                StakeProfile stake,
                string summary)
            {
                var hands = tables
                    .Where(table => File.Exists(table.SourcePath))
                    .SelectMany(LoadHands)
                    .OrderBy(hand => hand.PlayedAt)
                    .ThenBy(hand => hand.TableName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(hand => hand.HandNumber)
                    .ToList();

                return new HeroProfileViewModel(hero, tables, stake, summary, hands);
            }

            private static IEnumerable<StatMetricRow> BuildStatMetrics(PlayerStats hero, StakeProfile stake)
            {
                yield return new StatMetricRow("VPIP", "VPIP%", hero.VPIPPct, $"{hero.VPIPPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("PFR", "PFR%", hero.PFRPct, $"{hero.PFRPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("THREEBET", "3Bet%", hero.ThreeBetPct, $"{hero.ThreeBetPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("AF", "AF", hero.AF, $"{hero.AF:0.#}", hero.HandsReceived, stake);
                yield return new StatMetricRow("AFQ", "AFq%", hero.AFqPct, $"{hero.AFqPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("CBF", "CBet%", hero.CBetFlopPct, $"{hero.CBetFlopPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("FVCBF", "FvCBet%", hero.FoldVsCBetFlopPct, $"{hero.FoldVsCBetFlopPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("WTSD", "WTSD%", hero.WTSDPct, $"{hero.WTSDPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("WSD", "W$SD%", hero.WSDPct, $"{hero.WSDPct:0.#}%", hero.HandsReceived, stake);
                yield return new StatMetricRow("WWSF", "WWSF%", hero.WWSFPct, $"{hero.WWSFPct:0.#}%", hero.HandsReceived, stake);
            }

            private static IEnumerable<PositionRow> BuildPositionRows(IReadOnlyList<HeroHandRow> hands)
            {
                var groups = hands.GroupBy(hand => hand.Position)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var position in PositionOrder)
                {
                    var positionHands = groups.TryGetValue(position, out var rows)
                        ? rows
                        : new List<HeroHandRow>();

                    yield return PositionRow.From(position, positionHands);
                }
            }

            private static IEnumerable<ComboRow> BuildComboRows(IReadOnlyList<HeroHandRow> hands, int totalHands)
            {
                var playedHands = hands.Where(hand => hand.IsVpip).ToList();
                var denominator = Math.Max(1, totalHands);

                return playedHands
                    .GroupBy(hand => hand.Combo)
                    .Select(group => ComboRow.From(group.Key, group.ToList(), denominator))
                    .OrderByDescending(row => row.Count)
                    .ThenByDescending(row => row.TotalBb)
                    .Take(10);
            }

            private static IEnumerable<ActionRow> BuildActionRows(IReadOnlyList<HeroHandRow> hands)
            {
                return hands
                    .GroupBy(hand => hand.Action)
                    .Select(group => ActionRow.From(group.Key, group.ToList()))
                    .OrderBy(row => row.TotalBb)
                    .ThenByDescending(row => row.Count);
            }

            private static IEnumerable<TagViewModel> BuildTags(
                PlayerStats hero,
                IReadOnlyList<PositionRow> positions,
                IReadOnlyList<ComboRow> combos)
            {
                yield return Tag(
                    ClassifyProfile(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct, hero.AF),
                    "Perfil base segun VPIP/PFR/3Bet/AF, igual que en Data Villans.",
                    Neutral());

                if (hero.HandsReceived < 30)
                    yield return Tag("Sin muestra", "Menos de 30 manos totales.", Neutral());
                if (hero.VPIPPct >= 35)
                    yield return Tag("Juega muchas manos", $"VPIP {hero.VPIPPct:0.#}%.", Danger());
                if (hero.AF >= 4 || hero.AFqPct >= 65)
                    yield return Tag("Agresor", $"AF {hero.AF:0.#} | AFq {hero.AFqPct:0.#}%.", Danger());
                if (hero.ThreeBetPct >= 10)
                    yield return Tag("3Bet alto", $"3Bet {hero.ThreeBetPct:0.#}%.", Danger());
                if (hero.FoldVsCBetFlopPct >= 65)
                    yield return Tag("Foldea mucho a CBet", $"FvCB {hero.FoldVsCBetFlopPct:0.#}%.", Accent());
                if (hero.FoldVsCBetFlopPct > 0 && hero.FoldVsCBetFlopPct <= 30)
                    yield return Tag("No foldea CBet", $"FvCB {hero.FoldVsCBetFlopPct:0.#}%.", Danger());
                if (hero.WTSDPct >= 35)
                    yield return Tag("Va mucho a showdown", $"WTSD {hero.WTSDPct:0.#}%.", Danger());
                if (hero.VPIPPct >= 30 && hero.PFRPct < 12 && hero.AF < 1.5)
                    yield return Tag("Calling station", "VPIP alto, PFR bajo y agresion baja.", Accent());
                if (hero.VPIPPct < 14 && hero.PFRPct < 10 && hero.HandsReceived >= 50)
                    yield return Tag("Roca", "Rango cerrado con muestra suficiente.", Neutral());

                yield return Tag(
                    hero.VPIPPct >= 35 ? "Loose" : hero.VPIPPct <= 18 ? "Tight" : "Rango medio",
                    $"VPIP {hero.VPIPPct:0.#}%. Indica cuantas manos juegas voluntariamente preflop.",
                    Accent());
                yield return Tag(
                    hero.PFRPct >= 22 ? "Agresivo preflop" : hero.PFRPct <= 10 ? "PFR bajo" : "PFR estable",
                    $"PFR {hero.PFRPct:0.#}%. Mide cuantas manos subes preflop en vez de solo pagar.",
                    Neutral());
                yield return Tag(
                    hero.FoldVsCBetFlopPct >= 65 ? "Overfold vs CBet" : "Defensa vs CBet ok",
                    $"Fold vs CBet {hero.FoldVsCBetFlopPct:0.#}%. Alto significa que abandonas muchos flops ante apuesta de continuacion.",
                    hero.FoldVsCBetFlopPct >= 65 ? Danger() : Neutral());
                yield return Tag(
                    hero.CBetFlopPct >= 55 ? "CBet frecuente" : "CBet selectiva",
                    $"CBet flop {hero.CBetFlopPct:0.#}%. Frecuencia con la que apuestas flop tras ser agresor preflop.",
                    Neutral());
                yield return Tag(
                    hero.WSDPct >= 55 ? "Showdown fuerte" : "Showdown a revisar",
                    $"W$SD {hero.WSDPct:0.#}%. Porcentaje de showdowns ganados cuando llegas a mostrar.",
                    hero.WSDPct >= 55 ? Accent() : Danger());

                var worst = positions.Where(position => position.Hands > 0).OrderBy(position => position.TotalBb).FirstOrDefault();
                if (worst is not null)
                    yield return Tag(
                        $"Leak {worst.Position}",
                        $"Tu peor posicion por bb total es {worst.Position}: {worst.TotalBbLabel}, {worst.BbPer100Label} bb/100.",
                        Danger());

                var top = combos.FirstOrDefault();
                if (top is not null)
                    yield return CardTag(
                        top.Combo,
                        "lover",
                        $"{top.Combo} es el combo voluntario mas frecuente: {top.Count} veces, {top.UsageLabel} de la muestra.",
                        Neutral());

                foreach (var tag in BuildRangeTags(combos))
                    yield return tag;
            }

            private static IEnumerable<TagViewModel> BuildRangeTags(IReadOnlyList<ComboRow> combos)
            {
                var total = combos.Sum(combo => combo.Count);
                if (total == 0)
                    yield break;

                var premium = combos.Where(combo => IsPremium(combo.Combo)).Sum(combo => combo.Count);
                var low = combos.Where(combo => IsLowHand(combo.Combo)).Sum(combo => combo.Count);
                var suitedConnectors = combos.Where(combo => IsSuitedConnector(combo.Combo)).Sum(combo => combo.Count);
                var categories = new[]
                {
                    premium > 0,
                    low > 0,
                    suitedConnectors > 0,
                    combos.Any(combo => combo.Combo.EndsWith("o", StringComparison.Ordinal)),
                    combos.Any(combo => combo.Combo.EndsWith("s", StringComparison.Ordinal))
                }.Count(value => value);

                if (premium >= 3 && premium * 100.0 / total >= 30)
                    yield return Tag($"Amante premium - {premium}/{total}", "Muchas manos frecuentes son rango premium.", Neutral());
                if (low >= 3 && low * 100.0 / total >= 25)
                    yield return Tag($"Manos bajas - {low}/{total}", "Muestra tendencia a jugar manos bajas entre tus combos frecuentes.", Neutral());
                if (suitedConnectors >= 3 && suitedConnectors * 100.0 / total >= 20)
                    yield return Tag($"Suited connectors - {suitedConnectors}/{total}", "Juegas suited connectors con frecuencia.", Neutral());
                if (categories >= 4 && combos.Count >= 10)
                    yield return Tag("Mixto", "Muestra variedad amplia de categorias en tus combos frecuentes.", Neutral());
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

            private static bool IsPremium(string handCode) =>
                handCode is "AA" or "KK" or "QQ" or "JJ" or "TT" or "AKs" or "AKo" or "AQs";

            private static bool IsPair(string handCode) =>
                handCode.Length >= 2 && handCode[0] == handCode[1];

            private static bool IsLowHand(string handCode)
            {
                var ranks = HandRanks(handCode);
                return ranks.Count == 2 && ranks.Max() <= 8;
            }

            private static bool IsSuitedConnector(string handCode)
            {
                if (!handCode.EndsWith("s", StringComparison.Ordinal))
                    return false;

                var ranks = HandRanks(handCode);
                return ranks.Count == 2 && Math.Abs(ranks[0] - ranks[1]) <= 2;
            }

            private static IReadOnlyList<int> HandRanks(string handCode)
            {
                var values = new List<int>(2);
                foreach (var c in handCode)
                {
                    var value = c switch
                    {
                        'A' => 14,
                        'K' => 13,
                        'Q' => 12,
                        'J' => 11,
                        'T' => 10,
                        >= '2' and <= '9' => c - '0',
                        _ => 0
                    };

                    if (value > 0)
                        values.Add(value);
                    if (values.Count == 2)
                        break;
                }

                return values;
            }

            private static IEnumerable<string> BuildNotes(
                PlayerStats hero,
                IReadOnlyList<PositionRow> positions,
                IReadOnlyList<ActionRow> actions,
                IReadOnlyList<ComboRow> combos)
            {
                var worstPosition = positions.Where(row => row.Hands > 0).OrderBy(row => row.TotalBb).FirstOrDefault();
                if (worstPosition is not null)
                    yield return $"La posicion mas costosa en la muestra es {worstPosition.Position}: {worstPosition.TotalBbLabel}, {worstPosition.BbPer100Label}.";

                var bestPosition = positions.Where(row => row.Hands > 0).OrderByDescending(row => row.TotalBb).FirstOrDefault();
                if (bestPosition is not null)
                    yield return $"La posicion mas rentable es {bestPosition.Position}: {bestPosition.TotalBbLabel}.";

                var worstAction = actions.OrderBy(row => row.TotalBb).FirstOrDefault();
                if (worstAction is not null)
                    yield return $"La accion con peor EV agregado es {worstAction.Action}: {worstAction.TotalBbLabel}.";

                var topCombo = combos.FirstOrDefault();
                if (topCombo is not null)
                    yield return $"El combo mas jugado voluntariamente es {topCombo.Combo}: {topCombo.Count} veces, {topCombo.UsageLabel} de la muestra.";

                if (hero.FoldVsCBetFlopPct >= 65)
                    yield return "Fold vs CBet alto: revisar defensas en flop, sobre todo en posicion y BB.";
                if (hero.WTSDPct >= 30 && hero.WSDPct < 50)
                    yield return "Vas bastante al showdown y ganas poco: revisar calls de turn/river.";
            }

            private static IEnumerable<HeroHandRow> LoadHands(MainWindow.TableSessionStats table)
            {
                var chunks = SplitHands(File.ReadLines(table.SourcePath));
                var handNumber = 0;
                foreach (var hand in chunks)
                {
                    if (!TryExtractHeroCards(hand, table.HeroName, out var cards))
                        continue;

                    handNumber++;
                    var netAmount = EstimateHeroNetForHand(hand, table.HeroName);
                    var netBb = table.BigBlind > 0 ? netAmount / table.BigBlind : 0;
                    var position = ResolvePosition(hand, table.HeroName);
                    var action = DetectHeroAction(hand, table.HeroName);
                    var combo = NormalizeCombo(cards);
                    var playedAt = ExtractHandDateTime(hand, table.LastPlayedAt);

                    yield return new HeroHandRow(
                        table.TableName,
                        table.SourcePath,
                        handNumber,
                        playedAt,
                        combo,
                        cards,
                        position,
                        action.Label,
                        action.IsVpip,
                        action.IsPfr,
                        action.IsThreeBet,
                        netBb);
                }
            }

            private static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
            {
                return PokerStarsHandHistory.SplitHands(lines);
            }

            private static bool TryExtractHeroCards(IReadOnlyList<string> hand, string heroName, out string cards)
            {
                return PokerStarsHandHistory.TryGetDealtCards(hand, heroName, out cards);
            }

            private static string ResolvePosition(IReadOnlyList<string> hand, string heroName)
            {
                var positions = BuildPositions(hand);
                return positions.TryGetValue(heroName, out var position)
                    ? position
                    : "?";
            }

            private static Dictionary<string, string> BuildPositions(IReadOnlyList<string> hand)
            {
                return PokerStarsHandHistory.BuildPositionMap(hand);
            }

            private static void ApplyBlindOverrides(IReadOnlyList<string> hand, IDictionary<string, string> positions, int playerCount)
            {
                foreach (var line in hand)
                {
                    var match = Regex.Match(line, @"^(?<name>[^:]+):\s+(?<action>.+)$", RegexOptions.IgnoreCase);
                    if (!match.Success)
                        continue;

                    var name = match.Groups["name"].Value.Trim();
                    var action = match.Groups["action"].Value;
                    if (action.Contains("small blind", StringComparison.OrdinalIgnoreCase))
                    {
                        positions[name] = playerCount <= 2 ? "BTN/SB" : "SB";
                        continue;
                    }

                    if (action.Contains("big blind", StringComparison.OrdinalIgnoreCase))
                        positions[name] = "BB";
                }
            }

            private static string PositionFromOffset(int offset, int playerCount) =>
                playerCount switch
                {
                    <= 2 => offset == 0 ? "BTN/SB" : "BB",
                    3 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                    4 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "CO", _ => "?" },
                    5 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "CO", _ => "?" },
                    _ => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "MP", 5 => "CO", _ => "UTG" }
                };

            private static HeroActionInfo DetectHeroAction(IReadOnlyList<string> hand, string heroName)
            {
                var raisesBeforeHero = 0;
                foreach (var line in GetPreflopLines(hand))
                {
                    var match = PokerStarsHandHistory.ActorRx.Match(line);
                    if (!match.Success)
                        continue;

                    var actor = PokerStarsHandHistory.NormalizeName(match.Groups["actor"].Value);
                    var action = match.Groups["action"].Value.Trim();
                    var normalizedAction = PokerStarsHandHistory.NormalizeAction(action);
                    var isRaise = normalizedAction == "raises";

                    if (PokerStarsHandHistory.SamePlayer(actor, heroName))
                    {
                        if (action.Contains("all-in", StringComparison.OrdinalIgnoreCase))
                            return new HeroActionInfo("All-in", true, isRaise || raisesBeforeHero > 0, raisesBeforeHero >= 1);
                        if (isRaise)
                            return raisesBeforeHero switch
                            {
                                0 => new HeroActionInfo("Raise", true, true, false),
                                1 => new HeroActionInfo("3Bet", true, true, true),
                                _ => new HeroActionInfo("4Bet+", true, true, true)
                            };
                        if (normalizedAction == "calls")
                            return new HeroActionInfo("Call", true, false, false);
                        if (normalizedAction == "bets")
                            return new HeroActionInfo("Bet", true, true, false);
                        if (normalizedAction == "checks")
                            return new HeroActionInfo("Check", false, false, false);
                        if (normalizedAction == "folds")
                            return new HeroActionInfo("Fold", false, false, false);
                    }

                    if (isRaise)
                        raisesBeforeHero++;
                }

                return new HeroActionInfo("Sin accion", false, false, false);
            }

            private static IEnumerable<string> GetPreflopLines(IReadOnlyList<string> hand)
            {
                foreach (var line in hand)
                {
                    if (line.StartsWith("*** FLOP", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("*** SUMMARY", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("*** RESUMEN", StringComparison.OrdinalIgnoreCase))
                        yield break;
                    yield return line;
                }
            }

            private static DateTime ExtractHandDateTime(IReadOnlyList<string> hand, DateTime fallback)
            {
                return PokerStarsHandHistory.ExtractTimestamp(hand) ?? fallback;
            }

            private static string NormalizeCombo(string cards)
            {
                var parts = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "--";

                var first = ParseCard(parts[0]);
                var second = ParseCard(parts[1]);
                if (first.rank == second.rank)
                    return $"{first.rank}{second.rank}";

                var firstValue = RankValue(first.rank);
                var secondValue = RankValue(second.rank);
                var high = firstValue >= secondValue ? first : second;
                var low = firstValue >= secondValue ? second : first;
                return $"{high.rank}{low.rank}{(high.suit == low.suit ? "s" : "o")}";
            }

            private static (string rank, char suit) ParseCard(string raw)
            {
                var card = raw.Trim();
                if (card.Length < 2)
                    return (card.ToUpperInvariant(), '?');

                return (card[..^1].ToUpperInvariant(), char.ToLowerInvariant(card[^1]));
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

            private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
            {
                return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
            }

            private static bool TryParseAmount(string raw, out double value)
            {
                if (Hud.App.Services.PokerAmountParser.TryParse(raw, out value))
                    return true;

                raw = raw.Replace("$", "")
                    .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("\u00A0", "")
                    .Replace(" ", "")
                    .Replace(",", ".");
                return double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            }

            private static TagViewModel Tag(string text, string description, TagPalette palette) =>
                new(text, description, palette.Background, palette.Border, palette.Foreground, Array.Empty<CardChipViewModel>());

            private static TagViewModel CardTag(string combo, string text, string description, TagPalette palette) =>
                new(text, description, palette.Background, palette.Border, palette.Foreground, ComboToCardChips(combo));

            private static IReadOnlyList<CardChipViewModel> ComboToCardChips(string combo)
            {
                var cards = ComboToCards(combo);
                return CardChipViewModel.FromCards(cards);
            }

            private static string ComboToCards(string combo)
            {
                if (string.IsNullOrWhiteSpace(combo) || combo.Length < 2)
                    return "";

                var first = combo[0];
                var second = combo[1];
                if (first == second)
                    return $"{first}h {second}d";

                return combo.EndsWith("s", StringComparison.Ordinal)
                    ? $"{first}h {second}h"
                    : $"{first}h {second}d";
            }

            private static TagPalette Accent() =>
                new(BrushFrom(16, 44, 32), BrushFrom(33, 192, 122), BrushFrom(177, 255, 214));

            private static TagPalette Danger() =>
                new(BrushFrom(55, 22, 30), BrushFrom(226, 78, 91), BrushFrom(255, 199, 204));

            private static TagPalette Neutral() =>
                new(BrushFrom(18, 31, 45), BrushFrom(64, 92, 118), BrushFrom(203, 226, 245));

            private static Brush BrushFrom(byte r, byte g, byte b) =>
                new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private sealed record HeroActionInfo(string Label, bool IsVpip, bool IsPfr, bool IsThreeBet);
        private sealed record TagPalette(Brush Background, Brush Border, Brush Foreground);
        private sealed record TagViewModel(
            string Text,
            string Description,
            Brush Background,
            Brush Border,
            Brush Foreground,
            IReadOnlyList<CardChipViewModel> CardChips);
        private sealed record StatMetricRow(string Key, string Label, double Value, string DisplayValue, int Hands, StakeProfile Stake);

        private sealed record HeroHandRow(
            string TableName,
            string SourcePath,
            int HandNumber,
            DateTime PlayedAt,
            string Combo,
            string Cards,
            string Position,
            string Action,
            bool IsVpip,
            bool IsPfr,
            bool IsThreeBet,
            double NetBb)
        {
            public string NetBbLabel => $"{NetBb:+0.#;-0.#;0} bb";
            public string NetBbTrendIcon => TrendIcon(NetBb);
            public Brush NetBbTrendBrush => TrendBrush(NetBb);
            public IReadOnlyList<CardChipViewModel> CardChips => CardChipViewModel.FromCards(Cards);
        }

        private sealed record PositionRow(
            string Position,
            int Hands,
            int VpipHands,
            int PfrHands,
            int ThreeBetHands,
            double TotalBb,
            string WorstHand)
        {
            public string VpipLabel => Hands == 0 ? "0%" : $"{VpipHands * 100.0 / Hands:0.#}%";
            public string PfrLabel => Hands == 0 ? "0%" : $"{PfrHands * 100.0 / Hands:0.#}%";
            public string ThreeBetLabel => Hands == 0 ? "0%" : $"{ThreeBetHands * 100.0 / Hands:0.#}%";
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string BbPer100Label => Hands == 0 ? "0" : $"{TotalBb * 100.0 / Hands:+0.#;-0.#;0}";
            public string TotalBbTrendIcon => TrendIcon(TotalBb);
            public Brush TotalBbTrendBrush => TrendBrush(TotalBb);
            public double BbPer100 => Hands == 0 ? 0 : TotalBb * 100.0 / Hands;
            public string BbPer100TrendIcon => TrendIcon(BbPer100);
            public Brush BbPer100TrendBrush => TrendBrush(BbPer100);

            public static PositionRow From(string position, IReadOnlyList<HeroHandRow> hands)
            {
                var worst = hands.OrderBy(hand => hand.NetBb).FirstOrDefault();
                return new PositionRow(
                    position,
                    hands.Count,
                    hands.Count(hand => hand.IsVpip),
                    hands.Count(hand => hand.IsPfr),
                    hands.Count(hand => hand.IsThreeBet),
                    hands.Sum(hand => hand.NetBb),
                    worst is null ? "-" : $"{worst.Combo} {worst.NetBbLabel}");
            }
        }

        private sealed record ComboRow(
            string Combo,
            int Count,
            int VpipHands,
            int PfrHands,
            double UsagePct,
            double TotalBb,
            string CommonPosition)
        {
            public string UsageLabel => $"{UsagePct:0.#}%";
            public string VpipLabel => Count == 0 ? "0%" : $"{VpipHands * 100.0 / Count:0.#}%";
            public string PfrLabel => Count == 0 ? "0%" : $"{PfrHands * 100.0 / Count:0.#}%";
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string AverageBbLabel => Count == 0 ? "0 bb" : $"{TotalBb / Count:+0.#;-0.#;0} bb";
            public double AverageBb => Count == 0 ? 0 : TotalBb / Count;
            public string TotalBbTrendIcon => TrendIcon(TotalBb);
            public Brush TotalBbTrendBrush => TrendBrush(TotalBb);
            public string AverageBbTrendIcon => TrendIcon(AverageBb);
            public Brush AverageBbTrendBrush => TrendBrush(AverageBb);

            public static ComboRow From(string combo, IReadOnlyList<HeroHandRow> hands, int totalHands)
            {
                var commonPosition = hands
                    .GroupBy(hand => hand.Position)
                    .OrderByDescending(group => group.Count())
                    .FirstOrDefault()?.Key ?? "-";

                return new ComboRow(
                    combo,
                    hands.Count,
                    hands.Count(hand => hand.IsVpip),
                    hands.Count(hand => hand.IsPfr),
                    hands.Count * 100.0 / Math.Max(1, totalHands),
                    hands.Sum(hand => hand.NetBb),
                    commonPosition);
            }
        }

        private sealed record ActionRow(string Action, int Count, double TotalBb, string WorstHand)
        {
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public string AverageBbLabel => Count == 0 ? "0 bb" : $"{TotalBb / Count:+0.#;-0.#;0} bb";
            public double AverageBb => Count == 0 ? 0 : TotalBb / Count;
            public string TotalBbTrendIcon => TrendIcon(TotalBb);
            public Brush TotalBbTrendBrush => TrendBrush(TotalBb);
            public string AverageBbTrendIcon => TrendIcon(AverageBb);
            public Brush AverageBbTrendBrush => TrendBrush(AverageBb);

            public static ActionRow From(string action, IReadOnlyList<HeroHandRow> hands)
            {
                var worst = hands.OrderBy(hand => hand.NetBb).FirstOrDefault();
                return new ActionRow(
                    action,
                    hands.Count,
                    hands.Sum(hand => hand.NetBb),
                    worst is null ? "-" : $"{worst.Combo} {worst.Position}");
            }
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
                > 0 => BrushFrom(33, 192, 122),
                < 0 => BrushFrom(226, 78, 91),
                _ => BrushFrom(135, 145, 156)
            };

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}

