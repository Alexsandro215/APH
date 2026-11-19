using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services.Trainer;

namespace Hud.App.Services
{
    public sealed record RealTimePlayerReportRow(
        string Name,
        int Hands,
        double VPIP,
        double PFR,
        double ThreeBet,
        double AF,
        double AFq,
        double CBet,
        double FoldVsCBet,
        double WTSD,
        double WSD,
        double WWSF)
    {
        /// <summary>Los contadores de cada stat (para sumar mesas bien), si la fila sale de un PlayerStats.</summary>
        public RealTimeStatCounts? Counts { get; init; }
    }

    /// <summary>
    /// Contadores de las stats de un jugador en una mesa. Al juntar mesas se suman y se divide al final:
    /// si se promedian los porcentajes por manos, una mesa sin ninguna c-bet afrontada cuenta como "0% de
    /// fold" y hunde el total (FvCB 35% en el informe de la noche cuando eran 16 de 26, el 62%).
    /// </summary>
    public sealed record RealTimeStatCounts(
        int Hands, int Vpip, int Pfr, int ThreeBet, int ThreeBetOpp, int PostflopAggressive, int PostflopCalls,
        int CBetMade, int CBetOpp, int FoldToCBet, int FacedCBet, int SawFlop, int WentToShowdown, int WonAtShowdown,
        int WonWhenSawFlop);

    public sealed record RealTimeTableReportRow(
        string TableName,
        string? SourcePath,
        string? HeroName,
        bool IsRunning,
        long LinesRead,
        DateTime? FirstHandTime,
        DateTime? LastHandTime,
        IReadOnlyList<RealTimePlayerReportRow> Players);

    public static partial class RealTimeSessionReportService
    {
        private static readonly Regex BlindsRx =
            new(@"\((?:\$)?(?<small>[\d,.]+)\s*/\s*(?:\$)?(?<big>[\d,.]+)\)", RegexOptions.Compiled);

        private static readonly Regex HeaderBlindsRx =
            new(@"(?:\(|\s)(?:\$)?(?<small>[\d,.]+)\s*/\s*(?:\$)?(?<big>[\d,.]+)(?:\)|\s)", RegexOptions.Compiled);

        private static readonly Regex CurrencyAmountRx =
            new(@"(?:[$€]\s*\d|\d[\d,.]*\s*(?:USD|EUR))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string SavePdf(IReadOnlyList<RealTimeTableReportRow> tables, string? folderOverride = null)
        {
            var folder = folderOverride ?? ReportSessionIndexService.GetReportsFolder();
            Directory.CreateDirectory(folder);

            var fileName = $"APH_RT_Session_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var path = Path.Combine(folder, fileName);

            var model = BuildReport(tables);
            StyledPdfWriter.Write(path, model);
            if (folderOverride is not null)
                return path;
            PdfReportProtectionService.ProtectIfEnabled(path);
            ReportSessionIndexService.SaveMetadata(new ReportSessionRecord(
                0,
                path,
                model.GeneratedAt,
                model.FirstHand,
                model.LastHand,
                model.RegisteredTables,
                model.Duration,
                model.Hero,
                $"{FormatMoneyText(model.NetAmount, model.IsCash)} / {model.NetBb:+0.#;-0.#;0} bb",
                model.TotalHands,
                $"VPIP: {model.AverageStats.VPIP:0.#} / PFR: {model.AverageStats.PFR:0.#}",
                tables.Where(t => !string.IsNullOrWhiteSpace(t.SourcePath)).Select(t => t.SourcePath!).ToList()));
            return path;
        }

        /// <summary>
        /// Informe de una sesion ya jugada (desde Sesiones): las manos de cada mesa entre el inicio y el
        /// fin de la sesion (hora del historial), con las stats de esos jugadores calculadas de esas manos.
        /// </summary>
        public static string SavePdfForSession(IEnumerable<MainWindow.TableSessionStats> tables, DateTime start, DateTime end, string? folderOverride = null)
        {
            var rows = RowsInWindow(tables.Select(table => (table.TableName, table.SourcePath, (string?)table.HeroName)), start, end);
            if (rows.Count == 0)
                throw new InvalidOperationException("No hay manos de esa sesion en los historiales.");
            return SavePdf(rows, folderOverride);
        }

        /// <summary>
        /// Informe al terminar Tiempo real: solo el ultimo tramo de juego seguido (sin huecos de mas de 30
        /// minutos entre manos de todas las mesas). Antes cada mesa ponia su historial entero del dia y una
        /// mesa jugada horas antes metia esas manos (Letaba IV y Tanete V desde las 23:14 en la sesion de
        /// las 02:55). Null si no hay manos en los historiales (se usa el informe de siempre).
        /// </summary>
        public static string? SavePdfForLastStretch(IEnumerable<(string TableName, string Path, string? Hero)> tables, string? folderOverride = null)
        {
            var list = tables.Where(table => !string.IsNullOrWhiteSpace(table.Path) && File.Exists(table.Path)).ToList();
            var times = list
                .SelectMany(table => PokerStarsHandHistory.SplitHands(File.ReadLines(table.Path))
                    .Select(PokerStarsHandHistory.ExtractTimestamp))
                .Where(at => at.HasValue)
                .Select(at => at!.Value)
                .OrderBy(at => at)
                .ToList();
            if (times.Count == 0)
                return null;
            var (start, end) = LastStretch(times, TimeSpan.FromMinutes(30));
            var rows = RowsInWindow(list.Select(table => (table.TableName, table.Path, table.Hero)), start, end);
            return rows.Count == 0 ? null : SavePdf(rows, folderOverride);
        }

        /// <summary>El ultimo tramo de tiempos seguidos (sin huecos mayores que <paramref name="gap"/>).</summary>
        public static (DateTime Start, DateTime End) LastStretch(IReadOnlyList<DateTime> sortedTimes, TimeSpan gap)
        {
            var end = sortedTimes[^1];
            var start = end;
            for (var i = sortedTimes.Count - 1; i > 0; i--)
            {
                if (sortedTimes[i] - sortedTimes[i - 1] > gap)
                    break;
                start = sortedTimes[i - 1];
            }

            return (start, end);
        }

        private static List<RealTimeTableReportRow> RowsInWindow(IEnumerable<(string TableName, string Path, string? Hero)> tables, DateTime start, DateTime end)
        {
            var rows = new List<RealTimeTableReportRow>();
            foreach (var table in tables)
            {
                if (!File.Exists(table.Path))
                    continue;
                var hands = PokerStarsHandHistory.SplitHands(File.ReadLines(table.Path))
                    .Select(hand => (Hand: hand, At: PokerStarsHandHistory.ExtractTimestamp(hand)))
                    .Where(item => item.At is { } at && at >= start.AddSeconds(-1) && at <= end.AddSeconds(1))
                    .ToList();
                if (hands.Count == 0)
                    continue;

                var aggregator = new StatsAggregator();
                new PokerStarsParser(aggregator).FeedLines(hands.SelectMany(item => item.Hand.Append("")).ToList(), () => { });
                rows.Add(new RealTimeTableReportRow(
                    $"{table.TableName}",
                    table.Path,
                    table.Hero,
                    false,
                    hands.Sum(item => item.Hand.Count),
                    hands.Min(item => item.At),
                    hands.Max(item => item.At),
                    aggregator.Players.Values.Select(FromStats).ToList()));
            }

            return rows;
        }

        public static RealTimePlayerReportRow FromStats(PlayerStats player) =>
            new RealTimePlayerReportRow(
                player.Name,
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
                player.WWSFPct)
            {
                Counts = new RealTimeStatCounts(player.HandsReceived, player.VpipCount, player.PfrCount, player.ThreeBetCount,
                    player.ThreeBetOpportunities, player.PostflopBetsAndRaises, player.PostflopCalls, player.CBetFlopMade,
                    player.CBetFlopOpportunities, player.FoldToCBetFlop, player.FacedCBetFlop, player.SawFlop,
                    player.WentToShowdown, player.WonAtShowdown, player.WonWhenSawFlop)
            };

        private static SessionReportModel BuildReport(IReadOnlyList<RealTimeTableReportRow> tables)
        {
            var active = tables.Where(table => table.SourcePath is not null || table.Players.Count > 0).ToList();
            var hero = active.Select(table => table.HeroName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "-";
            var first = active.Select(table => table.FirstHandTime).Where(time => time.HasValue).Min();
            var last = active.Select(table => table.LastHandTime).Where(time => time.HasValue).Max();
            var mergedPlayers = active
                .SelectMany(table => table.Players)
                .GroupBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => MergePlayer(group.ToList()))
                .OrderByDescending(player => player.Hands)
                .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var heroStats = mergedPlayers.FirstOrDefault(player => string.Equals(player.Name, hero, StringComparison.OrdinalIgnoreCase));
            var totalHands = active.Sum(table =>
            {
                var heroRow = table.Players.FirstOrDefault(player => string.Equals(player.Name, table.HeroName, StringComparison.OrdinalIgnoreCase));
                return heroRow?.Hands ?? table.Players.Select(player => player.Hands).DefaultIfEmpty(0).Max();
            });
            var results = active.Select(EstimateHeroResult).ToList();
            var netAmount = results.Sum(result => result.NetAmount);
            var isCash = active.Any(table => table.TableName.Contains('$', StringComparison.Ordinal) || results.Any(result => result.IsCash));
            var tableModels = active.Select(table =>
            {
                var result = EstimateHeroResult(table);
                var bigBlind = DetectBigBlind(table);
                var heroRow = table.Players.FirstOrDefault(player => string.Equals(player.Name, table.HeroName, StringComparison.OrdinalIgnoreCase));
                var handImpacts = AnalyzeHeroHands(table, bigBlind).ToList();
                return new TableReportModel(
                    table,
                    result.NetAmount,
                    result.IsCash,
                    bigBlind,
                    bigBlind > 0 ? result.NetAmount / bigBlind : 0,
                    heroRow,
                    handImpacts.OrderByDescending(hand => hand.NetAmount).FirstOrDefault(),
                    handImpacts.OrderBy(hand => hand.NetAmount).FirstOrDefault(),
                    handImpacts,
                    table.Players
                        .OrderByDescending(player => player.Hands)
                        .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList());
            }).ToList();
            var averageBigBlind = tableModels.Where(table => table.BigBlind > 0).Select(table => table.BigBlind).DefaultIfEmpty(0).Average();
            var netBb = averageBigBlind > 0 ? netAmount / averageBigBlind : 0;
            var duration = first.HasValue && last.HasValue && last.Value >= first.Value ? last.Value - first.Value : TimeSpan.Zero;
            var bestTable = tableModels.OrderByDescending(table => table.NetAmount).FirstOrDefault();
            var worstTable = tableModels.OrderBy(table => table.NetAmount).FirstOrDefault();
            var heroSummary = new HeroSessionSummary(
                heroStats ?? MergePlayer(mergedPlayers),
                netAmount,
                netBb,
                bestTable?.Table.TableName ?? "-",
                worstTable?.Table.TableName ?? "-");
            var bestHand = tableModels.Select(table => table.BestHand).Where(hand => hand is not null).OrderByDescending(hand => hand!.NetAmount).FirstOrDefault();
            var worstHand = tableModels.Select(table => table.WorstHand).Where(hand => hand is not null).OrderBy(hand => hand!.NetAmount).FirstOrDefault();
            // Retroalimentacion con semaforo (la misma del perfil del heroe): avisos, notas y enfoque salen de aqui.
            var feedback = HeroStatCoach.Evaluate(ToInput(heroSummary.Stats), playMoney: !isCash);
            var coaching = BuildCoaching(active, tableModels);
            var alerts = BuildAlerts(heroSummary.Stats, feedback, coaching.DecisionVerdict).ToList();
            var rivalImpacts = BuildRivalImpacts(tableModels, hero).ToList();

            return new SessionReportModel(
                GeneratedAt: DateTime.Now,
                Hero: hero,
                RegisteredTables: active.Count,
                ActiveTables: active.Count(table => table.IsRunning),
                FirstHand: first,
                LastHand: last,
                TotalHands: totalHands,
                NetAmount: netAmount,
                NetBb: netBb,
                IsCash: isCash,
                Duration: duration,
                AverageStats: heroStats ?? MergePlayer(mergedPlayers),
                HeroSummary: heroSummary,
                Alerts: alerts,
                BestHand: bestHand,
                WorstHand: worstHand,
                RivalImpacts: rivalImpacts,
                ClosingNotes: BuildClosingNotes(netAmount, isCash, duration, heroSummary.Stats, feedback, tableModels, bestTable, worstTable, worstHand).ToList(),
                Feedback: feedback,
                Focus: heroSummary.Stats.Hands < StatScale.MinHands
                    ? "Juega más manos para tener una lectura fiable de tus stats."
                    : HeroStatCoach.Focus(feedback, coaching.DecisionVerdict),
                Tables: tableModels,
                TopPlayers: mergedPlayers.Take(20).ToList(),
                Coaching: coaching);
        }

        /// <summary>
        /// Lo que el informe explica en claro: suerte en los all-in, como siguio los consejos de APH en
        /// vivo (registro de revisiones) y que tipos de mano le dejaron dinero en esta sesion.
        /// </summary>
        private static CoachingSummary BuildCoaching(IReadOnlyList<RealTimeTableReportRow> tables, IReadOnlyList<TableReportModel> models)
        {
            var samples = new List<HandTypeSample>();
            var allIns = new List<(DateTime? At, string Table, string Cards, double NetBb, HandTypeProfit.AllInResult Result)>();
            var curve = new List<CurvePoint>();
            var luckByHand = new Dictionary<string, double>(StringComparer.Ordinal);
            var handIds = new HashSet<string>(StringComparer.Ordinal);
            var hands = 0;
            var vpip = 0;
            var pfr = 0;
            foreach (var table in tables)
            {
                if (string.IsNullOrWhiteSpace(table.HeroName))
                    continue;
                var bigBlind = models.FirstOrDefault(model => ReferenceEquals(model.Table, table))?.BigBlind ?? 0;
                foreach (var lines in SessionHands(table))
                {
                    if (TrainerHandParser.Parse(lines, table.HeroName) is not { HasHero: true } hand || hand.BigBlind <= 0)
                        continue;
                    hands++;
                    handIds.Add(hand.HandId);
                    var preflop = hand.Actions.Where(action => action.Street == "PREFLOP" &&
                                                               PokerStarsHandHistory.SamePlayer(action.Player, hand.HeroName)).ToList();
                    var played = preflop.Any(action => action.Kind is TrainerActionKind.Call or TrainerActionKind.Raise or TrainerActionKind.Bet);
                    if (played) vpip++;
                    if (preflop.Any(action => action.Kind == TrainerActionKind.Raise)) pfr++;
                    var net = PokerStarsHandHistory.EstimateNetForPlayer(lines, hand.HeroName) / hand.BigBlind;
                    var allIn = HandTypeProfit.AllInDetail(hand);
                    curve.Add(new CurvePoint(PokerStarsHandHistory.ExtractTimestamp(lines) ?? DateTime.MinValue, net, allIn?.LuckBb ?? 0));
                    if (allIn is not null)
                        luckByHand[hand.HandId] = allIn.LuckBb;
                    if (allIn is not null)
                        allIns.Add((PokerStarsHandHistory.ExtractTimestamp(lines), table.TableName, TrainerCards.ToText(hand.HeroCards), net, allIn));
                    if (played)
                        samples.Add(new HandTypeSample(TrainerCards.ComboName(hand.HeroCards[0], hand.HeroCards[1]),
                            hand.SeatOf(hand.HeroName)?.Position ?? "?", net, allIn?.LuckBb ?? 0));
                }
            }

            // Consejos en vivo: el registro de revisiones de los dias de la sesion, solo estas manos.
            var days = tables.SelectMany(table => new[] { table.FirstHandTime, table.LastHandTime })
                .Where(time => time.HasValue)
                .Select(time => DateTime.SpecifyKind(time!.Value, DateTimeKind.Utc).ToLocalTime().Date)
                .Distinct();
            var entries = days.SelectMany(TrainerReviewLog.Read).Where(entry => handIds.Contains(entry.HandId))
                .GroupBy(entry => entry.HandId).Select(group => group.Last()).ToList();
            var decisions = entries.SelectMany(entry => entry.Decisions.Select(decision => (Entry: entry, Decision: decision))).ToList();
            var withAdvice = decisions.Where(item => !string.IsNullOrWhiteSpace(item.Decision.LiveRecommendation)).ToList();
            var exploit = entries.Where(entry => entry.Decisions.Any(decision => decision.LiveSource?.StartsWith("Explotar", StringComparison.Ordinal) == true)).ToList();
            // Errores por mano, con lo perdido sin la suerte de los all-in: un buen all-in que perdio por
            // mala suerte no es de los errores caros.
            var mistakeHands = decisions.Where(item => item.Decision.Grade == "Mistake")
                .GroupBy(item => item.Entry.HandId)
                .Select(group => group.First())
                .Select(item => new MistakeLine(item.Entry.At, item.Entry.Table, item.Entry.HeroCards, item.Decision.Street,
                    item.Decision.Chosen, item.Decision.LiveRecommendation, item.Entry.NetBb, item.Decision.Explanation,
                    luckByHand.TryGetValue(item.Entry.HandId, out var luck) ? luck : 0))
                .ToList();
            var mistakes = mistakeHands.OrderBy(item => item.AdjustedBb).Take(3).ToList();
            var decisionVerdict = HeroStatCoach.EvaluateDecisions(
                decisions.Count,
                withAdvice.Count,
                withAdvice.Count(item => item.Decision.FollowedLive == true),
                decisions.Count(item => item.Decision.Grade == "Mistake"),
                mistakeHands.Select(item => new DecisionMistake(FormatCombo(item.Cards), item.Chosen, item.Advice, item.AdjustedBb)).ToList());

            var luckLines = allIns.OrderBy(item => -Math.Abs(item.Result.LuckBb)).Take(6)
                .Select(item => new AllInLine(
                    item.At,
                    item.Table,
                    item.Cards,
                    string.Join(" y ", item.Result.Opponents.Select(cards => TrainerCards.ToText(cards))),
                    item.Result.Street switch { "FLOP" => "Flop", "TURN" => "Turn", _ => "Preflop" },
                    item.Result.Equity,
                    item.NetBb,
                    item.Result.LuckBb))
                .ToList();

            return new CoachingSummary(
                hands,
                models.Sum(model => model.NetBb),
                allIns.Sum(item => item.Result.LuckBb),
                hands == 0 ? 0 : vpip * 100.0 / hands,
                hands == 0 ? 0 : pfr * 100.0 / hands,
                luckLines,
                decisions.Count,
                withAdvice.Count,
                withAdvice.Count(item => item.Decision.FollowedLive == true),
                decisions.Count(item => item.Decision.Grade == "Mistake"),
                exploit.Count,
                exploit.Sum(entry => entry.NetBb),
                mistakes,
                HandTypeProfit.Build(samples, key => LocalizationManager.Text("HandType." + key)),
                curve.OrderBy(point => point.At).ToList(),
                decisionVerdict);
        }

        /// <summary>Junta las filas de un jugador en varias mesas (publico para las pruebas).</summary>
        public static RealTimePlayerReportRow MergePlayerRows(IReadOnlyList<RealTimePlayerReportRow> rows) => MergePlayer(rows);

        private static RealTimePlayerReportRow MergePlayer(IReadOnlyList<RealTimePlayerReportRow> rows)
        {
            if (rows.Count == 0)
                return new RealTimePlayerReportRow("-", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            // Con los contadores de todas las mesas: se suman y cada stat se divide por sus propias
            // oportunidades (como en el HUD).
            if (rows.All(row => row.Counts is not null))
            {
                var c = rows.Select(row => row.Counts!).ToList();
                int Sum(Func<RealTimeStatCounts, int> pick) => c.Sum(pick);
                double Pct(int part, int whole) => whole > 0 ? 100.0 * part / whole : 0;
                var aggressive = Sum(x => x.PostflopAggressive);
                var calls = Sum(x => x.PostflopCalls);
                var counts = new RealTimeStatCounts(Sum(x => x.Hands), Sum(x => x.Vpip), Sum(x => x.Pfr), Sum(x => x.ThreeBet),
                    Sum(x => x.ThreeBetOpp), aggressive, calls, Sum(x => x.CBetMade), Sum(x => x.CBetOpp), Sum(x => x.FoldToCBet),
                    Sum(x => x.FacedCBet), Sum(x => x.SawFlop), Sum(x => x.WentToShowdown), Sum(x => x.WonAtShowdown),
                    Sum(x => x.WonWhenSawFlop));
                return new RealTimePlayerReportRow(
                    rows[0].Name,
                    rows.Sum(row => row.Hands),
                    Pct(counts.Vpip, counts.Hands),
                    Pct(counts.Pfr, counts.Hands),
                    Pct(counts.ThreeBet, counts.ThreeBetOpp),
                    calls == 0 ? aggressive : (double)aggressive / calls,
                    Pct(aggressive, aggressive + calls),
                    Pct(counts.CBetMade, counts.CBetOpp),
                    Pct(counts.FoldToCBet, counts.FacedCBet),
                    Pct(counts.WentToShowdown, counts.SawFlop),
                    Pct(counts.WonAtShowdown, counts.WentToShowdown),
                    Pct(counts.WonWhenSawFlop, counts.SawFlop)) { Counts = counts };
            }

            var totalHands = Math.Max(1, rows.Sum(row => row.Hands));
            double Weighted(Func<RealTimePlayerReportRow, double> selector) =>
                rows.Sum(row => selector(row) * row.Hands) / totalHands;

            return new RealTimePlayerReportRow(
                rows[0].Name,
                rows.Sum(row => row.Hands),
                Weighted(row => row.VPIP),
                Weighted(row => row.PFR),
                Weighted(row => row.ThreeBet),
                Weighted(row => row.AF),
                Weighted(row => row.AFq),
                Weighted(row => row.CBet),
                Weighted(row => row.FoldVsCBet),
                Weighted(row => row.WTSD),
                Weighted(row => row.WSD),
                Weighted(row => row.WWSF));
        }

        private static double DetectBigBlind(RealTimeTableReportRow table)
        {
            if (TryReadBigBlind(table.TableName, out var tableBlind))
                return tableBlind;

            if (!string.IsNullOrWhiteSpace(table.SourcePath) && File.Exists(table.SourcePath))
            {
                foreach (var line in File.ReadLines(table.SourcePath).Take(20))
                {
                    if (TryReadBigBlind(line, out var fileBlind))
                        return fileBlind;
                }
            }

            return 0;
        }

        private static bool TryReadBigBlind(string text, out double bigBlind)
        {
            foreach (var match in new[] { BlindsRx.Match(text), HeaderBlindsRx.Match(text) })
            {
                if (match.Success && PokerAmountParser.TryParse(match.Groups["big"].Value, out bigBlind))
                    return true;
            }

            bigBlind = 0;
            return false;
        }

        private static IEnumerable<HandImpact> AnalyzeHeroHands(RealTimeTableReportRow table, double bigBlind)
        {
            if (string.IsNullOrWhiteSpace(table.SourcePath) ||
                string.IsNullOrWhiteSpace(table.HeroName) ||
                !File.Exists(table.SourcePath))
            {
                yield break;
            }

            var handNumber = 0;
            foreach (var hand in SessionHands(table))
            {
                handNumber++;
                if (!PokerStarsHandHistory.HandHasPlayerActivity(hand, table.HeroName))
                    continue;

                var net = PokerStarsHandHistory.EstimateNetForPlayer(hand, table.HeroName);
                if (Math.Abs(net) < 0.0001)
                    continue;

                var combo = PokerStarsHandHistory.TryGetDealtCards(hand, table.HeroName, out var cards)
                    ? FormatCombo(cards)
                    : "-";
                var stamp = PokerStarsHandHistory.ExtractTimestamp(hand);
                yield return new HandImpact(
                    table.TableName,
                    handNumber,
                    combo,
                    net,
                    bigBlind > 0 ? net / bigBlind : 0,
                    stamp);
            }
        }

        /// <summary>"4s As" -> "A4s" (la alta primero; s si son del mismo palo, o si no).</summary>
        private static string FormatCombo(string cards)
        {
            var parsed = Regex.Matches(cards, @"(?<rank>[2-9TJQKA])(?<suit>[cdhs])", RegexOptions.IgnoreCase)
                .Select(match => (Rank: char.ToUpperInvariant(match.Groups["rank"].Value[0]), Suit: char.ToLowerInvariant(match.Groups["suit"].Value[0])))
                .Take(2)
                .ToList();
            if (parsed.Count != 2)
                return cards;
            const string order = "23456789TJQKA";
            var (high, low) = order.IndexOf(parsed[0].Rank) >= order.IndexOf(parsed[1].Rank) ? (parsed[0], parsed[1]) : (parsed[1], parsed[0]);
            return high.Rank == low.Rank ? $"{high.Rank}{low.Rank}" : $"{high.Rank}{low.Rank}{(high.Suit == low.Suit ? "s" : "o")}";
        }

        private static HeroStatInput ToInput(RealTimePlayerReportRow hero) =>
            new(hero.Hands, hero.VPIP, hero.PFR, hero.ThreeBet, hero.AF, hero.AFq, hero.CBet, hero.FoldVsCBet, hero.WTSD, hero.WSD, hero.WWSF)
            {
                Opportunities = hero.Counts is not { } c ? null : new Dictionary<string, int>
                {
                    ["THREEBET"] = c.ThreeBetOpp,
                    ["AF"] = c.PostflopAggressive + c.PostflopCalls,
                    ["AFQ"] = c.PostflopAggressive + c.PostflopCalls,
                    ["CBF"] = c.CBetOpp,
                    ["FVCBF"] = c.FacedCBet,
                    ["WTSD"] = c.SawFlop,
                    ["WSD"] = c.WentToShowdown,
                    ["WWSF"] = c.SawFlop
                }
            };

        /// <summary>
        /// Avisos rapidos: lo que la retroalimentacion marca a mejorar (rojo) o por ajustar (ambar), con la
        /// misma escala y el mismo criterio de dinero ficticio que el resto de APH. Antes tenian sus propios
        /// numeros fijos y podian contradecir a los colores ("VPIP sano" con el color "algo bajo").
        /// </summary>
        private static IEnumerable<string> BuildAlerts(RealTimePlayerReportRow hero, IReadOnlyList<StatFeedback> feedback, DecisionFeedback? decisions)
        {
            if (hero.Hands < StatScale.MinHands)
                return new[] { "Muestra corta: con menos de 30 manos las stats todavía no dicen mucho." };
            var alerts = feedback.Where(item => item.Verdict != StatVerdict.Good)
                .Select(item => $"{item.VerdictLabel}: {item.Sentence}")
                .ToList();
            if (decisions is { Verdict: not StatVerdict.Good })
                alerts.Insert(0, $"{decisions.VerdictLabel}: {decisions.Sentence}");
            return alerts.Count == 0 ? new[] { "Sin alertas: tus stats están bien para esta muestra." } : alerts;
        }

        private static IEnumerable<RivalImpact> BuildRivalImpacts(IReadOnlyList<TableReportModel> tables, string hero)
        {
            var rivals = tables
                .SelectMany(table => table.Players
                    .Where(player => !SamePlayer(player.Name, hero))
                    .Select(player => new { Player = player, Table = table }))
                .GroupBy(item => item.Player.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var hands = group.Sum(item => item.Player.Hands);
                    var weighted = group.Sum(item =>
                    {
                        var tableHands = Math.Max(1, item.Table.Players.Where(player => !SamePlayer(player.Name, hero)).Sum(player => player.Hands));
                        return item.Table.NetAmount * (item.Player.Hands / (double)tableHands);
                    });
                    var bb = group.Sum(item =>
                    {
                        var tableHands = Math.Max(1, item.Table.Players.Where(player => !SamePlayer(player.Name, hero)).Sum(player => player.Hands));
                        var tableWeighted = item.Table.NetAmount * (item.Player.Hands / (double)tableHands);
                        return item.Table.BigBlind > 0 ? tableWeighted / item.Table.BigBlind : 0;
                    });
                    return new RivalImpact(group.Key, hands, weighted, bb);
                })
                .ToList();

            var frequent = rivals.OrderByDescending(rival => rival.Hands).FirstOrDefault();
            if (frequent is not null)
                yield return frequent with { Label = "Rival más frecuente" };

            var mostCostly = rivals.OrderBy(rival => rival.NetAmount).FirstOrDefault();
            if (mostCostly is not null)
                yield return mostCostly with { Label = "Rival que más te costó" };

            var mostProfitable = rivals.OrderByDescending(rival => rival.NetAmount).FirstOrDefault();
            if (mostProfitable is not null)
                yield return mostProfitable with { Label = "Rival más rentable" };
        }

        /// <summary>
        /// Notas de cierre de la sesion (resultado, mesas, mano a revisar). Lo de las stats ya no va
        /// aqui: lo dice la retroalimentacion con semaforo, para no repetirlo ni contradecirlo.
        /// </summary>
        private static IEnumerable<ClosingNote> BuildClosingNotes(
            double netAmount,
            bool isCash,
            TimeSpan duration,
            RealTimePlayerReportRow hero,
            IReadOnlyList<StatFeedback> feedback,
            IReadOnlyList<TableReportModel> tables,
            TableReportModel? bestTable,
            TableReportModel? worstTable,
            HandImpact? worstHand)
        {
            var bb = tables.Sum(table => table.NetBb);
            yield return netAmount >= 0
                ? new ClosingNote($"Sesión ganadora: cerraste con {bb:+0.#;-0.#;0} bb ({FormatMoneyText(netAmount, isCash)}).", NoteTone.Good)
                : new ClosingNote($"Sesión perdedora: cerraste con {bb:+0.#;-0.#;0} bb ({FormatMoneyText(netAmount, isCash)}).", NoteTone.Watch);

            if (hero.Hands < StatScale.MinHands)
                yield return new ClosingNote("Muestra corta: usa este cierre como orientación, no como conclusión.", NoteTone.Info);
            else if (hero.Hands >= 300)
                yield return new ClosingNote($"Muestra sólida: {hero.Hands} manos dan una lectura más confiable de la sesión.", NoteTone.Info);

            if (duration.TotalHours >= 5)
                yield return new ClosingNote($"Sesión larga: {duration.TotalHours:0.#} horas. Revisa si hubo cansancio en la parte final.", NoteTone.Watch);
            else if (duration > TimeSpan.Zero && duration.TotalMinutes < 30)
                yield return new ClosingNote("Sesión corta: buena para revisar manos, pero corta para sacar tendencias.", NoteTone.Info);

            if (tables.Count > 1)
            {
                var winningTables = tables.Count(table => table.NetAmount > 0);
                yield return new ClosingNote($"Lectura por mesas: {winningTables} de {tables.Count} mesas terminaron en positivo.",
                    winningTables * 2 >= tables.Count ? NoteTone.Good : NoteTone.Watch);

                var biggest = tables.OrderByDescending(table => Math.Abs(table.NetAmount)).FirstOrDefault();
                var totalAbs = tables.Sum(table => Math.Abs(table.NetAmount));
                if (biggest is not null && totalAbs > 0 && Math.Abs(biggest.NetAmount) / totalAbs >= 0.55)
                    yield return new ClosingNote(
                        $"Distribución: {biggest.Table.TableName} {(biggest.NetAmount >= 0 ? "cargó gran parte de la ganancia" : "explicó gran parte de la pérdida")}; revisa esa mesa por varianza o manos grandes.",
                        NoteTone.Info);
            }

            if (netAmount < 0 && feedback.Count > 0 && feedback.All(item => item.Verdict == StatVerdict.Good))
                yield return new ClosingNote("Resultado negativo con stats sanas: puede ser varianza; revisa primero las manos grandes.", NoteTone.Info);

            if (worstHand is not null)
                yield return new ClosingNote($"Mano a revisar: la {worstHand.HandNumber} en {worstHand.TableName}, {worstHand.Combo}, {worstHand.NetBb:+0.#;-0.#;0} bb.", NoteTone.Watch);
            else if (worstTable is not null && worstTable.NetAmount < 0)
                yield return new ClosingNote($"Mesa más difícil: {worstTable.Table.TableName}, {worstTable.NetBb:+0.#;-0.#;0} bb.", NoteTone.Watch);

            if (bestTable is not null && bestTable.NetAmount > 0)
                yield return new ClosingNote($"Mesa más rentable: {bestTable.Table.TableName}, {bestTable.NetBb:+0.#;-0.#;0} bb.", NoteTone.Good);
        }

        /// <summary>
        /// Las manos de la mesa dentro de la sesion (de la primera a la ultima registradas). Una mesa
        /// usada en dos sesiones el mismo dia guarda las dos en el mismo archivo: antes se sumaba todo.
        /// </summary>
        private static IEnumerable<IReadOnlyList<string>> SessionHands(RealTimeTableReportRow table)
        {
            if (string.IsNullOrWhiteSpace(table.SourcePath) || !File.Exists(table.SourcePath))
                yield break;
            var from = table.FirstHandTime?.AddSeconds(-1);
            var to = table.LastHandTime?.AddSeconds(1);
            foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadLines(table.SourcePath)))
            {
                var stamp = PokerStarsHandHistory.ExtractTimestamp(hand);
                if (stamp is { } at && (from is { } start && at < start || to is { } end && at > end))
                    continue;
                yield return hand;
            }
        }

        private static (double NetAmount, bool IsCash) EstimateHeroResult(RealTimeTableReportRow table)
        {
            if (string.IsNullOrWhiteSpace(table.HeroName))
                return (0, false);

            var hands = SessionHands(table).ToList();
            var isCash = hands.Any(hand => hand.Any(line => CurrencyAmountRx.IsMatch(line)));
            var net = hands
                .Where(hand => PokerStarsHandHistory.HandHasPlayerActivity(hand, table.HeroName))
                .Sum(hand => PokerStarsHandHistory.EstimateNetForPlayer(hand, table.HeroName));
            return (net, isCash);
        }

        private static bool SamePlayer(string raw, string expected) =>
            string.Equals(
                PokerStarsHandHistory.NormalizeName(raw).Trim().TrimEnd(':'),
                PokerStarsHandHistory.NormalizeName(expected).Trim().TrimEnd(':'),
                StringComparison.OrdinalIgnoreCase);

        private static string FormatMoneyText(double amount, bool isCash)
        {
            var sign = amount >= 0 ? "+" : "-";
            var abs = Math.Abs(amount);
            return isCash
                ? $"{sign}${abs.ToString("#,0.##", CultureInfo.GetCultureInfo("es-MX"))}"
                : $"{sign}{abs.ToString("#,0", CultureInfo.GetCultureInfo("es-MX"))} fichas";
        }

        private sealed record SessionReportModel(
            DateTime GeneratedAt,
            string Hero,
            int RegisteredTables,
            int ActiveTables,
            DateTime? FirstHand,
            DateTime? LastHand,
            int TotalHands,
            double NetAmount,
            double NetBb,
            bool IsCash,
            TimeSpan Duration,
            RealTimePlayerReportRow AverageStats,
            HeroSessionSummary HeroSummary,
            IReadOnlyList<string> Alerts,
            HandImpact? BestHand,
            HandImpact? WorstHand,
            IReadOnlyList<RivalImpact> RivalImpacts,
            IReadOnlyList<ClosingNote> ClosingNotes,
            IReadOnlyList<TableReportModel> Tables,
            IReadOnlyList<RealTimePlayerReportRow> TopPlayers,
            CoachingSummary Coaching,
            IReadOnlyList<StatFeedback> Feedback,
            string Focus);

        private enum NoteTone
        {
            Good,
            Watch,
            Info
        }

        /// <summary>Una nota de cierre con su tono: salio bien, a vigilar o contexto.</summary>
        private sealed record ClosingNote(string Text, NoteTone Tone);

        private sealed record CoachingSummary(
            int Hands,
            double NetBb,
            double LuckBb,
            double Vpip,
            double Pfr,
            IReadOnlyList<AllInLine> AllIns,
            int Decisions,
            int WithAdvice,
            int Followed,
            int Mistakes,
            int ExploitHands,
            double ExploitNetBb,
            IReadOnlyList<MistakeLine> MistakeLines,
            IReadOnlyList<HandTypeRow> HandTypes,
            IReadOnlyList<CurvePoint> Curve,
            DecisionFeedback? DecisionVerdict);

        /// <summary>Un all-in con cartas por salir: con que ibas, contra que, y cuanto te dio o quito la suerte.</summary>
        private sealed record AllInLine(DateTime? At, string Table, string Cards, string Against, string Street, double Equity, double NetBb, double LuckBb);

        /// <summary>Un error marcado por la correccion: que hiciste, que decia APH y por que.</summary>
        private sealed record MistakeLine(DateTime At, string Table, string Cards, string Street, string Chosen, string? Advice, double NetBb, string Why, double LuckBb = 0)
        {
            /// <summary>Lo que perdio la mano sin la suerte de los all-in.</summary>
            public double AdjustedBb => NetBb - LuckBb;
        }

        /// <summary>Una mano del heroe en orden: resultado y suerte del all-in (para la grafica de la portada).</summary>
        private sealed record CurvePoint(DateTime At, double NetBb, double LuckBb);

        private sealed record TableReportModel(
            RealTimeTableReportRow Table,
            double NetAmount,
            bool IsCash,
            double BigBlind,
            double NetBb,
            RealTimePlayerReportRow? HeroStats,
            HandImpact? BestHand,
            HandImpact? WorstHand,
            IReadOnlyList<HandImpact> HandImpacts,
            IReadOnlyList<RealTimePlayerReportRow> Players);

        private sealed record HeroSessionSummary(
            RealTimePlayerReportRow Stats,
            double NetAmount,
            double NetBb,
            string BestTable,
            string WorstTable);

        private sealed record HandImpact(
            string TableName,
            int HandNumber,
            string Combo,
            double NetAmount,
            double NetBb,
            DateTime? Time);

        private sealed record RivalImpact(
            string Name,
            int Hands,
            double NetAmount,
            double NetBb,
            string Label = "");
    }
}
