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
        private HeroProfileViewModel? _viewModel;

        public HeroProfileWindow(
            PlayerStats hero,
            IEnumerable<MainWindow.TableSessionStats> tables,
            StakeProfile stake,
            string summary)
        {
            InitializeComponent();
            FitToWorkArea();
            // Construir el perfil lee todos los historiales: en segundo plano.
            var tableList = tables.ToList();
            BackgroundLoad.Run(this, () => HeroProfileViewModel.Build(hero, tableList, stake, summary), viewModel =>
            {
                _viewModel = viewModel;
                DataContext = viewModel;
            });
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

            if (_viewModel is null)
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

            AppNavigator.Open(window);
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
                HandTypeRows = HandTypeProfit.Build(hands.Where(hand => hand.IsVpip)
                        .Select(hand => new HandTypeSample(hand.Combo, hand.Position, hand.NetBb, hand.LuckBb)))
                    .Select(row => new HandTypeViewRow(row))
                    .ToList();
                HandTypeBars = HandTypeRows
                    .OrderByDescending(row => row.Row.AdjustedPerHand)
                    .Select(row => new Charts.BarDatum(row.Row.Name, row.Row.AdjustedPerHand, row.Row.PerHandLabel + " bb",
                        string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Hands"), row.Row.Hands) + " · " + row.SampleLabel,
                        row.Tooltip))
                    .ToList();
                HandTypeNotes = BuildHandTypeNotes(HandTypeRows, hands).ToList();

                // Retroalimentacion con semaforo: la misma del informe PDF (en ficticio, cerrado y por valor).
                var playMoney = tables.Count == 0 || tables.All(table => !table.IsCash);
                var feedback = HeroStatCoach.Evaluate(new HeroStatInput(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct,
                    hero.AF, hero.AFqPct, hero.CBetFlopPct, hero.FoldVsCBetFlopPct, hero.WTSDPct, hero.WSDPct, hero.WWSFPct), playMoney, stake);
                Feedback = feedback.Select(item => new FeedbackRow(item)).ToList();
                FeedbackIntro = LocalizationManager.Text(playMoney ? "HeroFeedback.IntroPlay" : "HeroFeedback.IntroReal");
                FeedbackFocus = feedback.Count == 0 ? LocalizationManager.Text("HeroFeedback.Empty") : HeroStatCoach.Focus(feedback);
                FeedbackSummary = feedback.Count == 0
                    ? ""
                    : string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HeroFeedback.Summary"),
                        feedback.Count(item => item.Verdict == StatVerdict.Good),
                        feedback.Count(item => item.Verdict == StatVerdict.Adjust),
                        feedback.Count(item => item.Verdict == StatVerdict.Bad));
                Tags = BuildTags(hero, feedback, PositionRows, TopCombos).ToList();
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
            public IReadOnlyList<HandTypeViewRow> HandTypeRows { get; }
            public IReadOnlyList<Charts.BarDatum> HandTypeBars { get; }
            public IReadOnlyList<string> HandTypeNotes { get; }
            public IReadOnlyList<FeedbackRow> Feedback { get; }
            public string FeedbackIntro { get; }
            public string FeedbackFocus { get; }
            public string FeedbackSummary { get; }

            // EV por accion en barras: rojo donde la accion pierde, verde donde gana.
            public IReadOnlyList<Charts.BarDatum> ActionBars => ActionRows
                .OrderBy(row => row.TotalBb)
                .Select(row => new Charts.BarDatum(
                    row.Action,
                    row.TotalBb,
                    row.TotalBbLabel,
                    $"{row.Count} {Hud.App.Services.LocalizationManager.Text("Visual.Hands")}",
                    $"{row.Action}: {row.Count} {Hud.App.Services.LocalizationManager.Text("Visual.Hands")}\n" +
                    $"{Hud.App.Services.LocalizationManager.Text("Common.Average")}: {row.AverageBbLabel}\n" +
                    $"{Hud.App.Services.LocalizationManager.Text("Common.WorstHand")}: {row.WorstHand}"))
                .ToList();
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

            /// <summary>Lectura del reporte por tipo de mano en frases: lo claro, lo que aun es suerte y la suerte total.</summary>
            private static IEnumerable<string> BuildHandTypeNotes(IReadOnlyList<HandTypeViewRow> rows, IReadOnlyList<HeroHandRow> hands)
            {
                if (rows.Count == 0)
                {
                    yield return LocalizationManager.Text("HandTypes.Empty");
                    yield break;
                }

                string Bb(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
                var clear = rows.Where(row => row.Row.IsClear).ToList();
                var best = clear.Where(row => row.Row.AdjustedPerHand > 0).OrderByDescending(row => row.Row.AdjustedPerHand).FirstOrDefault();
                var worst = clear.Where(row => row.Row.AdjustedPerHand < 0).OrderBy(row => row.Row.AdjustedPerHand).FirstOrDefault();
                if (best is not null)
                    yield return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Note.Best"), best.Row.Name, Bb(best.Row.AdjustedPerHand), best.Row.Hands);
                if (worst is not null)
                    yield return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Note.Worst"), worst.Row.Name, Bb(worst.Row.AdjustedPerHand), worst.Row.Hands);
                if (clear.Count == 0)
                    yield return LocalizationManager.Text("HandTypes.Note.NoClear");

                var played = rows.Sum(row => row.Row.Hands);
                var most = rows.OrderByDescending(row => row.Row.Hands).First();
                yield return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Note.MostPlayed"),
                    most.Row.Name, most.Row.Hands, Math.Round(most.Row.Hands * 100.0 / Math.Max(1, played)));

                var luck = hands.Where(hand => hand.IsVpip).Sum(hand => hand.LuckBb);
                if (Math.Abs(luck) >= 1)
                    yield return string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Note.Luck"), luck.ToString("+0;-0;0", CultureInfo.CurrentCulture));
                yield return LocalizationManager.Text("HandTypes.Note.Position");
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
                IReadOnlyList<StatFeedback> feedback,
                IReadOnlyList<PositionRow> positions,
                IReadOnlyList<ComboRow> combos)
            {
                yield return Tag(
                    ClassifyProfile(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct, hero.AF),
                    LocalizationManager.Text("Tag.ProfileBaseHero.Desc"),
                    Neutral());

                if (hero.HandsReceived < 30)
                    yield return Tag(LocalizationManager.Text("Tag.NoSample"), LocalizationManager.Text("Tag.NoSample.Short"), Neutral());

                // Las mismas etiquetas que el panel y el informe: salen de la retroalimentacion con semaforo.
                foreach (var tag in HeroStatCoach.Tags(feedback))
                    yield return Tag(tag.Text, tag.Description, tag.Verdict switch
                    {
                        StatVerdict.Good => Accent(),
                        StatVerdict.Adjust => Warning(),
                        _ => Danger()
                    });

                var worst = positions.Where(position => position.Hands > 0).OrderBy(position => position.TotalBb).FirstOrDefault();
                if (worst is not null)
                    yield return Tag(
                        $"Leak {worst.Position}",
                        string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.LeakPosition.Dynamic"), worst.Position, worst.TotalBbLabel, worst.BbPer100Label),
                        Danger());

                var top = combos.FirstOrDefault();
                if (top is not null)
                    yield return CardTag(
                        top.Combo,
                        "lover",
                        string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.ComboLover.Dynamic"), top.Combo, top.Count, top.UsageLabel),
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
                    yield return Tag($"{LocalizationManager.Text("Tag.PremiumLover")} - {premium}/{total}", LocalizationManager.Text("Tag.PremiumLoverHero.Desc"), Neutral());
                if (low >= 3 && low * 100.0 / total >= 25)
                    yield return Tag($"{LocalizationManager.Text("Tag.LowHands")} - {low}/{total}", LocalizationManager.Text("Tag.LowHandsHero.Desc"), Neutral());
                if (suitedConnectors >= 3 && suitedConnectors * 100.0 / total >= 20)
                    yield return Tag($"Suited connectors - {suitedConnectors}/{total}", LocalizationManager.Text("Tag.SuitedConnectorsHero.Desc"), Neutral());
                if (categories >= 4 && combos.Count >= 10)
                    yield return Tag(LocalizationManager.Text("Tag.Mixed"), LocalizationManager.Text("Tag.MixedHero.Desc"), Neutral());
            }

            private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af) =>
                Hud.App.Services.PlayerProfileClassifier.Classify(hands, vpip, pfr, threeBet, af);

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
                    yield return string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("HeroNote.WorstPosition"), worstPosition.Position, worstPosition.TotalBbLabel, worstPosition.BbPer100Label);

                var bestPosition = positions.Where(row => row.Hands > 0).OrderByDescending(row => row.TotalBb).FirstOrDefault();
                if (bestPosition is not null)
                    yield return string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("HeroNote.BestPosition"), bestPosition.Position, bestPosition.TotalBbLabel);

                var worstAction = actions.OrderBy(row => row.TotalBb).FirstOrDefault();
                if (worstAction is not null)
                    yield return string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("HeroNote.WorstAction"), worstAction.Action, worstAction.TotalBbLabel);

                var topCombo = combos.FirstOrDefault();
                if (topCombo is not null)
                    yield return string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("HeroNote.TopCombo"), topCombo.Combo, topCombo.Count, topCombo.UsageLabel);

                if (hero.FoldVsCBetFlopPct >= 65)
                    yield return LocalizationManager.Text("HeroNote.HighFoldCBet");
                if (hero.WTSDPct >= 30 && hero.WSDPct < 50)
                    yield return LocalizationManager.Text("HeroNote.ShowdownReview");
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
                    var luckBb = action.IsVpip ? AllInLuck(hand, table.HeroName) : 0;

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
                        netBb,
                        luckBb);
                }
            }

            /// <summary>Suerte del heroe en un all-in con showdown antes del river (0 si no lo hubo).</summary>
            private static double AllInLuck(IReadOnlyList<string> hand, string heroName)
            {
                if (!hand.Any(line => line.Contains("all-in", StringComparison.OrdinalIgnoreCase)) ||
                    !hand.Any(line => line.StartsWith("*** SHOW", StringComparison.OrdinalIgnoreCase)))
                    return 0;
                try
                {
                    return Hud.App.Services.Trainer.TrainerHandParser.Parse(hand, heroName) is { } parsed
                        ? HandTypeProfit.AllInLuckBb(parsed)
                        : 0;
                }
                catch
                {
                    return 0;
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
                    ? AsProfilePosition(position)
                    : "?";
            }

            // La tabla comun llama HJ al asiento previo al CO; este perfil lo agrupa como MP,
            // igual que su antigua copia de la tabla: PositionOrder no tiene HJ y esas manos se
            // quedaban fuera de la tabla de posiciones.
            private static string AsProfilePosition(string position) =>
                position == "HJ" ? "MP" : position;

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

                return new HeroActionInfo(LocalizationManager.Text("Common.NoAction"), false, false, false);
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

            private static TagPalette Warning() =>
                new(BrushFrom(56, 40, 12), BrushFrom(224, 160, 40), BrushFrom(255, 226, 170));

            private static TagPalette Neutral() =>
                new(Hud.App.Services.ThemeBrushes.NeutralBackground, Hud.App.Services.ThemeBrushes.NeutralBorder, Hud.App.Services.ThemeBrushes.Text);

            private static Brush BrushFrom(byte r, byte g, byte b) =>
                BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
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
        private sealed record StatMetricRow(string Key, string Label, double Value, string DisplayValue, int Hands, StakeProfile Stake)
        {
            /// <summary>
            /// Tooltip de la caja: primero que nivel es su color (la escala de todo APH) y luego que
            /// significa la stat, la referencia de 6-max y el valor del heroe.
            /// </summary>
            public string Help => StatScale.Describe(Key, Value, Hands, Stake) + Environment.NewLine + Environment.NewLine +
                string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HeroStats.Help"),
                LocalizationManager.Text(Key switch
                {
                    "VPIP" => "Metric.VPIP.Desc.Hero",
                    "PFR" => "Metric.PFR.Desc.Hero",
                    "THREEBET" => "Metric.ThreeBet.Desc",
                    "AF" => "Metric.AF.Desc",
                    "AFQ" => "Metric.AFq.Desc.Hero",
                    "CBF" => "Metric.CBet.Desc.Hero",
                    "FVCBF" => "Metric.FvCBet.Desc.Hero",
                    "WTSD" => "Metric.WTSD.Desc.Hero",
                    "WSD" => "Metric.WSD.Desc",
                    _ => "Metric.WWSF.Desc.Hero"
                }),
                LocalizationManager.Text("StatRef." + Key), DisplayValue, Hands);
        }

        /// <summary>Una fila de la retroalimentacion: veredicto con su color, stat, nivel (color de la escala) y que hacer.</summary>
        private sealed record FeedbackRow(StatFeedback Item)
        {
            public string Name => Item.Name;
            public string ValueLabel => Item.ValueLabel;
            public string LevelLabel => Item.LevelLabel;
            public string VerdictText => $"{Item.VerdictIcon}  {Item.VerdictLabel}";
            public string Advice => Item.Advice;
            public string LevelTip => StatScale.RangeOf(Item.Level);
            public Brush VerdictBrush => BackgroundLoad.Frozen(new SolidColorBrush(HeroStatCoach.ColorOf(Item.Verdict)));
            public Brush LevelBrush => StatScale.ColorOf(Item.Level) is { } color
                ? BackgroundLoad.Frozen(new SolidColorBrush(color))
                : ThemeBrushes.NeutralBackground;
            public Brush LevelInk => BackgroundLoad.Frozen(new SolidColorBrush(StatScale.InkOf(Item.Level)));
        }

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
            double NetBb,
            double LuckBb = 0)
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

        /// <summary>Fila del reporte por tipo de mano para la tabla: textos, colores y la explicacion del tooltip.</summary>
        private sealed record HandTypeViewRow(HandTypeRow Row)
        {
            public string Name => Row.Name;
            public int Hands => Row.Hands;
            public string PerHandLabel => Row.PerHandLabel;
            public string RawPerHandLabel => Row.RawPerHandLabel;
            public string MarginLabel => Row.MarginLabel;
            public string LateLabel => Row.GroupLabel("Late");
            public string EarlyLabel => Row.GroupLabel("Early");
            public string BlindsLabel => Row.GroupLabel("Blinds");
            public string SampleLabel => LocalizationManager.Text("HandTypes.Sample." + Row.SampleKey);
            public string PerHandTrendIcon => TrendIcon(Row.AdjustedPerHand);
            public Brush PerHandTrendBrush => TrendBrush(Row.AdjustedPerHand);

            /// <summary>En negrita las filas con un resultado claro (mas que su margen de error).</summary>
            public FontWeight Weight => Row.IsClear ? FontWeights.Bold : FontWeights.Normal;

            /// <summary>Muestra: gris si es muy poca, ambar poca, azul media, verde buena.</summary>
            public Brush SampleBrush => Row.SampleKey switch
            {
                "VeryLow" => BrushFrom(135, 145, 156),
                "Low" => BrushFrom(224, 170, 60),
                "Medium" => BrushFrom(79, 143, 214),
                _ => BrushFrom(33, 192, 122)
            };

            public string Tooltip => string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("HandTypes.Tooltip"),
                Row.Name, Row.Hands, Row.PerHandLabel, Row.MarginLabel, Row.RawPerHandLabel, Row.TotalLabel);
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
            BackgroundLoad.Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
    }
}

