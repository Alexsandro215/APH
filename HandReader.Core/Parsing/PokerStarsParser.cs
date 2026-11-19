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

    /// <summary>Lineas de la ultima mano terminada (hasta el resumen), para repasarla en la mesa.</summary>
    public IReadOnlyList<string> LastHandLines { get; private set; } = Array.Empty<string>();
    private bool _insideHand = false;

    // Object pools for GC reduction
    private readonly HashSet<string> _didVpipPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _playersInHandPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedPrePool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _winnersPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firstResponsePool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _threeBetOppPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pfrPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activePool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedAnyStreetPool = new(StringComparer.Ordinal);
    private readonly HashSet<string> _showdownPool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knownCardsPool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _seqPerPlayerPool = new(StringComparer.Ordinal);
    private readonly List<(int seat, string name)> _seatsPool = new();
    private readonly List<string> _actedPool = new();

    // Hand boundaries / streets
    /// <summary>
    /// Inicio de mano de PokerStars en todos sus formatos: "PokerStars Hand #", "PokerStars Zoom
    /// Hand #", "PokerStars Home Game Hand #", el antiguo "PokerStars Game #" y el cliente en
    /// espanol ("Mano #" / "Mano n.").  Antes solo se reconocia "PokerStars Hand #": las manos de
    /// Zoom se ignoraban por completo.
    /// </summary>
    public const string HandStartPattern =
        @"^(?:PokerStars(?:\s+[\w'-]+)*?\s+(?:Hand|Game|Mano)\s*(?:#|n\b)|Mano\s+(?:#|n\b))";

    private static readonly Regex HandStartRx = new(HandStartPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FlopRx  = new(@"^\*\*\* FLOP \*\*\*", RegexOptions.Compiled);
    private static readonly Regex TurnRx  = new(@"^\*\*\* TURN \*\*\*", RegexOptions.Compiled);
    private static readonly Regex RiverRx = new(@"^\*\*\* RIVER \*\*\*", RegexOptions.Compiled);
    private static readonly Regex SummaryRx = new(@"^\*\*\* (?:SUMMARY|RESUMEN) \*\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShowdownRx = new(@"^\*\*\* (?:SHOW ?DOWN|CONFRONTACI[OÓ]N) \*\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Lineas del bloque SUMMARY ("Seat 1: Jugador (button) collected ..."): nunca son la linea
    // de accion real y confundirlas inventa jugadores fantasma.
    private static readonly Regex SeatPrefixRx = new(@"^(?:Seat|Asiento)\b\s*(?:n\.?\s*(?:º|°|o|ro|&ordm;)\s*)?\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SittingOutRx = new(@"(?:is sitting out|sits out|sitting out|se sienta fuera|est[aá] sentado fuera|sentado fuera)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HoleCardsHeaderRx = new(@"\*\*\* (?:HOLE CARDS|CARTAS PROPIAS) \*\*\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TableSizeRx = new(@"\b(\d{1,2})-max\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Seats
    private static readonly Regex SeatRx = new(@"^(?:Seat|Asiento(?:\s+n\.?\s*(?:º|°|o|ro|&ordm;))?)\s+(\d+):\s+([^(\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dealt / Showdown (EN y ES)
    private static readonly Regex DealtToRx = new(@"^(?:Dealt to|Repartido a)\s+(.+?)\s+\[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShowsEnRx = new(@":+\s+shows \[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShowsEsRx = new(@":+\s+muestra \[(.+?)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Winner (EN y ES)
    private static readonly Regex CollectedEnRx = new(@"^([^:]+?)\s+collected", RegexOptions.Compiled);
    // ':' opcional tras el nombre: el ganador aparece como "Jugador recoge ..." o como
    // "Jugador: recaudo ...". Las lineas del resumen ("Asiento 1: ...") ya las descarta
    // SeatPrefixRx en TryGetCollectorName.
    private static readonly Regex CollectedEsRx = new(@"^(?<name>.+?):?\s+(?:recogió|recoge|cobra|cobró|cobro|recaudó|recaudo|se llevó|se llevo|se lleva el bote)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Player actions (EN y tolerante a ES parcial)
    private static readonly Regex PlayerActionRx = new(@"^(.+?):+\s+(checks|bets|calls|raises|folds|mucks|posts(?: small blind| big blind| the ante)?|is all-in|all-in|va all-in|est[aá] all-in|pasa|apuesta|iguala|paga|sube|no va|tira|pone ciega pequeña y grande|pone ciega pequeña|pone ciega grande|pone ante)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        LastHandLines = _buffer.ToArray();

        // Clear pools
        _didVpipPool.Clear();
        _playersInHandPool.Clear();
        _foldedPrePool.Clear();
        _winnersPool.Clear();
        _firstResponsePool.Clear();
        _threeBetOppPool.Clear();
        _pfrPool.Clear();
        _activePool.Clear();
        _foldedAnyStreetPool.Clear();
        _showdownPool.Clear();
        _knownCardsPool.Clear();
        _seqPerPlayerPool.Clear();
        _seatsPool.Clear();
        _actedPool.Clear();

        // ---- SEATS / ORDER ----
        // Solo los asientos de la cabecera (antes de las cartas). El resumen final repite
        // "Seat 3: nombre (button) collected..." y "Seat 1: nombre showed [..] and won...": leerlos
        // duplicaba jugadores, inventaba nombres y la lista de la mesa (y el HUD) salia revuelta.
        var tableSize = 0;
        var sittingOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in _buffer)
        {
            if (HoleCardsHeaderRx.IsMatch(l) || SummaryRx.IsMatch(l))
                break;
            if (tableSize == 0)
            {
                var max = TableSizeRx.Match(l);
                if (max.Success)
                    int.TryParse(max.Groups[1].Value, out tableSize);
            }
            var m = SeatRx.Match(l);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var seatNumber))
            {
                var name = NormalizeName(m.Groups[2].Value);
                if (_seatsPool.Any(s => s.seat == seatNumber))
                    continue;
                _seatsPool.Add((seatNumber, name));
                if (SittingOutRx.IsMatch(l))
                    sittingOut.Add(name);
            }
        }
        _seatsPool.Sort((a, b) => a.seat.CompareTo(b.seat));
        var orderedBySeat = _seatsPool.Select(s => s.name).ToList();
        if (_seatsPool.Count > 0)
        {
            var highest = _seatsPool[^1].seat;
            if (tableSize < highest)
                tableSize = highest <= 2 ? 2 : highest <= 6 ? 6 : highest <= 9 ? 9 : 10;
            _agg.SetCurrentSeats(_seatsPool.Select(s => new TableSeat(s.seat, s.name, sittingOut.Contains(s.name))), tableSize);
        }

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

        // ---- PARTICIPANTES REALES DE LA MANO ----
        // Estar sentado no es estar en la mano: los jugadores en "sitting out" aparecen en las
        // lineas Seat pero no reciben cartas. Contarlos hundiria VPIP/PFR/3Bet de todo el HUD.
        CollectActivePlayers();
        foreach (var name in orderedBySeat)
            if (_activePool.Contains(name)) _playersInHandPool.Add(name);
        if (_playersInHandPool.Count == 0)
            foreach (var name in orderedBySeat) _playersInHandPool.Add(name);
        _agg.RegisterHandParticipation(_playersInHandPool);

        // ---- CARTAS CONOCIDAS (hero + shows) ----
        var known = _knownCardsPool;
        foreach (var l in _buffer)
        {
            var md = DealtToRx.Match(l);
            if (md.Success)
            {
                known[NormalizeName(md.Groups[1].Value)] = NormalizeCards(md.Groups[2].Value);
                break;
            }
        }
        foreach (var l in _buffer)
        {
            if (TryGetShowdownName(l, out var showerName, out var showMatch) && showMatch.Success)
                known[showerName] = NormalizeCards(showMatch.Groups[1].Value);
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

        // ---- CABECERAS DE CALLE (se buscan una sola vez por mano) ----
        var streets = LocateStreets();

        // ---- PRE-FLOP: VPIP / PFR / 3Bet ----
        AnalyzePreflop(streets, out var vpipThisHand, out var pfrThisHand, out var preflopAggressor);
        foreach (var n in vpipThisHand) _agg.IncVPIP(n);
        // PFR: todo jugador que subio preflop (open, 3bet, 4bet...), una vez por mano.
        foreach (var n in pfrThisHand) _agg.IncPFR(n);

        // ---- Postflop Aggro / CBet / FvCBet / WTSD / WSD ----
        var winners = DetectWinners();
        ComputePostflopAndShowdown(_playersInHandPool, preflopAggressor, winners, streets);

        // ---- WWSF (gano una mano en la que llego al flop) ----
        if (streets.Flop >= 0)
            foreach (var w in winners)
                if (_playersInHandPool.Contains(w) && !_foldedPrePool.Contains(w)) _agg.IncWWSF(w);

        // ---- Ultimas 9 celdas ----
        // - Si VPIP en esta mano: token de cartas (si conocidas) o "xx"
        // - Si NO VPIP: "--"
        foreach (var p in orderedBySeat)
        {
            if (!_playersInHandPool.Contains(p)) continue;

            string cell;
            if (vpipThisHand.Contains(p))
                cell = known.TryGetValue(p, out var cc) ? ToToken(cc) : "xx";
            else
                cell = "--";

            _agg.PushCell(p, cell);
        }

        onRendered();
    }

    /// <summary>
    /// Jugadores con actividad real en la mano: actuaron, recibieron cartas, mostraron o cobraron.
    /// Excluye a los que solo aparecen sentados (sitting out).
    /// </summary>
    private void CollectActivePlayers()
    {
        foreach (var l in _buffer)
        {
            if (SittingOutRx.IsMatch(l)) continue;

            var ma = PlayerActionRx.Match(l);
            if (ma.Success) { _activePool.Add(NormalizeName(ma.Groups[1].Value)); continue; }

            var md = DealtToRx.Match(l);
            if (md.Success) { _activePool.Add(NormalizeName(md.Groups[1].Value)); continue; }

            if (TryGetShowdownName(l, out var showerName, out _)) { _activePool.Add(showerName); continue; }

            if (TryGetCollectorName(l, out var collectorName)) _activePool.Add(collectorName);
        }
    }

    private static bool TryGetShowdownName(string line, out string name, out Match cardsMatch)
    {
        var idxEn = line.IndexOf(": shows [", StringComparison.Ordinal);
        var idxEs = line.IndexOf(": muestra [", StringComparison.OrdinalIgnoreCase);
        if (idxEn > 0 || idxEs > 0)
        {
            name = NormalizeName(line[..(idxEn > 0 ? idxEn : idxEs)]);
            cardsMatch = idxEn > 0 ? ShowsEnRx.Match(line) : ShowsEsRx.Match(line);
            return true;
        }

        name = string.Empty;
        cardsMatch = Match.Empty;
        return false;
    }

    private static bool TryGetCollectorName(string line, out string name)
    {
        name = string.Empty;

        // Las lineas del SUMMARY repiten al ganador; contarlas duplica o inventa jugadores.
        if (SeatPrefixRx.IsMatch(line)) return false;

        var en = CollectedEnRx.Match(line);
        if (en.Success) { name = NormalizeName(en.Groups[1].Value); return true; }

        var es = CollectedEsRx.Match(line);
        if (es.Success) { name = NormalizeName(es.Groups["name"].Value); return true; }

        return false;
    }

    /// <summary>
    /// Posicion de cada cabecera en el buffer de la mano (-1 si no aparece). Summary vale
    /// _buffer.Count cuando la mano no trae resumen, para usarlo directamente como limite.
    /// </summary>
    private readonly record struct StreetIndexes(int Hole, int Flop, int Turn, int River, int Showdown, int Summary);

    private StreetIndexes LocateStreets()
    {
        int hole = -1, flop = -1, turn = -1, river = -1, showdown = -1, summary = -1;
        for (int i = 0; i < _buffer.Count; i++)
        {
            var l = _buffer[i];
            // Todas las cabeceras llevan "***": el resto de lineas no necesita pasar por los regex.
            if (!l.Contains("***", StringComparison.Ordinal)) continue;

            if (hole < 0 && HoleCardsHeaderRx.IsMatch(l)) hole = i;
            if (flop < 0 && FlopRx.IsMatch(l)) flop = i;
            if (turn < 0 && TurnRx.IsMatch(l)) turn = i;
            if (river < 0 && RiverRx.IsMatch(l)) river = i;
            if (showdown < 0 && ShowdownRx.IsMatch(l)) showdown = i;
            if (summary < 0 && SummaryRx.IsMatch(l)) summary = i;
        }

        return new StreetIndexes(hole, flop, turn, river, showdown, summary < 0 ? _buffer.Count : summary);
    }

    // ---------------- PRE-FLOP ----------------
    private void AnalyzePreflop(StreetIndexes streets, out HashSet<string> didVPIP, out HashSet<string> raisers, out string? aggressor)
    {
        didVPIP = _didVpipPool;
        raisers = _pfrPool;
        aggressor = null;

        int holeIdx = streets.Hole;
        if (holeIdx < 0) return;

        // El preflop acaba en el flop o, si no lo hubo, en el resumen.
        int end = streets.Flop > holeIdx ? streets.Flop : streets.Summary;

        var raisesSoFar = 0;
        for (int i = holeIdx + 1; i < end; i++)
        {
            var l = _buffer[i];
            var ma = PlayerActionRx.Match(l);
            if (!ma.Success) continue;

            var name = NormalizeName(ma.Groups[1].Value);
            var action = NormalizeAction(ma.Groups[2].Value);

            if (action.StartsWith("posts")) continue; // no cuenta para VPIP

            // 3Bet: subir enfrentando exactamente un open raise. Un 4bet/5bet ya no cuenta como
            // 3bet, y la oportunidad se registra aunque el jugador acabe igualando o foldeando.
            if (raisesSoFar == 1 &&
                action is "calls" or "raises" or "folds" &&
                _threeBetOppPool.Add(name))
            {
                _agg.IncThreeBetOpp(name);
                if (action == "raises") _agg.IncThreeBet(name);
            }

            if (action is "calls" or "raises" or "bets")
                didVPIP.Add(name);

            if (action == "raises")
            {
                raisesSoFar++;
                raisers.Add(name); // cualquier subida preflop cuenta para PFR, no solo la primera
                aggressor = name; // ultimo raiser = agresor preflop, el unico que puede hacer CBet
            }
        }
    }

    // ------------- POSTFLOP + SHOWDOWN --------------
    private void ComputePostflopAndShowdown(HashSet<string> playersInHand, string? aggressor, HashSet<string> winners, StreetIndexes streets)
    {
        int flopIdx  = streets.Flop;
        int turnIdx  = streets.Turn;
        int riverIdx = streets.River;
        int summIdx  = streets.Summary;

        // Quien foldeo preflop y quien foldeo en cualquier calle
        var foldedPre = _foldedPrePool;
        var foldedAny = _foldedAnyStreetPool;
        for (int i = Math.Max(streets.Hole, 0); i < summIdx; i++)
        {
            var ma = PlayerActionRx.Match(_buffer[i]);
            if (!ma.Success || NormalizeAction(ma.Groups[2].Value) != "folds") continue;

            var name = NormalizeName(ma.Groups[1].Value);
            foldedAny.Add(name);
            if (flopIdx < 0 || i < flopIdx) foldedPre.Add(name);
        }

        // SawFlop
        if (flopIdx >= 0)
        {
            foreach (var p in playersInHand)
                if (!foldedPre.Contains(p)) _agg.IncSawFlop(p);
        }

        // CBet: solo puede hacerla el agresor preflop (el ultimo que subio) si sigue en la mano.
        // Si tuvo oportunidad o no lo decide su primera accion en el flop (ScanStreetAggro).
        var cbetAggressor = aggressor != null && playersInHand.Contains(aggressor) && !foldedPre.Contains(aggressor)
            ? aggressor
            : null;

        // Escanear calles para Aggro y CBet/FvCBet
        ScanStreetAggro(flopIdx, turnIdx >= 0 ? turnIdx : (riverIdx >= 0 ? riverIdx : summIdx), "FLOP", cbetAggressor);
        ScanStreetAggro(turnIdx, riverIdx >= 0 ? riverIdx : summIdx, "TURN", aggressor: null);
        ScanStreetAggro(riverIdx, summIdx, "RIVER", aggressor: null);

        // WTSD: llegar al showdown, se muestren o no las cartas (quien hace muck tambien llego).
        var wentToShowdown = _showdownPool;
        if (streets.Showdown >= 0 || IsShowdownWithoutHeader(playersInHand, foldedAny, streets))
        {
            foreach (var p in playersInHand)
                if (!foldedAny.Contains(p)) wentToShowdown.Add(p);
        }

        foreach (var s in wentToShowdown) _agg.IncWTSD(s);

        // Ganador que ademas llego al showdown -> WSD
        foreach (var w in winners)
            if (wentToShowdown.Contains(w)) _agg.IncWSD(w);
    }

    /// <summary>
    /// Respaldo para historiales sin cabecera de showdown: solo lo hubo si quedan al menos dos
    /// jugadores sin foldear y alguien muestra cartas tras la ultima calle. El ganador que muestra
    /// sus cartas tras un bote sin disputa no fue al showdown.
    /// </summary>
    private bool IsShowdownWithoutHeader(HashSet<string> playersInHand, HashSet<string> foldedAny, StreetIndexes streets)
    {
        var stillIn = 0;
        foreach (var p in playersInHand)
            if (!foldedAny.Contains(p)) stillIn++;
        if (stillIn < 2) return false;

        // Ultima cabecera de calle encontrada (-1 si no hay ninguna); las cabeceras van en orden.
        int lastStreetIdx = Math.Max(Math.Max(streets.Hole, streets.Flop), Math.Max(streets.Turn, streets.River));
        for (int i = lastStreetIdx + 1; i < streets.Summary; i++)
            if (TryGetShowdownName(_buffer[i], out _, out _)) return true;

        return false;
    }

    private void ScanStreetAggro(int start, int endExclusive, string street, string? aggressor)
    {
        if (start < 0 || start >= endExclusive) return;

        // Para CBet en FLOP: solo hay CBet si la primera apuesta de la calle es del agresor preflop.
        bool sawFirstBet = false;
        bool aggressorActed = false;
        bool cbetMade = false;
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

            // CBet logica solo en FLOP
            if (street == "FLOP" && aggressor != null)
            {
                if (!sawFirstBet)
                {
                    // Oportunidad: primera accion del agresor con el flop aun sin apuestas. Si otro
                    // apuesta antes (donk bet) o el agresor no actua (all-in previo), no la hubo.
                    if (name == aggressor && !aggressorActed && action is "checks" or "bets")
                    {
                        _agg.IncCBetOpp(aggressor);
                        if (action == "bets")
                        {
                            _agg.IncCBetMade(aggressor);
                            cbetMade = true;
                        }
                    }

                    if (name == aggressor) aggressorActed = true;
                    if (action == "bets") sawFirstBet = true;
                    continue;
                }

                // Fold vs CBet: primera respuesta de cada rival a la CBet, aunque antes pasara.
                // Una donk bet no es CBet, asi que nadie la cuenta como CBet afrontada.
                if (cbetMade && name != aggressor && !firstResponse.Contains(name))
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
            if (TryGetCollectorName(l, out var name)) winners.Add(name);

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

