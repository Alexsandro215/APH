using System.IO;
using System.Text.RegularExpressions;

namespace Hud.App.Services
{
    /// <summary>
    /// Una decision preflop real del jugador: en que situacion estaba, con que mano y que hizo.
    /// Sirve para compararla con las tablas y para practicar con sus propias manos.
    /// </summary>
    public sealed record PreflopSpot(
        string Player,
        string Scenario,
        string Hero,
        string Villain,
        string Hand,
        string Cards,
        string Action,
        string Table,
        string HandId);

    /// <summary>
    /// Lee las manos de PokerStars (texto en ingles) y saca cada decision preflop que tiene tabla:
    /// abrir, contra limp, contra subida, squeeze, contra 3bet, contra 4bet y contra 5bet, y las de
    /// heads-up. Solo mesas de 2 a 6 jugadores (las tablas son de 6-max) y sin torneos.
    /// </summary>
    public static class PreflopSpotReader
    {
        private static readonly Regex HeaderIdRx = new(@"#(?<id>\d+)", RegexOptions.Compiled);
        private static readonly Regex TableRx = new(@"^Table '(?<name>[^']+)'.*?Seat #(?<button>\d+) is the button", RegexOptions.Compiled);
        private static readonly Regex SeatRx = new(@"^Seat (?<seat>\d+): (?<name>.+?) \([^)]*in chips[^)]*\)(?<rest>.*)$", RegexOptions.Compiled);
        private static readonly Regex DealtRx = new(@"^Dealt to (?<name>.+?) \[(?<c1>[2-9TJQKA][cdhs]) (?<c2>[2-9TJQKA][cdhs])\]", RegexOptions.Compiled);
        private static readonly Regex ActionRx = new(@"^(?<name>.+?): (?<verb>folds|checks|calls|raises|bets)\b", RegexOptions.Compiled);

        private const string RankOrder = "AKQJT98765432";

        private static readonly object CacheSync = new();
        private static string? _cacheKey;
        private static Task<List<PreflopSpot>>? _cacheTask;

        /// <summary>
        /// Lee en segundo plano y guarda el resultado mientras los archivos no cambien de tamano (en
        /// una sesion en curso PokerStars sigue escribiendo manos al final del archivo).
        /// </summary>
        public static Task<List<PreflopSpot>> LoadAsync(IReadOnlyCollection<string> paths)
        {
            var key = string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p =>
            {
                try { return $"{p}:{(File.Exists(p) ? new FileInfo(p).Length : -1)}"; }
                catch { return p; }
            }));

            lock (CacheSync)
            {
                if (_cacheTask is not null && _cacheKey == key && !_cacheTask.IsFaulted)
                    return _cacheTask;
                _cacheKey = key;
                _cacheTask = Task.Run(() => ReadFiles(paths));
                return _cacheTask;
            }
        }

        public static List<PreflopSpot> ReadFiles(IEnumerable<string> paths)
        {
            var spots = new List<PreflopSpot>();
            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(path))
                        continue;
                    foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadAllLines(path)))
                        spots.AddRange(ReadHand(hand));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"No se pudieron leer las decisiones preflop de {path}", ex);
                }
            }
            return spots;
        }

        public static IEnumerable<PreflopSpot> ReadHand(IReadOnlyList<string> lines)
        {
            if (lines.Count < 3 || lines[0].Contains("Tournament", StringComparison.OrdinalIgnoreCase))
                yield break;

            var table = TableRx.Match(lines[1]);
            if (!table.Success)
                yield break;
            var buttonSeat = int.Parse(table.Groups["button"].Value);
            var handId = HeaderIdRx.Match(lines[0]).Groups["id"].Value;

            var seats = new List<(int Seat, string Name)>();
            var holeIndex = -1;
            for (var i = 2; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("*** HOLE CARDS", StringComparison.Ordinal)) { holeIndex = i; break; }
                var seat = SeatRx.Match(lines[i]);
                if (seat.Success && !seat.Groups["rest"].Value.Contains("sitting out") && !seat.Groups["rest"].Value.Contains("out of hand"))
                    seats.Add((int.Parse(seat.Groups["seat"].Value), seat.Groups["name"].Value));
            }
            if (holeIndex < 0 || seats.Count is < 2 or > 6)
                yield break;

            var positions = Positions(seats, buttonSeat);
            var headsUp = seats.Count == 2;

            string? hero = null, cards = null;
            var actions = new List<(string Name, string Verb)>();
            for (var i = holeIndex + 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.StartsWith("*** ", StringComparison.Ordinal))
                    break;
                var dealt = DealtRx.Match(line);
                if (dealt.Success)
                {
                    hero = dealt.Groups["name"].Value;
                    cards = dealt.Groups["c1"].Value + dealt.Groups["c2"].Value;
                    continue;
                }
                var action = ActionRx.Match(line);
                if (action.Success && positions.ContainsKey(action.Groups["name"].Value))
                    actions.Add((action.Groups["name"].Value, action.Groups["verb"].Value));
            }
            if (hero is null || cards is null || !positions.TryGetValue(hero, out var heroPos))
                yield break;

            var hand = HandClass(cards);
            var tableName = table.Groups["name"].Value;
            var raises = 0;
            var limpers = new List<string>();
            var limpersBeforeRaise = 0;
            var callersSinceRaise = 0;
            string? opener = null, lastRaiser = null;
            string? heroLine = null; // situacion de la decision anterior del heroe, si subio

            foreach (var (name, verb) in actions)
            {
                if (name == hero)
                {
                    var code = verb switch { "raises" or "bets" => "R", "calls" or "checks" => "C", _ => "F" };
                    var spot = Classify();
                    if (spot is not null)
                        yield return new PreflopSpot(hero, spot.Value.Scenario, heroPos, spot.Value.Villain, hand, cards, code, tableName, handId);

                    // Solo se sigue la linea del heroe si subio en una situacion con tabla para la siguiente.
                    heroLine = code == "R" && spot is not null ? PreflopScenarios.BaseOf(spot.Value.Scenario) : "stop";
                    if (heroLine == "stop")
                        yield break;
                }

                switch (verb)
                {
                    case "raises":
                    case "bets":
                        raises++;
                        if (raises == 1) { opener = name; limpersBeforeRaise = limpers.Count; }
                        lastRaiser = name;
                        callersSinceRaise = 0;
                        break;
                    case "calls":
                        if (raises == 0) limpers.Add(name);
                        else callersSinceRaise++;
                        break;
                }
            }

            (string Scenario, string Villain)? Classify()
            {
                string Pos(string? name) => name is not null && positions.TryGetValue(name, out var p) ? p : "";
                string Name(string scenario) => headsUp ? "HU " + scenario : scenario;

                switch (heroLine)
                {
                    case null when raises == 0 && limpers.Count == 0:
                        return (Name(PreflopScenarios.Open), "");
                    case null when raises == 0:
                        return headsUp ? null : (PreflopScenarios.VsLimp, Pos(limpers[0]));
                    case null when raises == 1 && limpersBeforeRaise == 0:
                        return callersSinceRaise == 0
                            ? (Name(PreflopScenarios.VsRaise), Pos(opener))
                            : headsUp ? null : (PreflopScenarios.Squeeze, Pos(opener));
                    case PreflopScenarios.Open when raises == 2:
                        return (Name(PreflopScenarios.Vs3Bet), Pos(lastRaiser));
                    case PreflopScenarios.VsRaise when raises == 3:
                        return (Name(PreflopScenarios.Vs4Bet), Pos(lastRaiser));
                    case PreflopScenarios.Vs3Bet when raises == 4 && !headsUp:
                        return (PreflopScenarios.Vs5Bet, Pos(lastRaiser));
                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// Posiciones por asiento. Heads-up: el boton es la ciega pequena. Con 3 a 6 jugadores:
        /// despues del boton SB y BB; antes del boton CO, HJ y LJ (UTG).
        /// </summary>
        public static Dictionary<string, string> Positions(IReadOnlyList<(int Seat, string Name)> seats, int buttonSeat)
        {
            var ordered = seats.OrderBy(s => s.Seat).ToList();
            var start = ordered.FindIndex(s => s.Seat > buttonSeat);
            if (start < 0) start = 0;
            var rotated = ordered.Skip(start).Concat(ordered.Take(start)).ToList(); // empieza despues del boton
            var n = rotated.Count;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (n == 2)
            {
                result[rotated[1].Name] = "SB"; // el boton
                result[rotated[0].Name] = "BB";
                return result;
            }

            for (var i = 0; i < n; i++)
            {
                result[rotated[i].Name] = i switch
                {
                    0 => "SB",
                    1 => "BB",
                    _ when i == n - 1 => "BTN",
                    _ when i == n - 2 => "CO",
                    _ when i == n - 3 => "HJ",
                    _ => "LJ"
                };
            }
            return result;
        }

        /// <summary>"Ad8c" -> "A8o"; "KhQh" -> "KQs"; "7s7d" -> "77".</summary>
        public static string HandClass(string cards)
        {
            var r1 = cards[0];
            var r2 = cards[2];
            if (RankOrder.IndexOf(r1) > RankOrder.IndexOf(r2)) (r1, r2) = (r2, r1);
            if (r1 == r2) return $"{r1}{r2}";
            return $"{r1}{r2}{(cards[1] == cards[3] ? 's' : 'o')}";
        }
    }
}
