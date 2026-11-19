using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;

namespace Hud.App
{
    public partial class MainWindow
    {
        private static readonly Regex BlindsRx =
            new(@"\((?<sb>" + PokerAmountParser.BlindAmountPattern + @")\s*/\s*(?<bb>" + PokerAmountParser.BlindAmountPattern + @")(?:\s+[A-Z]{3})?\)",
                RegexOptions.Compiled);
        private static readonly Regex TableRx =
            new(@"(?:Table\s+'(?<table>[^']+)'|Mesa\s+(?<table>.+?)\s+\d+-max)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HeaderGameRx =
            new(@"(?:PokerStars(?:\s+[\w'-]+)*?\s+(?:Hand|Game)\s+#\d+|Mano #\d+ de PokerStars):\s+(?<game>.+?)\s+\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MaxTableRx =
            new(@"(?<max>\d+)-max", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeatRx =
            new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:\u00BA|\u00B0|o|ro|&ordm;))?)\s+(?<seat>\d+):", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HandStartRx =
            new(HandReader.Core.Parsing.PokerStarsParser.HandStartPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
        // Compartido con el resto del parseo: acepta horas de uno o dos digitos.
        // Con \d{2} fijo, "9:05:00" no casaba y la mesa acababa fechada en 0001-01-01.
        private static readonly Regex HeaderTimestampRx =
            PokerStarsHandHistory.HeaderTimestampRx;

        private static DashboardResult AnalyzeFolder(string folder, string pokerRoom)
        {
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            files.AddRange(AphBackupDatabaseService.MaterializeMissingBackups(pokerRoom));
            files = files
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var agg = new StatsAggregator();
            var parser = new PokerStarsParser(agg);
            var stakeVotes = new Dictionary<StakeProfile, int>();
            var tables = new List<TableSessionStats>();
            var dealtHandsByHero = new Dictionary<string, int>(StringComparer.Ordinal);
            var filesWithoutHero = new List<string>();

            // Una unica lectura de disco por archivo: antes cada .txt se leia cuatro veces
            // (stake, stats globales, stats por mesa y respaldo).
            foreach (var file in files)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"no pudo leer {file}: {ex.Message}");
                    continue;
                }

                VoteStake(stakeVotes, DetectStakeFromLines(lines));
                parser.FeedLines(lines, () => { });

                // Cada archivo tiene su propio heroe ("Dealt to"): la carpeta de PokerStars guarda
                // una subcarpeta por cuenta y, con un unico heroe global, se perdian las mesas de
                // las demas cuentas y sus respaldos quedaban con el heroe equivocado.
                var (fileHero, dealtHands) = DetectHeroFromLines(lines);
                if (fileHero is null)
                {
                    // Sin "Dealt to" su heroe depende del resto de archivos: se procesa al final.
                    filesWithoutHero.Add(file);
                    continue;
                }

                dealtHandsByHero.TryGetValue(fileHero, out var heroHands);
                dealtHandsByHero[fileHero] = heroHands + dealtHands;

                var table = AnalyzeTableLines(file, lines, fileHero, pokerRoom);
                AphBackupDatabaseService.BackupHandHistoryFile(file, lines, pokerRoom, fileHero, table);
                if (table is not null)
                    tables.Add(table);
            }

            var stake = stakeVotes.Count == 0
                ? StakeProfile.Low
                : stakeVotes.OrderByDescending(kv => kv.Value).First().Key;

            var players = agg.Players.Values
                .OrderByDescending(p => p.HandsReceived)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // El heroe del panel es la cuenta con mas manos repartidas; sin ningun "Dealt to" se
            // recupera la heuristica antigua (jugador con mas manos).
            var heroName = dealtHandsByHero.Count == 0
                ? null
                : dealtHandsByHero
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;

            var hero = heroName is not null && agg.Players.TryGetValue(heroName, out var heroStats)
                ? heroStats
                : players.FirstOrDefault();

            // Solo se releen los archivos sin "Dealt to" y cada uno se respalda una unica vez:
            // con el heroe resuelto arriba, o sin heroe ni mesa si no hay ninguno.
            var fallbackHero = heroName ?? hero?.Name;
            foreach (var file in filesWithoutHero)
            {
                try
                {
                    var lines = File.ReadAllLines(file);
                    var table = fallbackHero is null ? null : AnalyzeTableLines(file, lines, fallbackHero, pokerRoom);
                    AphBackupDatabaseService.BackupHandHistoryFile(file, lines, pokerRoom, fallbackHero, table);
                    if (table is not null)
                        tables.Add(table);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"segunda pasada fallo en {file}: {ex.Message}");
                }
            }

            tables = tables
                .OrderByDescending(table => table.LastPlayedAt)
                .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DashboardResult(files.Count, stake, hero, tables);
        }

        /// <summary>
        /// Heroe de un archivo y sus manos repartidas: el nombre que mas se repite en
        /// "Dealt to &lt;nombre&gt; [". Se cuenta en vez de tomar el primero por si el formato
        /// lista tambien cartas de rivales.
        /// </summary>
        private static (string? HeroName, int DealtHands) DetectHeroFromLines(IReadOnlyList<string> lines)
        {
            var dealtByName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var match = DealtToRx.Match(line);
                if (!match.Success) continue;

                var name = PokerStarsHandHistory.NormalizeName(match.Groups["hero"].Value);
                dealtByName.TryGetValue(name, out var count);
                dealtByName[name] = count + 1;
            }

            if (dealtByName.Count == 0)
                return (null, 0);

            var top = dealtByName.OrderByDescending(kv => kv.Value).First();
            return (top.Key, top.Value);
        }

        private static void VoteStake(Dictionary<StakeProfile, int> votes, StakeProfile stake)
        {
            votes.TryGetValue(stake, out var count);
            votes[stake] = count + 1;
        }

        private static StakeProfile DetectStakeFromLines(IReadOnlyList<string> lines)
        {
            foreach (var line in lines.Take(200))
            {
                var match = BlindsRx.Match(line);
                if (!match.Success) continue;

                if (!PokerAmountParser.TryParse(match.Groups["bb"].Value, out var bb))
                    continue;

                return GetStakeFromBigBlind(bb, match.Groups["bb"].Value);
            }

            return StakeProfile.Low;
        }

        private static TableSessionStats? AnalyzeTableLines(
            string path,
            IReadOnlyList<string> lines,
            string heroName,
            string pokerRoom)
        {
            var agg = new StatsAggregator();
            var parser = new PokerStarsParser(agg);
            parser.FeedLines(lines, () => { });

            if (!agg.Players.TryGetValue(heroName, out var heroStats) || heroStats.HandsReceived == 0)
                return null;

            var (tableName, blindsLabel, bigBlind) = DetectTableInfo(path, lines);
            var lastPlayedAt = DetectLastPlayedAt(lines);
            var netAmount = EstimateHeroNet(lines, heroName);
            var netBb = bigBlind > 0 ? netAmount / bigBlind : 0;
            var isCash = IsCashBlind(blindsLabel);

            return new TableSessionStats(
                $"{tableName} ({blindsLabel})",
                DetectGameFormat(lines),
                lastPlayedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                lastPlayedAt,
                path,
                pokerRoom,
                heroName,
                bigBlind,
                GetStakeFromBigBlind(bigBlind, blindsLabel),
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
                FormatNetAmount(netAmount, isCash, blindsLabel));
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

        private static StakeProfile GetStakeFromBigBlind(double bigBlind, string blindsText) =>
            StakeProfileRules.FromBigBlind(bigBlind, blindsText);

        private static DateTime DetectLastPlayedAt(IReadOnlyList<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var match = HeaderTimestampRx.Match(lines[i]);
                if (!match.Success) continue;

                if (DateTime.TryParseExact(
                    match.Groups["stamp"].Value,
                    "yyyy/MM/dd H:mm:ss",
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

            // PokerStars en espanol escribe "Sin Limite" con tilde y "Limite" tampoco contiene
            // "limit". Comparar sin acentos evita que NLH se detecte como una simple "H".
            var plain = RemoveDiacritics(raw);

            bool Has(string needle) => plain.Contains(needle, StringComparison.OrdinalIgnoreCase);

            var noLimit = Has("No Limit") || Has("sin limite");
            var potLimit = Has("Pot Limit") || Has("limite de bote");
            var anyLimit = Has("limit");

            if (Has("Hold'em") || Has("Holdem"))
            {
                if (noLimit) return "NLH";
                if (potLimit) return "PLH";
                if (anyLimit) return "LH";
                return "H";
            }

            if (Has("Omaha"))
            {
                var suffix = Has("Hi/Lo") || Has("H/L") ? "8" : "";

                if (potLimit) return $"PLO{suffix}";
                if (noLimit) return $"NLO{suffix}";
                if (anyLimit) return $"LO{suffix}";
                return $"O{suffix}";
            }

            return raw;
        }

        private static string RemoveDiacritics(string value)
        {
            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    builder.Append(ch);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
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

        private static bool TryParseAmount(string raw, out double value) =>
            PokerAmountParser.TryParse(raw, out value);

        private static string FormatBlind(string raw) =>
            PokerAmountParser.FormatBlind(raw);

        // Solo la moneda decide si es cash. Un separador decimal no sirve: "1,000/2,000" en
        // fichas de torneo lo tiene y no es dinero.
        private static bool IsCashBlind(string raw) => PokerAmountParser.HasCurrency(raw);

        private static string FormatNetAmount(double amount, bool isCash, string blindsLabel)
        {
            var sign = amount >= 0 ? "+" : "-";
            var absolute = Math.Abs(amount);
            // La moneda de la mesa: antes las mesas en euros (PokerStars.es) salian con "$".
            var currency = blindsLabel.Contains('\u20AC') || blindsLabel.Contains("EUR", StringComparison.OrdinalIgnoreCase) ? "\u20AC"
                : blindsLabel.Contains('\u00A3') || blindsLabel.Contains("GBP", StringComparison.OrdinalIgnoreCase) ? "\u00A3"
                : "$";

            return isCash
                ? $"{sign}{currency}{absolute.ToString("0.00", CultureInfo.InvariantCulture)}"
                : $"{sign}{absolute.ToString("0", CultureInfo.InvariantCulture)} {LocalizationManager.Text("Common.Chips").ToLowerInvariant()}";
        }
    }
}
