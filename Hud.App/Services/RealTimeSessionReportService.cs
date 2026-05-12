using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HandReader.Core.Models;

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
        double WWSF);

    public sealed record RealTimeTableReportRow(
        string TableName,
        string? SourcePath,
        string? HeroName,
        bool IsRunning,
        long LinesRead,
        DateTime? FirstHandTime,
        DateTime? LastHandTime,
        IReadOnlyList<RealTimePlayerReportRow> Players);

    public static class RealTimeSessionReportService
    {
        private static readonly Regex BlindsRx =
            new(@"\((?:\$)?(?<small>[\d,.]+)\s*/\s*(?:\$)?(?<big>[\d,.]+)\)", RegexOptions.Compiled);

        private static readonly Regex HeaderBlindsRx =
            new(@"(?:\(|\s)(?:\$)?(?<small>[\d,.]+)\s*/\s*(?:\$)?(?<big>[\d,.]+)(?:\)|\s)", RegexOptions.Compiled);

        private static readonly Regex CurrencyAmountRx =
            new(@"(?:[$€]\s*\d|\d[\d,.]*\s*(?:USD|EUR))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string SavePdf(IReadOnlyList<RealTimeTableReportRow> tables)
        {
            var folder = ReportSessionIndexService.GetReportsFolder();
            Directory.CreateDirectory(folder);

            var fileName = $"APH_RT_Session_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var path = Path.Combine(folder, fileName);

            var model = BuildReport(tables);
            StyledPdfWriter.Write(path, model);
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

        public static RealTimePlayerReportRow FromStats(PlayerStats player) =>
            new(
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
                player.WWSFPct);

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
            var results = active.Select(table => EstimateHeroResult(table.SourcePath, table.HeroName)).ToList();
            var netAmount = results.Sum(result => result.NetAmount);
            var isCash = active.Any(table => table.TableName.Contains('$', StringComparison.Ordinal) || results.Any(result => result.IsCash));
            var tableModels = active.Select(table =>
            {
                var result = EstimateHeroResult(table.SourcePath, table.HeroName);
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
            var alerts = BuildAlerts(heroSummary.Stats).ToList();
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
                ClosingNotes: BuildClosingNotes(netAmount, isCash, duration, heroSummary.Stats, alerts, tableModels, bestTable, worstTable, worstHand).ToList(),
                Tables: tableModels,
                TopPlayers: mergedPlayers.Take(12).ToList());
        }

        private static RealTimePlayerReportRow MergePlayer(IReadOnlyList<RealTimePlayerReportRow> rows)
        {
            if (rows.Count == 0)
                return new RealTimePlayerReportRow("-", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

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
            foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadLines(table.SourcePath)))
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

        private static string FormatCombo(string cards)
        {
            var ranks = Regex.Matches(cards, @"[2-9TJQKA]", RegexOptions.IgnoreCase)
                .Select(match => match.Value.ToUpperInvariant())
                .Take(2)
                .ToList();

            return ranks.Count == 2 ? string.Concat(ranks) : cards;
        }

        private static IEnumerable<string> BuildAlerts(RealTimePlayerReportRow hero)
        {
            var alerts = new List<string>();
            if (hero.Hands < 30)
                alerts.Add("Muestra corta: lee estas stats con cuidado.");
            if (hero.VPIP >= 35)
                alerts.Add("VPIP alto: estas entrando a muchas manos.");
            if (hero.VPIP >= 25 && hero.PFR <= 10)
                alerts.Add("Gap VPIP/PFR grande: muchas manos pasivas preflop.");
            if (hero.ThreeBet >= 10)
                alerts.Add("3Bet alta: revisa que no estes forzando demasiados spots.");
            if (hero.CBet >= 70)
                alerts.Add("CBet muy alta: revisa boards donde conviene frenar.");
            if (hero.FoldVsCBet >= 65)
                alerts.Add("FvCB alto: puedes estar foldeando demasiado ante CBet.");
            if (hero.WTSD >= 30 && hero.WSD < 50)
                alerts.Add("Llegas mucho a showdown sin ganar suficiente.");
            if (hero.WSD >= 55)
                alerts.Add("Showdown fuerte: tus manos al river estan rindiendo.");
            if (hero.WWSF < 40 && hero.Hands >= 30)
                alerts.Add("WWSF bajo: faltan botes ganados antes del showdown.");

            return alerts.Count == 0
                ? new[] { "Sin alertas fuertes: sesion estable para la muestra." }
                : alerts;
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
                yield return frequent with { Label = "Rival mas frecuente" };

            var mostCostly = rivals.OrderBy(rival => rival.NetAmount).FirstOrDefault();
            if (mostCostly is not null)
                yield return mostCostly with { Label = "Rival que mas te costo" };

            var mostProfitable = rivals.OrderByDescending(rival => rival.NetAmount).FirstOrDefault();
            if (mostProfitable is not null)
                yield return mostProfitable with { Label = "Rival mas rentable" };
        }

        private static IEnumerable<string> BuildClosingNotes(
            double netAmount,
            bool isCash,
            TimeSpan duration,
            RealTimePlayerReportRow hero,
            IReadOnlyList<string> alerts,
            IReadOnlyList<TableReportModel> tables,
            TableReportModel? bestTable,
            TableReportModel? worstTable,
            HandImpact? worstHand)
        {
            var notes = new List<string>
            {
                netAmount >= 0
                    ? $"Sesion ganadora: cerraste con {FormatMoneyText(netAmount, isCash)}."
                    : $"Sesion perdedora: cerraste con {FormatMoneyText(netAmount, isCash)}."
            };

            var strengths = BuildStrengthNotes(hero).ToList();
            var warnings = BuildWarningNotes(hero, alerts).ToList();
            var context = BuildContextNotes(hero, duration, tables, netAmount).ToList();

            notes.AddRange(strengths.Take(2));
            notes.AddRange(warnings.Take(2));
            notes.AddRange(context.Take(2));

            if (worstHand is not null)
                notes.Add($"Spot a revisar: mano {worstHand.HandNumber} en {worstHand.TableName}, {worstHand.Combo}, {FormatMoneyText(worstHand.NetAmount, isCash)}.");
            else if (worstTable is not null)
                notes.Add($"Mesa mas dificil: {worstTable.Table.TableName}, {FormatMoneyText(worstTable.NetAmount, worstTable.IsCash)}.");

            if (bestTable is not null)
                notes.Add($"Mesa mas rentable: {bestTable.Table.TableName}, {FormatMoneyText(bestTable.NetAmount, bestTable.IsCash)}.");

            notes.Add($"Siguiente enfoque: {BuildFocusNote(hero, alerts)}");
            return notes.Take(10);
        }

        private static IEnumerable<string> BuildStrengthNotes(RealTimePlayerReportRow hero)
        {
            if (hero.VPIP is >= 18 and <= 32)
                yield return $"Fortaleza preflop: VPIP sano ({hero.VPIP:0.#}%). Estas entrando a una cantidad razonable de manos.";
            if (hero.PFR is >= 8 and <= 22 && hero.VPIP - hero.PFR <= 16)
                yield return $"Fortaleza preflop: PFR estable ({hero.PFR:0.#}%) y buen nivel de iniciativa.";
            if (hero.ThreeBet is >= 4 and <= 9)
                yield return $"Fortaleza preflop: 3Bet sano ({hero.ThreeBet:0.#}%). Hay presion sin volverse excesiva.";
            if (hero.WSD >= 60 && hero.WTSD >= 20)
                yield return $"Fortaleza showdown: sigue llegando al showdown asi; tu W$SD es fuerte ({hero.WSD:0.#}%).";
            else if (hero.WSD >= 55)
                yield return $"Fortaleza showdown: W$SD fuerte ({hero.WSD:0.#}%). Cuando llegas al river, tus manos estan rindiendo.";
            if (hero.WWSF >= 50)
                yield return $"Fortaleza: WWSF sano ({hero.WWSF:0.#}%). Estas ganando botes antes del showdown con buena frecuencia.";
            if (hero.AF is >= 1.5 and <= 3.2 && hero.AFq is >= 45 and <= 68)
                yield return $"Fortaleza: agresion equilibrada. AF {hero.AF:0.#} y AFq {hero.AFq:0.#}% indican presion sin descontrol.";
            if (hero.CBet is >= 45 and <= 65 && hero.FoldVsCBet <= 60)
                yield return $"Fortaleza: CBet y defensa vs CBet se ven estables ({hero.CBet:0.#}% / FvCB {hero.FoldVsCBet:0.#}%).";
        }

        private static IEnumerable<string> BuildWarningNotes(RealTimePlayerReportRow hero, IReadOnlyList<string> alerts)
        {
            if (hero.VPIP < 15 && hero.Hands >= 30)
                yield return $"Atencion preflop: VPIP muy cerrado ({hero.VPIP:0.#}%). Podrias estar dejando pasar spots rentables.";
            if (hero.VPIP >= 35)
                yield return $"Atencion preflop: VPIP alto ({hero.VPIP:0.#}%). Estas entrando a muchas manos.";
            if (hero.VPIP >= 25 && hero.PFR <= 10)
                yield return $"Atencion preflop: gap VPIP/PFR grande. Hay demasiadas manos pasivas preflop.";
            if (hero.PFR < 7 && hero.Hands >= 30)
                yield return $"Atencion preflop: PFR bajo ({hero.PFR:0.#}%). Falta iniciativa en opens/raises.";
            if (hero.ThreeBet < 3 && hero.Hands >= 30)
                yield return $"Atencion preflop: 3Bet bajo ({hero.ThreeBet:0.#}%). Estas dejando pasar spots de resubida.";
            if (hero.ThreeBet >= 10)
                yield return $"Atencion preflop: 3Bet alta ({hero.ThreeBet:0.#}%). Revisa que no estes forzando demasiados spots.";
            if (hero.CBet < 35 && hero.Hands >= 30)
                yield return $"Atencion postflop: CBet baja ({hero.CBet:0.#}%). Puedes estar perdiendo iniciativa en flops favorables.";
            if (hero.CBet >= 70)
                yield return $"Atencion postflop: CBet muy alta ({hero.CBet:0.#}%). Revisa boards donde conviene frenar.";
            if (hero.FoldVsCBet >= 65)
                yield return $"Atencion defensa: FvCB alto ({hero.FoldVsCBet:0.#}%). Puedes estar abandonando demasiados flops.";
            if (hero.WTSD < 18 && hero.Hands >= 30)
                yield return $"Atencion showdown: WTSD bajo ({hero.WTSD:0.#}%). Puede que estes foldeando de mas antes del river.";
            if (hero.WTSD >= 30 && hero.WSD < 50)
                yield return $"Atencion showdown: llegas bastante al river ({hero.WTSD:0.#}%), pero tu W$SD ({hero.WSD:0.#}%) pide revisar calls finales.";
            if (hero.WSD < 45 && hero.Hands >= 30)
                yield return $"Atencion showdown: W$SD bajo ({hero.WSD:0.#}%). Estas llegando al showdown con demasiadas manos debiles.";
            if (hero.WWSF < 40 && hero.Hands >= 30)
                yield return $"Atencion: WWSF bajo ({hero.WWSF:0.#}%). Falta robar o presionar mas botes sin llegar al river.";
            if (hero.AF < 1 && hero.Hands >= 30)
                yield return $"Atencion agresion: AF bajo ({hero.AF:0.#}). Hay demasiada pasividad postflop.";
            if (hero.AF >= 5 || hero.AFq >= 75)
                yield return $"Atencion agresion: AF {hero.AF:0.#} / AFq {hero.AFq:0.#}%. Revisa barrels y raises con poca equity.";
            if (alerts.Count > 0 && !alerts[0].StartsWith("Sin alertas", StringComparison.OrdinalIgnoreCase))
                yield return $"Principal lectura: {alerts[0]}";
        }

        private static IEnumerable<string> BuildContextNotes(
            RealTimePlayerReportRow hero,
            TimeSpan duration,
            IReadOnlyList<TableReportModel> tables,
            double netAmount)
        {
            if (hero.Hands < 30)
                yield return "Muestra corta: usa este cierre como orientacion, no como conclusion fuerte.";
            else if (hero.Hands >= 300)
                yield return $"Muestra solida: {hero.Hands} manos dan una lectura mas confiable de la sesion.";

            if (duration.TotalHours >= 5)
                yield return $"Sesion larga: {duration.TotalHours:0.#} horas. Revisa si hubo fatiga en la parte final.";
            else if (duration > TimeSpan.Zero && duration.TotalMinutes < 30)
                yield return "Sesion corta: buena para revisar spots, pero limitada para tendencias.";

            if (tables.Count > 1)
            {
                var winningTables = tables.Count(table => table.NetAmount > 0);
                yield return $"Lectura por mesas: {winningTables} de {tables.Count} mesas terminaron en positivo.";

                var biggestAbs = tables.OrderByDescending(table => Math.Abs(table.NetAmount)).FirstOrDefault();
                var totalAbs = tables.Sum(table => Math.Abs(table.NetAmount));
                if (biggestAbs is not null && totalAbs > 0 && Math.Abs(biggestAbs.NetAmount) / totalAbs >= 0.55)
                {
                    var label = biggestAbs.NetAmount >= 0 ? "cargo gran parte de la ganancia" : "explico gran parte de la perdida";
                    yield return $"Distribucion: {biggestAbs.Table.TableName} {label}; revisa esa mesa por varianza o spots grandes.";
                }
            }

            if (netAmount >= 0 && tables.Any(table => table.NetAmount < 0))
                yield return "Resultado positivo con fugas: ganaste la sesion, pero hubo mesas negativas que vale la pena revisar.";
            if (netAmount < 0 && hero.VPIP is >= 18 and <= 32 && hero.WSD >= 50)
                yield return "Resultado negativo con stats sanas: puede haber varianza/coolers; prioriza revisar manos grandes.";
        }

        private static string BuildFocusNote(RealTimePlayerReportRow hero, IReadOnlyList<string> alerts)
        {
            if (hero.FoldVsCBet >= 65 || alerts.Any(alert => alert.Contains("FvCB", StringComparison.OrdinalIgnoreCase)))
                return "defender mejor contra CBet y revisar folds automaticos.";
            if (hero.VPIP >= 35 || hero.VPIP >= 25 && hero.PFR <= 10)
                return "cerrar rango preflop y convertir mas manos jugables en raises claros.";
            if (hero.PFR < 7 || hero.ThreeBet < 3)
                return "buscar mas iniciativa preflop: opens claros y 3Bet selectivos.";
            if (hero.CBet < 35)
                return "identificar boards favorables para apostar mas flops con iniciativa.";
            if (hero.CBet >= 70)
                return "separar boards buenos de CBet de boards donde conviene check.";
            if (hero.WTSD >= 30 && hero.WSD < 50 || hero.WSD < 45)
                return "filtrar calls de turn/river y revisar manos que llegaron debiles al showdown.";
            if (hero.WWSF < 40)
                return "presionar mas botes pequenos y buscar steals sin depender del showdown.";
            if (hero.AF < 1)
                return "subir agresion postflop en spots con ventaja de rango o equity.";
            if (hero.AF >= 5 || hero.AFq >= 75)
                return "bajar frecuencia de barrels/raises marginales y priorizar equity real.";

            return "seguir acumulando muestra y comparar contra la proxima sesion.";
        }

        private static (double NetAmount, bool IsCash) EstimateHeroResult(string? sourcePath, string? heroName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(heroName) ||
                !File.Exists(sourcePath))
            {
                return (0, false);
            }

            var lines = File.ReadLines(sourcePath).ToList();
            var isCash = lines.Any(line => CurrencyAmountRx.IsMatch(line));
            var net = PokerStarsHandHistory
                .SplitHands(lines)
                .Where(hand => PokerStarsHandHistory.HandHasPlayerActivity(hand, heroName))
                .Sum(hand => PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName));

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
                ? $"{sign}${abs:0.##}"
                : $"{sign}{abs:0} fichas";
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
            IReadOnlyList<string> ClosingNotes,
            IReadOnlyList<TableReportModel> Tables,
            IReadOnlyList<RealTimePlayerReportRow> TopPlayers);

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

        private static class StyledPdfWriter
        {
            private const double PageW = 842;
            private const double PageH = 595;
            private const double Margin = 34;
            private const double StatH = 22;

            public static void Write(string path, SessionReportModel model)
            {
                var pages = new List<string>();
                var canvas = new PdfCanvas();
                var y = PageH - Margin;
                StartPage(canvas);

                DrawHeader(canvas, model, ref y);
                DrawSummaryCards(canvas, model, ref y);
                DrawStatsBar(canvas, "Stats media de todas las mesas", model.AverageStats, ref y);
                DrawHeroSummary(canvas, model, ref y);
                DrawAlerts(canvas, model.Alerts, ref y);
                DrawHandHighlights(canvas, model, ref y);
                DrawRivalImpacts(canvas, model.RivalImpacts, ref y);
                FinishPage(canvas);
                pages.Add(canvas.Content);

                canvas = new PdfCanvas();
                y = PageH - Margin;
                StartPage(canvas);
                DrawTopPlayersPage(canvas, model, ref y);
                FinishPage(canvas);
                pages.Add(canvas.Content);

                canvas = new PdfCanvas();
                y = PageH - Margin;
                StartPage(canvas);

                foreach (var table in model.Tables)
                {
                    EnsureSpace(pages, ref canvas, ref y, 190);
                    DrawTable(canvas, table, ref y);
                }

                FinishPage(canvas);
                pages.Add(canvas.Content);

                canvas = new PdfCanvas();
                y = PageH - Margin;
                StartPage(canvas);
                DrawSessionEvolutionPage(canvas, model, ref y);
                FinishPage(canvas);
                pages.Add(canvas.Content);

                canvas = new PdfCanvas();
                y = PageH - Margin;
                StartPage(canvas);
                DrawClosingNotes(canvas, model.ClosingNotes, ref y);
                FinishPage(canvas);
                pages.Add(canvas.Content);
                WriteObjects(path, pages);
            }

            private static void DrawHeader(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                canvas.Text("APH Informe de sesion", Margin + 6, y - 8, 28, Rgb(0, 0, 0), bold: true);
                canvas.Text($"Inicio detectado: {Date(model.FirstHand)}", PageW - 320, y - 8, 13, Rgb(0, 0, 0), bold: true);
                canvas.Text($"Generado: {Date(model.GeneratedAt)}", PageW - 320, y - 26, 9, Rgb(80, 86, 96));
                y -= 46;
                canvas.Text("Heroe:", Margin + 6, y, 12, Rgb(0, 0, 0), bold: true);
                canvas.Text(model.Hero, Margin + 58, y, 12, Rgb(0, 0, 0), bold: true);
                y -= 18;
            }

            private static void DrawSummaryCards(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                var cards = new[]
                {
                    ("Mesas registradas", model.RegisteredTables.ToString(CultureInfo.InvariantCulture)),
                    ("Manos totales jugadas", model.TotalHands.ToString(CultureInfo.InvariantCulture)),
                    ("Duracion", FormatDuration(model.Duration)),
                    ("Ganancia/perdida", $"{FormatMoney(model.NetAmount, model.IsCash)} / {FormatBb(model.NetBb)}"),
                    ("Inicio detectado", Date(model.FirstHand)),
                    ("Ultima mano", Date(model.LastHand))
                };

                var w = (PageW - Margin * 2 - 35) / 6;
                for (var i = 0; i < cards.Length; i++)
                {
                    var x = Margin + i * (w + 7);
                    canvas.FillRect(x, y - 48, w, 48, Rgb(255, 255, 255));
                    canvas.StrokeRect(x, y - 48, w, 48, Rgb(210, 216, 224));
                    canvas.Text(cards[i].Item1, x + 8, y - 16, 8, Rgb(70, 78, 90));
                    var valueColor = cards[i].Item1 == "Ganancia/perdida"
                        ? ColorForMoney(cards[i].Item2)
                        : Rgb(0, 0, 0);
                    canvas.Text(cards[i].Item2, x + 8, y - 34, 9, valueColor, bold: true);
                }

                y -= 66;
            }

            private static void DrawStatsBar(PdfCanvas canvas, string title, RealTimePlayerReportRow stats, ref double y)
            {
                DrawSectionTitle(canvas, title, ref y);
                y -= 10;

                var statsData = new (string Label, string Key, string Value, double Raw)[]
                {
                    ("VPIP", "VPIP", Pct(stats.VPIP), stats.VPIP),
                    ("PFR", "PFR", Pct(stats.PFR), stats.PFR),
                    ("3Bet", "THREEBET", Pct(stats.ThreeBet), stats.ThreeBet),
                    ("AF", "AF", stats.AF.ToString("0.#", CultureInfo.InvariantCulture), stats.AF),
                    ("AFq", "AFQ", Pct(stats.AFq), stats.AFq),
                    ("CBet", "CBF", Pct(stats.CBet), stats.CBet),
                    ("FvCB", "FVCBF", Pct(stats.FoldVsCBet), stats.FoldVsCBet),
                    ("WTSD", "WTSD", Pct(stats.WTSD), stats.WTSD),
                    ("W$SD", "WSD", Pct(stats.WSD), stats.WSD),
                    ("WWSF", "WWSF", Pct(stats.WWSF), stats.WWSF)
                };

                var w = (PageW - Margin * 2) / statsData.Length;
                for (var i = 0; i < statsData.Length; i++)
                {
                    var x = Margin + i * w;
                    var bg = ColorForMetric(statsData[i].Key, statsData[i].Raw, stats.Hands);
                    canvas.FillRect(x, y - StatH, w - 2, StatH, bg);
                    canvas.StrokeRect(x, y - StatH, w - 2, StatH, Rgb(30, 36, 44));
                    canvas.Text($"{statsData[i].Label} {statsData[i].Value}", x + 5, y - 14, 8, Rgb(0, 0, 0), bold: true);
                }

                y -= 42;
            }

            private static void DrawHeroSummary(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                EnsureSpaceForBlock(canvas, ref y, 78);
                DrawSectionTitle(canvas, "Resumen del heroe", ref y);

                var stats = model.HeroSummary.Stats;
                var cards = new[]
                {
                    ("Hero", model.Hero),
                    ("Manos", stats.Hands.ToString(CultureInfo.InvariantCulture)),
                    ("VPIP / PFR / 3Bet", $"{Pct(stats.VPIP)} / {Pct(stats.PFR)} / {Pct(stats.ThreeBet)}"),
                    ("Resultado", $"{FormatMoney(model.HeroSummary.NetAmount, model.IsCash)} / {FormatBb(model.HeroSummary.NetBb)}"),
                    ("Mejor mesa", model.HeroSummary.BestTable),
                    ("Peor mesa", model.HeroSummary.WorstTable)
                };

                var w = (PageW - Margin * 2 - 30) / 3;
                for (var i = 0; i < cards.Length; i++)
                {
                    var row = i / 3;
                    var col = i % 3;
                    var x = Margin + col * (w + 15);
                    var top = y - row * 32;
                    canvas.FillRect(x, top - 26, w, 26, Rgb(255, 255, 255));
                    canvas.StrokeRect(x, top - 26, w, 26, Rgb(210, 216, 224));
                    canvas.Text(cards[i].Item1, x + 7, top - 10, 7, Rgb(70, 78, 90));
                    var valueColor = cards[i].Item1 == "Resultado"
                        ? ColorForMoney(cards[i].Item2)
                        : Rgb(0, 0, 0);
                    canvas.Text(TrimTo(cards[i].Item2, 34), x + 7, top - 21, 8, valueColor, bold: true);
                }

                y -= 78;
            }

            private static void DrawAlerts(PdfCanvas canvas, IReadOnlyList<string> alerts, ref double y)
            {
                EnsureSpaceForBlock(canvas, ref y, 56);
                DrawSectionTitle(canvas, "Alertas rapidas", ref y);
                var x = Margin;
                var rowY = y;
                foreach (var alert in alerts.Take(6))
                {
                    var text = TrimTo(alert, 58);
                    var w = Math.Min(250, Math.Max(120, text.Length * 4.5 + 14));
                    if (x + w > PageW - Margin)
                    {
                        x = Margin;
                        rowY -= 24;
                    }

                    var bg = alert.Contains("fuerte", StringComparison.OrdinalIgnoreCase) ||
                             alert.Contains("Showdown fuerte", StringComparison.OrdinalIgnoreCase) ||
                             alert.Contains("estable", StringComparison.OrdinalIgnoreCase)
                        ? Rgb(222, 247, 235)
                        : alert.Contains("alto", StringComparison.OrdinalIgnoreCase) ||
                          alert.Contains("bajo", StringComparison.OrdinalIgnoreCase) ||
                          alert.Contains("perdedora", StringComparison.OrdinalIgnoreCase)
                            ? Rgb(255, 232, 235)
                            : Rgb(255, 246, 214);
                    canvas.FillRect(x, rowY - 17, w, 17, bg);
                    canvas.StrokeRect(x, rowY - 17, w, 17, Rgb(210, 216, 224));
                    canvas.Text(text, x + 7, rowY - 11, 7, Rgb(0, 0, 0));
                    x += w + 6;
                }

                y = rowY - 32;
            }

            private static void DrawHandHighlights(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                EnsureSpaceForBlock(canvas, ref y, 54);
                var w = (PageW - Margin * 2 - 12) / 2;
                DrawHandCard(canvas, "Mejor mano", model.BestHand, Margin, y, w, model.IsCash);
                DrawHandCard(canvas, "Peor mano", model.WorstHand, Margin + w + 12, y, w, model.IsCash);
                y -= 64;
            }

            private static void DrawHandCard(PdfCanvas canvas, string title, HandImpact? hand, double x, double y, double w, bool isCash)
            {
                canvas.FillRect(x, y - 48, w, 48, Rgb(255, 255, 255));
                canvas.StrokeRect(x, y - 48, w, 48, Rgb(210, 216, 224));
                canvas.Text(title, x + 8, y - 14, 9, Rgb(0, 0, 0), bold: true);
                if (hand is null)
                {
                    canvas.Text("Sin datos suficientes", x + 8, y - 32, 8, Rgb(80, 86, 96));
                    return;
                }

                canvas.Text($"Mano {hand.HandNumber} | {hand.Combo} | {TrimTo(hand.TableName, 34)}", x + 8, y - 29, 8, Rgb(70, 78, 90));
                canvas.Text($"{FormatMoney(hand.NetAmount, isCash)} / {FormatBb(hand.NetBb)}", x + 8, y - 42, 8, hand.NetAmount >= 0 ? Rgb(33, 192, 122) : Rgb(226, 78, 91), bold: true);
            }

            private static void DrawTopPlayers(PdfCanvas canvas, IReadOnlyList<RealTimePlayerReportRow> players, ref double y)
            {
                DrawSectionTitle(canvas, "Top jugadores por manos en la sesion", ref y);
                DrawPlayerHeader(canvas, y);
                y -= 16;

                foreach (var player in players.Take(10))
                {
                    DrawPlayerRow(canvas, player, y);
                    y -= 15;
                }

                y -= 14;
            }

            private static void DrawTopPlayersPage(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                canvas.Text("Top jugadores por manos en la sesion", Margin + 6, y - 8, 24, Rgb(0, 0, 0), bold: true);
                canvas.Text($"Heroe: {model.Hero}    Mesas: {model.RegisteredTables}    Manos: {model.TotalHands}", Margin + 8, y - 30, 10, Rgb(70, 78, 90));
                canvas.Text($"Resultado: {FormatMoney(model.NetAmount, model.IsCash)} / {FormatBb(model.NetBb)}", PageW - 260, y - 30, 10, model.NetAmount >= 0 ? Rgb(33, 192, 122) : Rgb(226, 78, 91), bold: true);
                y -= 58;

                canvas.FillRect(Margin, y - 36, PageW - Margin * 2, 36, Rgb(248, 250, 252));
                canvas.StrokeRect(Margin, y - 36, PageW - Margin * 2, 36, Rgb(210, 216, 224), 1.1);
                canvas.Text("Lectura rapida", Margin + 10, y - 13, 11, Rgb(0, 0, 0), bold: true);
                canvas.Text(TrimTo(string.Join("  |  ", model.Alerts.Take(3)), 135), Margin + 10, y - 28, 8, Rgb(70, 78, 90));
                y -= 54;

                DrawPlayerHeader(canvas, y);
                y -= 16;
                foreach (var player in model.TopPlayers.Take(28))
                {
                    DrawPlayerRow(canvas, player, y);
                    y -= 15;
                    if (y < Margin + 24)
                        break;
                }
            }

            private static void DrawRivalImpacts(PdfCanvas canvas, IReadOnlyList<RivalImpact> rivals, ref double y)
            {
                if (rivals.Count == 0)
                    return;

                DrawSectionTitle(canvas, "Rivales clave", ref y);
                var w = (PageW - Margin * 2 - 24) / 3;
                for (var i = 0; i < Math.Min(3, rivals.Count); i++)
                {
                    var rival = rivals[i];
                    var x = Margin + i * (w + 12);
                    canvas.FillRect(x, y - 44, w, 44, Rgb(255, 255, 255));
                    canvas.StrokeRect(x, y - 44, w, 44, Rgb(210, 216, 224));
                    canvas.Text(rival.Label, x + 8, y - 12, 8, Rgb(70, 78, 90));
                    canvas.Text(TrimTo(rival.Name, 24), x + 8, y - 25, 10, Rgb(0, 0, 0), bold: true);
                    canvas.Text($"{rival.Hands} manos | impacto aprox. {FormatMoney(rival.NetAmount, false)} / {FormatBb(rival.NetBb)}", x + 8, y - 38, 7, rival.NetAmount >= 0 ? Rgb(33, 192, 122) : Rgb(226, 78, 91));
                }

                y -= 62;
            }

            private static void DrawTable(PdfCanvas canvas, TableReportModel table, ref double y)
            {
                var start = y;
                canvas.FillRect(Margin, y - 48, PageW - Margin * 2, 48, Rgb(239, 246, 252));
                canvas.FillRect(Margin, y - 48, 6, 48, table.NetAmount >= 0 ? Rgb(33, 192, 122) : Rgb(226, 78, 91));
                canvas.StrokeRect(Margin, y - 48, PageW - Margin * 2, 48, Rgb(120, 139, 160), 1.4);
                canvas.Text($"Mesa: {table.Table.TableName}", Margin + 14, y - 17, 14, Rgb(0, 0, 0), bold: true);
                var hero = table.HeroStats;
                var heroLine = hero is null
                    ? "Hero: sin stats"
                    : $"Hero VPIP/PFR/3Bet: {Pct(hero.VPIP)} / {Pct(hero.PFR)} / {Pct(hero.ThreeBet)}";
                canvas.Text($"Inicio: {Date(table.Table.FirstHandTime)}    Ultima: {Date(table.Table.LastHandTime)}    Manos: {hero?.Hands ?? 0}    Jugadores: {table.Players.Count}", Margin + 14, y - 34, 8, Rgb(70, 78, 90));
                canvas.Text(heroLine, Margin + 14, y - 45, 8, Rgb(70, 78, 90));
                canvas.Text($"Resultado: {FormatMoney(table.NetAmount, table.IsCash)} / {FormatBb(table.NetBb)}", PageW - 218, y - 17, 10, table.NetAmount >= 0 ? Rgb(33, 192, 122) : Rgb(226, 78, 91), bold: true);
                y -= 64;

                if (hero is not null)
                    DrawStatsBar(canvas, "Stats de hero en esta mesa", hero, ref y);

                DrawPlayerHeader(canvas, y);
                y -= 16;
                foreach (var player in table.Players.Take(8))
                {
                    DrawPlayerRow(canvas, player, y);
                    y -= 15;
                }

                y -= 14;
                if (y > start)
                    y = start - 184;
            }

            private static void DrawSessionEvolutionPage(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                canvas.Text("Evolucion de bb por mesa", Margin + 6, y - 8, 24, Rgb(0, 0, 0), bold: true);
                canvas.Text($"Heroe: {model.Hero}    Resultado total: {FormatMoney(model.NetAmount, model.IsCash)} / {FormatBb(model.NetBb)}", Margin + 8, y - 30, 10, Rgb(70, 78, 90));
                y -= 58;

                DrawBbEvolutionChart(canvas, model, ref y);
                DrawTableSummary(canvas, model, ref y);
            }

            private static void DrawBbEvolutionChart(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                DrawSectionTitle(canvas, "Grafica acumulada en bb", ref y);
                var chartX = Margin + 42;
                var chartY = y - 210;
                var chartW = PageW - Margin * 2 - 72;
                var chartH = 190;
                var series = model.Tables
                    .Select((table, index) => new
                    {
                        Table = table,
                        Color = ChartColor(index),
                        Points = BuildCumulativeBb(table).ToList()
                    })
                    .Where(item => item.Points.Count > 1)
                    .ToList();

                var values = series.SelectMany(item => item.Points.Select(point => point.Bb)).ToList();
                values.Add(0);
                values.Add(model.Tables.Count > 0 ? model.Tables.Average(table => table.NetBb) : 0);
                var min = Math.Min(0, values.Min());
                var max = Math.Max(0, values.Max());
                if (Math.Abs(max - min) < 1)
                {
                    max += 1;
                    min -= 1;
                }

                canvas.FillRect(chartX, chartY, chartW, chartH, Rgb(255, 255, 255));
                canvas.StrokeRect(chartX, chartY, chartW, chartH, Rgb(190, 199, 210), 1.2);
                for (var i = 0; i <= 4; i++)
                {
                    var gy = chartY + (chartH / 4 * i);
                    canvas.StrokeLine(chartX, gy, chartX + chartW, gy, Rgb(232, 236, 241), 0.5);
                    var label = min + ((max - min) / 4 * i);
                    canvas.Text($"{label:0.#} bb", Margin + 6, gy - 3, 7, Rgb(70, 78, 90));
                }

                var zeroY = MapY(0, min, max, chartY, chartH);
                canvas.StrokeLine(chartX, zeroY, chartX + chartW, zeroY, Rgb(105, 114, 128), 0.9);

                var average = model.Tables.Count > 0 ? model.Tables.Average(table => table.NetBb) : 0;
                var averageY = MapY(average, min, max, chartY, chartH);
                canvas.StrokeLine(chartX, averageY, chartX + chartW, averageY, Rgb(20, 130, 180), 1.4);
                canvas.Text($"Media {FormatBb(average)}", chartX + chartW - 78, averageY + 5, 8, Rgb(20, 130, 180), bold: true);

                foreach (var item in series)
                {
                    for (var i = 1; i < item.Points.Count; i++)
                    {
                        var p1 = item.Points[i - 1];
                        var p2 = item.Points[i];
                        var x1 = chartX + (p1.Index / (double)Math.Max(1, item.Points.Count - 1)) * chartW;
                        var x2 = chartX + (p2.Index / (double)Math.Max(1, item.Points.Count - 1)) * chartW;
                        canvas.StrokeLine(x1, MapY(p1.Bb, min, max, chartY, chartH), x2, MapY(p2.Bb, min, max, chartY, chartH), item.Color, 1.8);
                    }
                }

                var legendY = chartY - 18;
                var legendX = chartX;
                foreach (var item in series.Take(6))
                {
                    var label = $"{TrimTo(item.Table.Table.TableName, 22)} {FormatBb(item.Table.NetBb)}";
                    var w = Math.Min(160, Math.Max(72, label.Length * 4.3 + 18));
                    if (legendX + w > chartX + chartW)
                    {
                        legendX = chartX;
                        legendY -= 14;
                    }

                    canvas.FillRect(legendX, legendY - 8, 8, 8, item.Color);
                    canvas.Text(label, legendX + 12, legendY - 7, 7, Rgb(0, 0, 0));
                    legendX += w;
                }

                y = legendY - 28;
            }

            private static void DrawTableSummary(PdfCanvas canvas, SessionReportModel model, ref double y)
            {
                DrawSectionTitle(canvas, "Resumen de mesas y ganancias", ref y);
                var widths = new[] { 176d, 44, 58, 58, 86, 66, 56, 56, 56 };
                var headers = new[] { "Mesa", "Manos", "Inicio", "Ultima", "Resultado", "bb", "VPIP", "PFR", "3Bet" };
                var x = Margin;
                canvas.FillRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(238, 242, 246));
                canvas.StrokeRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(210, 216, 224));
                for (var i = 0; i < headers.Length; i++)
                {
                    canvas.Text(headers[i], x + 4, y - 9, 7, Rgb(0, 0, 0), bold: true);
                    x += widths[i];
                }

                y -= 16;
                foreach (var table in model.Tables.Take(18))
                {
                    var hero = table.HeroStats;
                    x = Margin;
                    var rowValues = new[]
                    {
                        TrimTo(table.Table.TableName, 30),
                        (hero?.Hands ?? 0).ToString(CultureInfo.InvariantCulture),
                        ShortDate(table.Table.FirstHandTime),
                        ShortDate(table.Table.LastHandTime),
                        FormatMoney(table.NetAmount, table.IsCash),
                        FormatBb(table.NetBb),
                        hero is null ? "-" : Pct(hero.VPIP),
                        hero is null ? "-" : Pct(hero.PFR),
                        hero is null ? "-" : Pct(hero.ThreeBet)
                    };

                    canvas.FillRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(255, 255, 255));
                    canvas.StrokeRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(224, 229, 235));
                    for (var i = 0; i < rowValues.Length; i++)
                    {
                        var bg = i switch
                        {
                            4 or 5 => table.NetAmount >= 0 ? Rgb(222, 247, 235) : Rgb(255, 232, 235),
                            6 when hero is not null => ColorForMetric("VPIP", hero.VPIP, hero.Hands),
                            7 when hero is not null => ColorForMetric("PFR", hero.PFR, hero.Hands),
                            8 when hero is not null => ColorForMetric("THREEBET", hero.ThreeBet, hero.Hands),
                            _ => Rgb(255, 255, 255)
                        };
                        if (i >= 4)
                        {
                            canvas.FillRect(x, y - 15, widths[i] - 1, 15, bg);
                            canvas.Text(rowValues[i], x + 4, y - 9, 7, i is 4 or 5 ? ColorForMoney(rowValues[i]) : Rgb(0, 0, 0), i is 4 or 5);
                        }
                        else
                        {
                            canvas.Text(rowValues[i], x + 4, y - 9, 7, Rgb(0, 0, 0), i == 0);
                        }

                        x += widths[i];
                    }

                    y -= 15;
                }

                y -= 16;
            }

            private static void DrawClosingNotes(PdfCanvas canvas, IReadOnlyList<string> notes, ref double y)
            {
                if (notes.Count == 0)
                    return;

                DrawSectionTitle(canvas, "Notas de cierre", ref y);
                foreach (var note in notes.Take(10))
                {
                    var bg = ColorForClosingNote(note);
                    canvas.FillRect(Margin, y - 18, PageW - Margin * 2, 18, bg);
                    canvas.FillRect(Margin, y - 18, 5, 18, AccentForClosingNote(note));
                    canvas.StrokeRect(Margin, y - 18, PageW - Margin * 2, 18, Rgb(210, 216, 224));
                    canvas.Text(TrimTo(note, 128), Margin + 11, y - 12, 8, Rgb(0, 0, 0));
                    y -= 22;
                }

                y -= 10;
            }

            private static (double R, double G, double B) ColorForClosingNote(string note)
            {
                if (note.Contains("ganadora", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("rentable", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Fortaleza", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("fuerte", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("sano", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("estable", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("equilibrada", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("positivo", StringComparison.OrdinalIgnoreCase))
                    return Rgb(222, 247, 235);

                if (note.Contains("perdedora", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Principal lectura", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Spot a revisar", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Atencion", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("bajo", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("alto", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("dificil", StringComparison.OrdinalIgnoreCase))
                    return Rgb(255, 232, 235);

                if (note.Contains("Siguiente enfoque", StringComparison.OrdinalIgnoreCase))
                    return Rgb(255, 246, 214);

                return Rgb(248, 250, 252);
            }

            private static (double R, double G, double B) AccentForClosingNote(string note)
            {
                if (note.Contains("ganadora", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("rentable", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Fortaleza", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("fuerte", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("sano", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("estable", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("equilibrada", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("positivo", StringComparison.OrdinalIgnoreCase))
                    return Rgb(33, 192, 122);

                if (note.Contains("perdedora", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Principal lectura", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Spot a revisar", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("Atencion", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("bajo", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("alto", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("dificil", StringComparison.OrdinalIgnoreCase))
                    return Rgb(226, 78, 91);

                if (note.Contains("Siguiente enfoque", StringComparison.OrdinalIgnoreCase))
                    return Rgb(194, 107, 0);

                return Rgb(120, 139, 160);
            }

            private static void DrawSectionTitle(PdfCanvas canvas, string title, ref double y)
            {
                canvas.StrokeLine(Margin, y + 8, PageW - Margin, y + 8, Rgb(210, 216, 224), 0.8);
                canvas.FillRect(Margin, y - 18, PageW - Margin * 2, 19, Rgb(248, 250, 252));
                canvas.StrokeRect(Margin, y - 18, PageW - Margin * 2, 19, Rgb(225, 230, 236));
                canvas.Text(title, Margin + 8, y - 12, 12, Rgb(0, 0, 0), bold: true);
                y -= 30;
            }

            private static void DrawPlayerHeader(PdfCanvas canvas, double y)
            {
                canvas.FillRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(238, 242, 246));
                canvas.StrokeRect(Margin, y - 15, PageW - Margin * 2, 15, Rgb(210, 216, 224));
                var headers = new[] { "Jugador", "Manos", "VPIP", "PFR", "3Bet", "AF", "AFq", "CBet", "FvCB", "WTSD", "W$SD", "WWSF" };
                var widths = new[] { 142d, 42, 50, 50, 50, 40, 50, 50, 50, 50, 50, 50 };
                var x = Margin + 4;
                for (var i = 0; i < headers.Length; i++)
                {
                    canvas.Text(headers[i], x, y - 9, 7, Rgb(0, 0, 0), bold: true);
                    x += widths[i];
                }
            }

            private static void DrawPlayerRow(PdfCanvas canvas, RealTimePlayerReportRow player, double y)
            {
                canvas.FillRect(Margin, y - 14, PageW - Margin * 2, 14, Rgb(255, 255, 255));
                canvas.StrokeRect(Margin, y - 14, PageW - Margin * 2, 14, Rgb(224, 229, 235));
                var values = new[]
                {
                    Clean(player.Name),
                    player.Hands.ToString(CultureInfo.InvariantCulture),
                    Pct(player.VPIP),
                    Pct(player.PFR),
                    Pct(player.ThreeBet),
                    player.AF.ToString("0.#", CultureInfo.InvariantCulture),
                    Pct(player.AFq),
                    Pct(player.CBet),
                    Pct(player.FoldVsCBet),
                    Pct(player.WTSD),
                    Pct(player.WSD),
                    Pct(player.WWSF)
                };
                var widths = new[] { 142d, 42, 50, 50, 50, 40, 50, 50, 50, 50, 50, 50 };
                var x = Margin + 4;
                for (var i = 0; i < values.Length; i++)
                {
                    if (i >= 2)
                    {
                        var metric = i switch
                        {
                            2 => ("VPIP", player.VPIP),
                            3 => ("PFR", player.PFR),
                            4 => ("THREEBET", player.ThreeBet),
                            5 => ("AF", player.AF),
                            6 => ("AFQ", player.AFq),
                            7 => ("CBF", player.CBet),
                            8 => ("FVCBF", player.FoldVsCBet),
                            9 => ("WTSD", player.WTSD),
                            10 => ("WSD", player.WSD),
                            _ => ("WWSF", player.WWSF)
                        };
                        var bg = ColorForMetric(metric.Item1, metric.Item2, player.Hands);
                        canvas.FillRect(x - 3, y - 14, widths[i] - 2, 14, bg);
                        canvas.Text(values[i], x, y - 9, 7, Rgb(0, 0, 0), false);
                        x += widths[i];
                        continue;
                    }

                    canvas.Text(values[i], x, y - 9, 7, Rgb(0, 0, 0), i <= 1);
                    x += widths[i];
                }
            }

            private static void EnsureSpace(List<string> pages, ref PdfCanvas canvas, ref double y, double required)
            {
                if (y - required > Margin)
                    return;

                FinishPage(canvas);
                pages.Add(canvas.Content);
                canvas = new PdfCanvas();
                StartPage(canvas);
                y = PageH - Margin;
            }

            private static void EnsureSpaceForBlock(PdfCanvas canvas, ref double y, double required)
            {
                if (y - required > Margin)
                    return;

                y = Margin + required;
            }

            private static string Pct(double value) => $"{value:0.#}%";

            private static string FormatBb(double value) => $"{(value >= 0 ? "+" : "-")}{Math.Abs(value):0.#} bb";

            private static string FormatDuration(TimeSpan duration)
            {
                if (duration <= TimeSpan.Zero)
                    return "-";

                return duration.TotalHours >= 1
                    ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
                    : $"{duration.Minutes}m";
            }

            private static string TrimTo(string value, int max)
            {
                var clean = Clean(value);
                return clean.Length <= max ? clean : clean[..Math.Max(0, max - 3)] + "...";
            }

            private static string Date(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            private static string Date(DateTime? value) => value.HasValue ? Date(value.Value) : "-";

            private static string ShortDate(DateTime? value) => value.HasValue ? value.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "-";

            private static string FormatMoney(double amount, bool isCash)
            {
                var sign = amount >= 0 ? "+" : "-";
                var abs = Math.Abs(amount);
                return isCash
                    ? $"{sign}${abs:0.##}"
                    : $"{sign}{abs:0} fichas";
            }

            private static (double R, double G, double B) ColorForMoney(string value) =>
                value.StartsWith("-", StringComparison.Ordinal) ? Rgb(226, 78, 91) : Rgb(33, 192, 122);

            private static IEnumerable<(int Index, double Bb)> BuildCumulativeBb(TableReportModel table)
            {
                yield return (0, 0);
                var cumulative = 0.0;
                var index = 1;
                foreach (var hand in table.HandImpacts.OrderBy(hand => hand.HandNumber))
                {
                    cumulative += hand.NetBb;
                    yield return (index, cumulative);
                    index++;
                }

                if (index == 1)
                    yield return (1, table.NetBb);
            }

            private static double MapY(double value, double min, double max, double y, double h)
            {
                var ratio = (value - min) / (max - min);
                return y + ratio * h;
            }

            private static (double R, double G, double B) ChartColor(int index) =>
                (index % 8) switch
                {
                    0 => Rgb(0, 120, 212),
                    1 => Rgb(33, 192, 122),
                    2 => Rgb(226, 78, 91),
                    3 => Rgb(194, 107, 0),
                    4 => Rgb(111, 66, 193),
                    5 => Rgb(4, 184, 175),
                    6 => Rgb(156, 0, 0),
                    _ => Rgb(76, 184, 4)
                };

            private static void StartPage(PdfCanvas canvas)
            {
                canvas.FillRect(0, 0, PageW, PageH, Rgb(255, 255, 255));
            }

            private static void FinishPage(PdfCanvas canvas)
            {
                canvas.WatermarkText("APH", 235, 210, 180, Rgb(80, 96, 116), 0.13, bold: true);
                canvas.WatermarkText("Analyzer Poker Hands", 292, 190, 18, Rgb(80, 96, 116), 0.16, bold: true);
            }

            private static (double R, double G, double B) ColorForMetric(string key, double value, int hands)
            {
                if (hands < 30)
                    return Rgb(18, 24, 32);

                var thresholds = GetThresholds(key);
                var band = 0;
                foreach (var threshold in thresholds)
                {
                    if (value >= threshold)
                        band++;
                }

                if (key is "FVCBF" or "WSD")
                    band = 7 - band;

                return band switch
                {
                    0 => Rgb(2, 87, 166),
                    1 => Rgb(4, 184, 175),
                    2 => Rgb(76, 184, 4),
                    3 => Rgb(184, 181, 4),
                    4 => Rgb(194, 107, 0),
                    5 => Rgb(255, 115, 115),
                    _ => Rgb(156, 0, 0)
                };
            }

            private static (double R, double G, double B) TextForBackground((double R, double G, double B) bg)
            {
                var luminance = (0.299 * bg.R) + (0.587 * bg.G) + (0.114 * bg.B);
                return luminance < 0.35 ? Rgb(255, 255, 255) : Rgb(0, 0, 0);
            }

            private static double[] GetThresholds(string key) =>
                key switch
                {
                    "VPIP" => new double[] { 10, 15, 22, 28, 35, 45, 55 },
                    "PFR" => new double[] { 6, 9, 13, 17, 22, 28, 35 },
                    "THREEBET" => new double[] { 2, 4, 6, 8, 10, 13, 16 },
                    "AF" => new double[] { 0.7, 1.0, 1.5, 2.2, 3.0, 4.0, 6.0 },
                    "AFQ" => new double[] { 20, 30, 40, 50, 60, 70, 80 },
                    "CBF" => new double[] { 30, 40, 50, 60, 70, 75, 80 },
                    "FVCBF" => new double[] { 25, 35, 45, 55, 65, 75, 85 },
                    "WTSD" => new double[] { 18, 22, 25, 28, 32, 36, 40 },
                    "WSD" => new double[] { 40, 45, 50, 55, 60, 65, 70 },
                    "WWSF" => new double[] { 35, 40, 45, 50, 54, 58, 62 },
                    _ => Array.Empty<double>()
                };

            private static (double R, double G, double B) Rgb(double r, double g, double b) => (r / 255, g / 255, b / 255);

            private static void WriteObjects(string path, IReadOnlyList<string> pageContents)
            {
                var objects = new List<string>
                {
                    "<< /Type /Catalog /Pages 2 0 R >>",
                    "__PAGES__",
                    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
                    "<< /Type /ExtGState /ca 0.14 /CA 0.14 >>"
                };

                var pageIds = new List<int>();
                foreach (var content in pageContents)
                {
                    var pageId = objects.Count + 1;
                    var contentId = objects.Count + 2;
                    pageIds.Add(pageId);
                    objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW.ToString(CultureInfo.InvariantCulture)} {PageH.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> /ExtGState << /WM 5 0 R >> >> /Contents {contentId} 0 R >>");
                    objects.Add(BuildStream(content));
                }

                objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>";

                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, Encoding.ASCII);
                writer.WriteLine("%PDF-1.4");
                writer.WriteLine("%APH");
                writer.Flush();

                var offsets = new List<long> { 0 };
                for (var i = 0; i < objects.Count; i++)
                {
                    offsets.Add(stream.Position);
                    writer.WriteLine($"{i + 1} 0 obj");
                    writer.WriteLine(objects[i]);
                    writer.WriteLine("endobj");
                    writer.Flush();
                }

                var xref = stream.Position;
                writer.WriteLine("xref");
                writer.WriteLine($"0 {objects.Count + 1}");
                writer.WriteLine("0000000000 65535 f ");
                foreach (var offset in offsets.Skip(1))
                    writer.WriteLine($"{offset:0000000000} 00000 n ");
                writer.WriteLine("trailer");
                writer.WriteLine($"<< /Size {objects.Count + 1} /Root 1 0 R >>");
                writer.WriteLine("startxref");
                writer.WriteLine(xref.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("%%EOF");
            }

            private static string BuildStream(string content) =>
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";
        }

        private sealed class PdfCanvas
        {
            private readonly StringBuilder _builder = new();

            public string Content => _builder.ToString();

            public void FillRect(double x, double y, double w, double h, (double R, double G, double B) color)
            {
                _builder.AppendLine($"{Num(color.R)} {Num(color.G)} {Num(color.B)} rg");
                _builder.AppendLine($"{Num(x)} {Num(y)} {Num(w)} {Num(h)} re f");
            }

            public void StrokeRect(double x, double y, double w, double h, (double R, double G, double B) color, double width = 1)
            {
                _builder.AppendLine($"{Num(color.R)} {Num(color.G)} {Num(color.B)} RG");
                _builder.AppendLine($"{Num(width)} w");
                _builder.AppendLine($"{Num(x)} {Num(y)} {Num(w)} {Num(h)} re S");
                _builder.AppendLine("1 w");
            }

            public void StrokeLine(double x1, double y1, double x2, double y2, (double R, double G, double B) color, double width = 1)
            {
                _builder.AppendLine($"{Num(color.R)} {Num(color.G)} {Num(color.B)} RG");
                _builder.AppendLine($"{Num(width)} w");
                _builder.AppendLine($"{Num(x1)} {Num(y1)} m {Num(x2)} {Num(y2)} l S");
                _builder.AppendLine("1 w");
            }

            public void Text(string text, double x, double y, double size, (double R, double G, double B) color, bool bold = false)
            {
                _builder.AppendLine("BT");
                _builder.AppendLine($"{Num(color.R)} {Num(color.G)} {Num(color.B)} rg");
                _builder.AppendLine($"/{(bold ? "F2" : "F1")} {Num(size)} Tf");
                _builder.AppendLine($"{Num(x)} {Num(y)} Td");
                _builder.AppendLine($"({Escape(Clean(text))}) Tj");
                _builder.AppendLine("ET");
            }

            public void WatermarkText(string text, double x, double y, double size, (double R, double G, double B) color, double opacity, bool bold = false)
            {
                _builder.AppendLine("q");
                _builder.AppendLine("/WM gs");
                _builder.AppendLine("BT");
                _builder.AppendLine($"{Num(color.R)} {Num(color.G)} {Num(color.B)} rg");
                _builder.AppendLine($"/{(bold ? "F2" : "F1")} {Num(size)} Tf");
                _builder.AppendLine($"{Num(x)} {Num(y)} Td");
                _builder.AppendLine($"({Escape(Clean(text))}) Tj");
                _builder.AppendLine("ET");
                _builder.AppendLine("Q");
            }

            private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

            private static string Escape(string value) =>
                value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("(", "\\(", StringComparison.Ordinal)
                    .Replace(")", "\\)", StringComparison.Ordinal);
        }

        private static string Clean(string value)
        {
            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(value.Length);
            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;

                builder.Append(c switch
                {
                    'ñ' => 'n',
                    'Ñ' => 'N',
                    '\u25B2' => '+',
                    '\u25BC' => '-',
                    _ when c < 32 || c > 126 => ' ',
                    _ => c
                });
            }

            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }
    }
}
