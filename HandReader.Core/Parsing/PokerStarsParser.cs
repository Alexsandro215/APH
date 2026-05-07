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

    // Hand boundaries / streets
    private static readonly Regex HandStartRx = new(@"^PokerStars Hand #", RegexOptions.Compiled);
    private static readonly Regex FlopRx  = new(@"^\*\*\* FLOP \*\*\*", RegexOptions.Compiled);
    private static readonly Regex TurnRx  = new(@"^\*\*\* TURN \*\*\*", RegexOptions.Compiled);
    private static readonly Regex RiverRx = new(@"^\*\*\* RIVER \*\*\*", RegexOptions.Compiled);
    private static readonly Regex SummaryRx = new(@"^\*\*\* SUMMARY \*\*\*", RegexOptions.Compiled);
    private static readonly Regex HoleCardsHeaderRx = new(@"\*\*\* HOLE CARDS \*\*\*", RegexOptions.Compiled);

    // Seats
    private static readonly Regex SeatRx = new(@"^Seat\s+(\d+):\s+([^(\r\n]+)", RegexOptions.Compiled);

    // Dealt / Showdown (EN y ES)
    private static readonly Regex DealtToRx = new(@"^Dealt to\s+(.+?)\s+\[(.+?)\]", RegexOptions.Compiled);
    private static readonly Regex ShowsEnRx = new(@": shows \[(.+?)\]", RegexOptions.Compiled);
    private static readonly Regex ShowsEsRx = new(@": muestra \[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Winner (EN y ES)
    private static readonly Regex CollectedEnRx = new(@"^([^:]+?)\s+collected", RegexOptions.Compiled);
    private static readonly Regex CollectedEsRx = new(@"^([^:]+?)\s+(recogió|recoge|se lleva el bote)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Player actions (EN y tolerante a ES parcial)
    private static readonly Regex PlayerActionRx = new(@"^([^:]+):\s+(checks|bets|calls|raises|folds|mucks|posts(?: small blind| big blind| the ante)?|is all-in|all-in|va all-in)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        // ---- SEATS / ORDER (6-max render) ----
        var seats = new List<(int seat, string name)>();
        foreach (var l in _buffer)
        {
            var m = SeatRx.Match(l);
            if (m.Success)
                seats.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()));
        }
        seats.Sort((a, b) => a.seat.CompareTo(b.seat));
        var orderedBySeat = seats.Select(s => s.name).ToList();

        if (orderedBySeat.Count == 0)
        {
            // Fallback: quienes actuaron
            var acted = new List<string>();
            foreach (var l in _buffer)
            {
                var ma = PlayerActionRx.Match(l);
                if (ma.Success)
                {
                    var n = ma.Groups[1].Value;
                    if (!acted.Contains(n)) acted.Add(n);
                }
            }
            orderedBySeat = acted;
        }
        _agg.SetCurrentTableOrder(orderedBySeat);

        // ---- PARTICIPANTES DE LA MANO ----
        var playersInHand = new HashSet<string>(orderedBySeat, StringComparer.Ordinal);
        _agg.RegisterHandParticipation(playersInHand);

        // ---- CARTAS CONOCIDAS (hero + shows) ----
        string? hero = null; string? heroCards = null;
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in _buffer)
        {
            var md = DealtToRx.Match(l);
            if (md.Success)
            {
                hero = md.Groups[1].Value;
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
                var name = l[..(idx1 > 0 ? idx1 : idx2)].Trim();
                var ms = idx1 > 0 ? ShowsEnRx.Match(l) : ShowsEsRx.Match(l);
                if (ms.Success) known[name] = NormalizeCards(ms.Groups[1].Value);
            }
        }

        // ---- ACCIONES (para secuencia por jugador) ----
        var seqPerPlayer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var l in _buffer)
        {
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = ma.Groups[1].Value;
            var action = ma.Groups[2].Value.ToLowerInvariant();
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
            _agg.SetActionSeq(kv.Key, string.Join("-", kv.Value));

        // ---- ESTADÍSTICAS PRE-FLOP + set VPIP por mano ----
        AnalyzePreflop(out var vpipThisHand, out var pfrPlayer, out var raisesOrder);
        // Incrementos métricas globales
        foreach (var n in vpipThisHand) _agg.IncVPIP(n);
        if (raisesOrder.Count >= 1) _agg.IncPFR(raisesOrder[0]);
        if (raisesOrder.Count >= 2) for (int i = 1; i < raisesOrder.Count; i++) _agg.IncThreeBet(raisesOrder[i]);

        // ---- Postflop Aggro / CBet / FvCBet / WTSD / WSD / WWSF ----
        ComputePostflopAndShowdown(playersInHand, pfrPlayer);

        // ---- WWSF adicional (ganador en mano con flop) ----
        var winners = DetectWinners();
        if (_buffer.Any(ln => FlopRx.IsMatch(ln)))
            foreach (var w in winners) _agg.IncWWSF(w);

        // ---- Últimas 9 celdas según TU REGLA ----
        // • Si VPIP en esta mano: token de cartas (si conocidas) o "xx"
        // • Si NO VPIP: "--"
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
        didVPIP = new HashSet<string>(StringComparer.Ordinal);
        raisesOrder = new List<string>();
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

            var name = ma.Groups[1].Value;
            var action = ma.Groups[2].Value.ToLowerInvariant();

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
            var foldedPre = new HashSet<string>(StringComparer.Ordinal);
            int holeIdx = _buffer.FindIndex(l => HoleCardsHeaderRx.IsMatch(l));
            for (int i = holeIdx + 1; i < flopIdx; i++)
            {
                var ma = PlayerActionRx.Match(_buffer[i]);
                if (ma.Success && ma.Groups[2].Value.Equals("folds", StringComparison.OrdinalIgnoreCase))
                    foldedPre.Add(ma.Groups[1].Value);
            }
            foreach (var p in playersInHand)
                if (!foldedPre.Contains(p)) _agg.IncSawFlop(p);
        }

        // Escanear calles para Aggro y CBet/FvCBet
        ScanStreetAggro(flopIdx,  turnIdx >= 0 ? turnIdx  : (riverIdx >= 0 ? riverIdx : summIdx), "FLOP", pfr);
        ScanStreetAggro(turnIdx,  riverIdx >= 0 ? riverIdx : summIdx, "TURN", pfr: null);
        ScanStreetAggro(riverIdx, summIdx, "RIVER", pfr: null);

        // Showdown: WTSD (shows/muestra)
        var showers = new HashSet<string>(StringComparer.Ordinal);
        int startSD = (riverIdx >= 0 ? riverIdx : (flopIdx >= 0 ? flopIdx : 0)) + 1;
        for (int i = startSD; i < summIdx; i++)
        {
            var l = _buffer[i];
            var idxEn = l.IndexOf(": shows [", StringComparison.Ordinal);
            var idxEs = l.IndexOf(": muestra [", StringComparison.OrdinalIgnoreCase);
            if (idxEn > 0 || idxEs > 0)
            {
                var name = l[..(idxEn > 0 ? idxEn : idxEs)].Trim();
                showers.Add(name);
            }
        }
        foreach (var s in showers) _agg.IncWTSD(s);

        // Ganador que además mostró -> WSD
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
        var firstResponse = new HashSet<string>(StringComparer.Ordinal); // ya respondieron al cbet

        for (int i = start + 1; i < endExclusive; i++)
        {
            var l = _buffer[i];
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = ma.Groups[1].Value;
            var action = ma.Groups[2].Value.ToLowerInvariant();

            // Aggression counters
            if (action is "bets" or "raises") _agg.IncBR(name);
            if (action is "calls") _agg.IncCall(name);

            // CBet lógica solo en FLOP
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
        var winners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in _buffer)
        {
            var en = CollectedEnRx.Match(l);
            if (en.Success) { winners.Add(en.Groups[1].Value); continue; }

            var es = CollectedEsRx.Match(l);
            if (es.Success) { winners.Add(es.Groups[1].Value); continue; }
        }
        return winners;
    }

    private static string NormalizeCards(string raw) =>
        raw.Replace(" ", string.Empty).Replace(",", string.Empty);

    // "AsKh" -> "AKo"; "AhKh" -> "AK♥"; "TsTd" -> "TT" (ordena por rango: AK, no KA)
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
            var sym = s1 switch { 'h' => '♥', 'd' => '♦', 'c' => '♣', 's' => '♠', _ => 's' };
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
