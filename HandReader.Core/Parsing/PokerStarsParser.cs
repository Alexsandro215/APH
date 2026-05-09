using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HandReader.Core.Stats;

namespace HandReader.Core.Parsing;

public sealed class PokerStarsParser
{
    private readonly StatsAggregator _agg;

    private readonly List<string> _buffer = new();
    private bool _insideHand = false;

    // Object pools for GC reduction
    private readonly HashSet<string> _didVpipPool = new(StringComparer.Ordinal);
    private readonly List<string> _raisesOrderPool = new();
    private readonly HashSet<string> _playersInHandPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedPrePool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _showersPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _winnersPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firstResponsePool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knownCardsPool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _seqPerPlayerPool = new(StringComparer.Ordinal);
    private readonly List<(int seat, string name)> _seatsPool = new();
    private readonly List<string> _actedPool = new();

    // Hand boundaries / streets
    private static readonly Regex HandStartRx = new(@"^(?:PokerStars Hand #|Mano #)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FlopRx  = new(@"^\*\*\* FLOP \*\*\*", RegexOptions.Compiled);
    private static readonly Regex TurnRx  = new(@"^\*\*\* TURN \*\*\*", RegexOptions.Compiled);
    private static readonly Regex RiverRx = new(@"^\*\*\* RIVER \*\*\*", RegexOptions.Compiled);
    private static readonly Regex SummaryRx = new(@"^\*\*\* (?:SUMMARY|RESUMEN) \*\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HoleCardsHeaderRx = new(@"\*\*\* (?:HOLE CARDS|CARTAS PROPIAS) \*\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Seats
    private static readonly Regex SeatRx = new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:º|°|o|ro|&ordm;))?)\s+(\d+):\s+([^(\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dealt / Showdown (EN y ES)
    private static readonly Regex DealtToRx = new(@"^(?:Dealt to|Repartido a)\s+(.+?)\s+\[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShowsEnRx = new(@":+\s+shows \[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShowsEsRx = new(@":+\s+muestra \[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Winner (EN y ES)
    private static readonly Regex CollectedEnRx = new(@"^([^:]+?)\s+collected", RegexOptions.Compiled);
    private static readonly Regex CollectedEsRx = new(@"^(.+?):?\s+(?:recogió|recoge|cobra|cobró|cobro|recaudó|recaudo|se llevó|se llevo|se lleva el bote)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Player actions (EN y tolerante a ES parcial)
    private static readonly Regex PlayerActionRx = new(@"^(.+?):+\s+(checks|bets|calls|raises|folds|mucks|posts(?: small blind| big blind| the ante)?|is all-in|all-in|va all-in|pasa|apuesta|iguala|paga|sube|no va|tira|pone ciega pequeña y grande|pone ciega pequeña|pone ciega grande|pone ante)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PokerStarsParser(StatsAggregator agg) => _agg = agg;

    public void FeedLines(IEnumerable<string> lines, Action onRendered)
    {
        foreach (var line in lines)
        {
            if (HandStartRx.IsMatch(line))
            {
                FinalizeHand(onRendered);
                _insideHand = true;
                _buffer.Clear();
                _buffer.Add(line);
                continue;
            }

            if (_insideHand)
            {
                _buffer.Add(line);
                if (SummaryRx.IsMatch(line))
                {
                    FinalizeHand(onRendered);
                    _insideHand = false;
                    _buffer.Clear();
                }
            }
        }
    }

    private void FinalizeHand(Action onRendered)
    {
        if (_buffer.Count == 0) return;

        // Clear pools
        _didVpipPool.Clear();
        _raisesOrderPool.Clear();
        _playersInHandPool.Clear();
        _foldedPrePool.Clear();
        _showersPool.Clear();
        _winnersPool.Clear();
        _firstResponsePool.Clear();
        _knownCardsPool.Clear();
        foreach (var list in _seqPerPlayerPool.Values) list.Clear();
        _seatsPool.Clear();
        _actedPool.Clear();

        // ---- SEATS / ORDER (6-max render) ----
        foreach (var l in _buffer)
        {
            var m = SeatRx.Match(l);
            if (m.Success)
                _seatsPool.Add((int.Parse(m.Groups[1].Value), NormalizeName(m.Groups[2].Value)));
        }
        _seatsPool.Sort((a, b) => a.seat.CompareTo(b.seat));
        var orderedBySeat = _seatsPool.Select(s => s.name).ToList();

        if (orderedBySeat.Count == 0)
        {
            // Fallback: quienes actuaron
            foreach (var l in _buffer)
            {
                var ma = PlayerActionRx.Match(l);
                if (ma.Success)
                {
                    var n = NormalizeName(ma.Groups[1].Value);
                    if (!_actedPool.Contains(n)) _actedPool.Add(n);
                }
            }
            orderedBySeat = _actedPool;
        }
        _agg.SetCurrentTableOrder(orderedBySeat);

        // ---- PARTICIPANTES DE LA MANO ----
        foreach (var name in orderedBySeat) _playersInHandPool.Add(name);
        _agg.RegisterHandParticipation(_playersInHandPool);

        // ---- CARTAS CONOCIDAS (hero + shows) ----
        string? hero = null; string? heroCards = null;
        var known = _knownCardsPool;
        foreach (var l in _buffer)
        {
            var md = DealtToRx.Match(l);
            if (md.Success)
            {
                hero = NormalizeName(md.Groups[1].Value);
                heroCards = md.Groups[2].Value;
                known[hero] = NormalizeCards(heroCards);
                break;
            }
        }
        foreach (var l in _buffer)
        {
            var idx1 = l.IndexOf(": shows [", StringComparison.Ordinal);
            var idx2 = l.IndexOf(": muestra [", StringComparison.OrdinalIgnoreCase);
            if (idx1 > 0 || idx2 > 0)
            {
                var name = NormalizeName(l[..(idx1 > 0 ? idx1 : idx2)]);
                var ms = idx1 > 0 ? ShowsEnRx.Match(l) : ShowsEsRx.Match(l);
                if (ms.Success) known[name] = NormalizeCards(ms.Groups[1].Value);
            }
        }

        // ---- ACCIONES (para secuencia por jugador) ----
        var seqPerPlayer = _seqPerPlayerPool;
        foreach (var l in _buffer)
        {
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = NormalizeName(ma.Groups[1].Value);
            var action = NormalizeAction(ma.Groups[2].Value);
            var token = action switch
            {
                "checks" => "X",
                "bets" => "B",
                "calls" => "C",
                "raises" => "R",
                "folds" => "F",
                "mucks" => "M",
                "is all-in" or "all-in" or "va all-in" => "AI",
                _ => null
            };
            if (token is null) continue;
            if (!seqPerPlayer.TryGetValue(name, out var list))
            {
                list = new List<string>(8);
                seqPerPlayer[name] = list;
            }
            list.Add(token);
        }
        foreach (var kv in seqPerPlayer)
            if (kv.Value.Count > 0) _agg.SetActionSeq(kv.Key, string.Join("-", kv.Value));

        // ---- ESTADÃSTICAS PRE-FLOP + set VPIP por mano ----
        AnalyzePreflop(out var vpipThisHand, out var pfrPlayer, out var raisesOrder);
        // Incrementos mÃ©tricas globales
        foreach (var n in vpipThisHand) _agg.IncVPIP(n);
        if (raisesOrder.Count >= 1) _agg.IncPFR(raisesOrder[0]);
        if (raisesOrder.Count >= 2) for (int i = 1; i < raisesOrder.Count; i++) _agg.IncThreeBet(raisesOrder[i]);

        // ---- Postflop Aggro / CBet / FvCBet / WTSD / WSD / WWSF ----
        ComputePostflopAndShowdown(_playersInHandPool, pfrPlayer);

        // ---- WWSF adicional (ganador en mano con flop) ----
        var winners = DetectWinners();
        if (_buffer.Any(ln => FlopRx.IsMatch(ln)))
            foreach (var w in winners) _agg.IncWWSF(w);

        // ---- Ãšltimas 9 celdas segÃºn TU REGLA ----
        // â€¢ Si VPIP en esta mano: token de cartas (si conocidas) o "xx"
        // â€¢ Si NO VPIP: "--"
        var six = orderedBySeat.Take(6).ToList();
        foreach (var p in six)
        {
            string cell;
            if (vpipThisHand.Contains(p))
            {
                cell = known.TryGetValue(p, out var cc) ? ToToken(cc) : "xx";
            }
            else
            {
                cell = "--";
            }
            _agg.PushCell(p, cell);
        }

        onRendered();
    }

    // ---------------- PRE-FLOP ----------------
    private void AnalyzePreflop(out HashSet<string> didVPIP, out string? pfr, out List<string> raisesOrder)
    {
        didVPIP = _didVpipPool;
        raisesOrder = _raisesOrderPool;
        pfr = null;

        int holeIdx = _buffer.FindIndex(l => HoleCardsHeaderRx.IsMatch(l));
        if (holeIdx < 0) return;

        int end = _buffer.FindIndex(holeIdx + 1, l => FlopRx.IsMatch(l) || SummaryRx.IsMatch(l));
        if (end < 0) end = _buffer.Count;

        for (int i = holeIdx + 1; i < end; i++)
        {
            var l = _buffer[i];
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = NormalizeName(ma.Groups[1].Value);
            var action = NormalizeAction(ma.Groups[2].Value);

            if (action.StartsWith("posts")) continue; // no cuenta para VPIP

            if (action is "calls" or "raises" or "bets")
                didVPIP.Add(name);

            if (action == "raises")
            {
                raisesOrder.Add(name);
                if (pfr is null) pfr = name; // primer raiser = PFR
            }
        }
    }

    // ------------- POSTFLOP + SHOWDOWN --------------
    private void ComputePostflopAndShowdown(HashSet<string> playersInHand, string? pfr)
    {
        int flopIdx   = _buffer.FindIndex(l => FlopRx.IsMatch(l));
        int turnIdx   = _buffer.FindIndex(l => TurnRx.IsMatch(l));
        int riverIdx  = _buffer.FindIndex(l => RiverRx.IsMatch(l));
        int summIdx   = _buffer.FindIndex(l => SummaryRx.IsMatch(l));
        if (summIdx < 0) summIdx = _buffer.Count;

        // SawFlop
        if (flopIdx >= 0)
        {
            var foldedPre = _foldedPrePool;
            int holeIdx = _buffer.FindIndex(l => HoleCardsHeaderRx.IsMatch(l));
            for (int i = holeIdx + 1; i < flopIdx; i++)
            {
                var ma = PlayerActionRx.Match(_buffer[i]);
                if (ma.Success && NormalizeAction(ma.Groups[2].Value) == "folds")
                    foldedPre.Add(NormalizeName(ma.Groups[1].Value));
            }
            foreach (var p in playersInHand)
                if (!foldedPre.Contains(p)) _agg.IncSawFlop(p);
        }

        // Escanear calles para Aggro y CBet/FvCBet
        ScanStreetAggro(flopIdx,  turnIdx >= 0 ? turnIdx  : (riverIdx >= 0 ? riverIdx : summIdx), "FLOP", pfr);
        ScanStreetAggro(turnIdx,  riverIdx >= 0 ? riverIdx : summIdx, "TURN", pfr: null);
        ScanStreetAggro(riverIdx, summIdx, "RIVER", pfr: null);

        // Showdown: WTSD (shows/muestra)
        var showers = _showersPool;
        int startSD = (riverIdx >= 0 ? riverIdx : (flopIdx >= 0 ? flopIdx : 0)) + 1;
        for (int i = startSD; i < summIdx; i++)
        {
            var l = _buffer[i];
            var idxEn = l.IndexOf(": shows [", StringComparison.Ordinal);
            var idxEs = l.IndexOf(": muestra [", StringComparison.OrdinalIgnoreCase);
            if (idxEn > 0 || idxEs > 0)
            {
                var name = NormalizeName(l[..(idxEn > 0 ? idxEn : idxEs)]);
                showers.Add(name);
            }
        }
        foreach (var s in showers) _agg.IncWTSD(s);

        // Ganador que ademÃ¡s mostrÃ³ -> WSD
        var winners = DetectWinners();
        foreach (var w in winners)
            if (showers.Contains(w)) _agg.IncWSD(w);
    }

    private void ScanStreetAggro(int start, int endExclusive, string street, string? pfr)
    {
        if (start < 0 || start >= endExclusive) return;

        // Para CBet en FLOP:
        bool sawFirstBet = false;
        string? bettor = null;
        var firstResponse = _firstResponsePool; // ya respondieron al cbet

        for (int i = start + 1; i < endExclusive; i++)
        {
            var l = _buffer[i];
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = NormalizeName(ma.Groups[1].Value);
            var action = NormalizeAction(ma.Groups[2].Value);

            // Aggression counters
            if (action is "bets" or "raises") _agg.IncBR(name);
            if (action is "calls") _agg.IncCall(name);

            // CBet lÃ³gica solo en FLOP
            if (street == "FLOP")
            {
                if (!sawFirstBet && action == "bets")
                {
                    sawFirstBet = true;
                    bettor = name;

                    if (pfr != null)
                    {
                        _agg.IncCBetOpp(pfr);
                        if (bettor == pfr) _agg.IncCBetMade(pfr);
                    }
                    continue;
                }

                if (sawFirstBet && name != bettor && !firstResponse.Contains(name))
                {
                    _agg.IncFacedCBet(name);
                    if (action == "folds") _agg.IncFoldToCBet(name);
                    firstResponse.Add(name);
                }
            }
        }
    }

    private HashSet<string> DetectWinners()
    {
        var winners = _winnersPool;
        foreach (var l in _buffer)
        {
            var en = CollectedEnRx.Match(l);
            if (en.Success) { winners.Add(NormalizeName(en.Groups[1].Value)); continue; }

            var es = CollectedEsRx.Match(l);
            if (es.Success) { winners.Add(NormalizeName(es.Groups[1].Value)); continue; }
        }
        return winners;
    }

    private static string NormalizeCards(string raw) =>
        raw.Replace(" ", string.Empty).Replace(",", string.Empty);

    private static string NormalizeName(string raw) =>
        raw.Trim().TrimEnd(':').Trim();

    private static string NormalizeAction(string raw)
    {
        var action = raw.Trim().ToLowerInvariant();
        if (action.StartsWith("posts", StringComparison.Ordinal) ||
            action.StartsWith("pone ciega", StringComparison.Ordinal) ||
            action.StartsWith("pone ante", StringComparison.Ordinal))
        {
            return "posts";
        }

        return action switch
        {
            "pasa" => "checks",
            "apuesta" => "bets",
            "iguala" or "paga" => "calls",
            "sube" => "raises",
            "no va" => "folds",
            "tira" => "mucks",
            "está all-in" or "esta all-in" => "all-in",
            _ => action
        };
    }

    // "AsKh" -> "AKo"; "AhKh" -> "AKâ™¥"; "TsTd" -> "TT" (ordena por rango: AK, no KA)
    private static string ToToken(string twoCards)
    {
        if (twoCards.Length < 4) return "xx";
        var r1 = Rank(twoCards[0]);
        var s1 = Suit(twoCards[1]);
        var r2 = Rank(twoCards[2]);
        var s2 = Suit(twoCards[3]);
        if (r1 == '?' || r2 == '?' || s1 == '?' || s2 == '?') return "xx";

        if (RankOrder(r2) > RankOrder(r1))
        {
            (r1, r2) = (r2, r1);
            (s1, s2) = (s2, s1);
        }
        if (r1 == r2) return $"{r1}{r2}";

        bool suited = s1 == s2;
        if (suited)
        {
            var sym = "s";
            return $"{r1}{r2}{sym}";
        }
        else
        {
            return $"{r1}{r2}o";
        }
    }

    private static int RankOrder(char r) => r switch
    {
        'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10,
        '9' => 9, '8' => 8, '7' => 7, '6' => 6, '5' => 5, '4' => 4, '3' => 3, '2' => 2,
        _ => 0
    };

    private static char Rank(char c) => char.ToLowerInvariant(c) switch
    {
        'a' => 'A', 'k' => 'K', 'q' => 'Q', 'j' => 'J',
        't' => 'T', '9' => '9', '8' => '8', '7' => '7',
        '6' => '6', '5' => '5', '4' => '4', '3' => '3', '2' => '2',
        _ => '?'
    };

    private static char Suit(char c) => char.ToLowerInvariant(c) switch
    {
        'h' => 'h', 'd' => 'd', 'c' => 'c', 's' => 's', _ => '?'
    };
}

