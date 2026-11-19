using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hud.App.Services.Solver;

namespace Hud.App.Services.Trainer
{
    /// <summary>Decision recomendada para el turno actual del alumno, lista para mostrar.</summary>
    public sealed record LiveDecision(
        string Street,
        string Kind,
        string Action,
        string Reason,
        IReadOnlyList<string> Details)
    {
        /// <summary>El alumno tenia una apuesta delante (para emparejarla con la decision calificada).</summary>
        public bool FacingBet { get; init; }

        /// <summary>
        /// De donde sale: "GTO" (equilibrio: all-in con pocas ciegas, o turn y river entre dos),
        /// "Chart" (tablas preflop de APH) o "Exploit" (mejor jugada contra el rango estimado).
        /// Encima de la mesa solo se muestra lo GTO.
        /// </summary>
        public string Source { get; init; } = "Exploit";

        public bool IsGto => Source == "GTO";

        /// <summary>
        /// Fuga del rival que se esta explotando ("pagador · 80 manos"): con un rival conocido el
        /// consejo deja el GTO y sale del solver contra su rango y su forma de jugar.
        /// </summary>
        public string? Exploit { get; init; }

        /// <summary>Como se explota a ese rival, en una frase (se muestra bajo el motivo).</summary>
        public string? ExploitPlan { get; init; }

        /// <summary>Nombre del rival que se explota: su capsula del HUD parpadea.</summary>
        public string? ExploitTarget { get; init; }

        /// <summary>
        /// Lo que se tuvo que corregir o adivinar al leer la mesa en este turno (pila, bote, importe a
        /// pagar, posicion...): el cuadro lo marca en amarillo para que se desconfie del consejo.
        /// </summary>
        public string? ReadWarning { get; init; }

        /// <summary>
        /// Aviso de farol: el rival se deja farolear y la mesa no tiene "Farol" activado (o lo tiene y
        /// el consejo ya cuenta con ello). Se muestra en el cuadro de la mesa.
        /// </summary>
        public string? BluffNote { get; init; }

        /// <summary>Lo que se leyo en pantalla (cartas del heroe, board y posicion), para compararlo con el historial.</summary>
        public string? ReadHero { get; init; }
        public string? ReadBoard { get; init; }
        public string? ReadPosition { get; init; }

        /// <summary>"fold", "check", "call" o "raise": el color del cartel.</summary>
        public string Tone => Kind switch
        {
            "Fold" => "fold",
            "Check" => "check",
            "Call" => "call",
            _ => "raise"
        };
    }

    /// <summary>
    /// Lo que se va sabiendo de la mano en curso a partir de las placas: lo que ha puesto cada
    /// jugador en cada calle (su stack al empezarla menos el de ahora), las subidas preflop en orden,
    /// la ultima accion de cada placa en cada calle y quien fue all-in. Con eso la decision en vivo
    /// reconstruye el spot como lo hace la correccion con el historial.
    /// </summary>
    public sealed class LiveHandContext
    {
        private readonly Dictionary<string, Dictionary<int, double>> _startStacks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<int, double>> _committed = new(StringComparer.Ordinal);
        private readonly Dictionary<int, double> _lastStacks = new();
        private readonly Dictionary<int, string> _preflopLabels = new();
        private double _preflopBet = 1;
        private double _preflopFullRaise = 1;
        private int? _preflopAggressor;

        public string HeroKey { get; init; } = "";

        /// <summary>La mesa tiene "Farol" activado (Tiempo real): ver <see cref="TableBluffModes"/>.</summary>
        public bool BluffFolders { get; set; }

        /// <summary>Posicion del heroe y de cada placa en esta mano, una vez sabidas (se mantienen en todas las calles).</summary>
        public string? HeroPosition { get; set; }
        public IReadOnlyDictionary<int, string>? PlatePositions { get; set; }

        /// <summary>Linea preflop de cada placa; lo agresivo manda (quien subio sigue con rango de subida).</summary>
        public Dictionary<int, string> PreflopActions { get; } = new();

        /// <summary>Linea de cada placa en cada calle postflop ("FLOP", "TURN", "RIVER").</summary>
        public Dictionary<string, Dictionary<int, string>> StreetActions { get; } = new(StringComparer.Ordinal);

        /// <summary>Placas que han ido all-in en la mano.</summary>
        public HashSet<int> AllIn { get; } = new();

        /// <summary>Subidas completas preflop en orden (la placa de cada una; el heroe es la 0).</summary>
        public List<int> PreflopRaises { get; } = new();

        /// <param name="positions">Posicion de cada placa: preflop suma la ciega a lo puesto.</param>
        public void Merge(string street, IReadOnlyList<LivePlate> plates, IReadOnlyDictionary<int, string>? positions = null)
        {
            var preflop = street == "PREFLOP";
            if (!_startStacks.TryGetValue(street, out var starts))
            {
                _startStacks[street] = starts = new Dictionary<int, double>();
                _committed[street] = new Dictionary<int, double>();
            }

            var committed = _committed[street];
            var actions = preflop ? PreflopActions : ActionsOf(street);
            foreach (var original in plates)
            {
                var plate = original;
                if (plate.EmptySeat)
                    continue;

                // Coma perdida en la pila ("116,2" leido "1162"): las pilas no crecen dentro de la mano,
                // asi que una lectura 10 o 100 veces la ultima es un error y no se usa.
                if (plate.StackBb is { } read && _lastStacks.TryGetValue(plate.Index, out var lastRead) && CommaLoss(read, lastRead))
                    plate = plate with { StackBb = null };

                // Stack al empezar la calle: el de ahora si aun no ha actuado en ella; si ya actuo, el
                // ultimo visto antes (las fichas no se mueven entre calles).
                var acted = plate.Action is not null and not "Post";
                if (!starts.ContainsKey(plate.Index))
                {
                    if (!acted && plate.StackBb is { } fresh)
                        starts[plate.Index] = fresh;
                    else if (_lastStacks.TryGetValue(plate.Index, out var previous))
                        starts[plate.Index] = previous;
                    else if (plate.StackBb is { } current)
                        starts[plate.Index] = current;
                }

                var now = plate.StackBb ?? (plate.Action == "AllIn" ? 0 : (double?)null);
                if (now is { } stackNow)
                {
                    _lastStacks[plate.Index] = stackNow;
                    // Y si la mal leida fue la del principio de la calle, se corrige con la de ahora.
                    if (starts.TryGetValue(plate.Index, out var misread) && CommaLoss(misread, stackNow))
                        starts[plate.Index] = stackNow;
                    if (starts.TryGetValue(plate.Index, out var start))
                    {
                        var blind = preflop ? BlindOf(positions, plate.Index) : 0;
                        var amount = blind + Math.Max(0, start - stackNow);
                        if (amount > committed.GetValueOrDefault(plate.Index))
                            committed[plate.Index] = amount;
                    }
                }

                if (plate.Action is null or "Post")
                    continue;
                if (plate.Action == "AllIn")
                    AllIn.Add(plate.Index);
                if (preflop)
                    _preflopLabels[plate.Index] = plate.Action;

                if (actions.TryGetValue(plate.Index, out var line) && line is "Raise" or "AllIn" or "Bet" &&
                    plate.Action is "Call" or "Check")
                    continue;
                actions[plate.Index] = plate.Action;
            }

            if (preflop)
                TrackPreflopRaises(plates, committed);
        }

        /// <summary>Si <paramref name="big"/> es <paramref name="small"/> con la coma perdida (x10 o x100).</summary>
        public static bool CommaLoss(double big, double small) =>
            small > 0.5 && big > small * 5 &&
            (Math.Abs(big - small * 10) <= 0.15 || Math.Abs(big - small * 100) <= 1.5);

        /// <summary>Ultima pila leida de una placa en esta mano (bb), si hay.</summary>
        public double? LastStack(int plate) => _lastStacks.TryGetValue(plate, out var stack) ? stack : null;

        /// <summary>Lo que ha puesto una placa en la calle (con la ciega preflop), si se sabe.</summary>
        public double? Committed(string street, int plate) =>
            _committed.TryGetValue(street, out var map) && map.TryGetValue(plate, out var value) ? value : null;

        /// <summary>La apuesta mas alta de la calle, si se sabe.</summary>
        public double? CurrentBet(string street) =>
            _committed.TryGetValue(street, out var map) && map.Count > 0 ? map.Values.Max() : null;

        /// <summary>Quien hizo la ultima apuesta o subida de la calle (la mas alta), si se sabe.</summary>
        public int? Aggressor(string street)
        {
            if (street == "PREFLOP")
                return _preflopAggressor;
            if (!_committed.TryGetValue(street, out var map) || map.Count == 0)
                return null;
            var top = map.OrderByDescending(pair => pair.Value).First();
            return top.Value > 0.01 ? top.Key : null;
        }

        /// <summary>
        /// Bote al que opta el heroe: lo que cada rival ha puesto por encima de lo que el heroe puede
        /// igualar (<paramref name="heroCap"/>) se le devolvera y no cuenta. Null sin cifras de rivales.
        /// </summary>
        /// <summary>Ultimo bote leido en cada calle en un turno del heroe (para descartar lecturas imposibles).</summary>
        public Dictionary<string, double> PotSeen { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Fichas que habia en la mesa al empezar la mano (la suma de los stacks iniciales, mas las
        /// ciegas): ningun bote puede ser mayor. Solo si se conoce el stack de todos los sentados (si
        /// faltara alguno, la suma se quedaria corta y "corregiria" un bote bueno). Null si no.
        /// </summary>
        public double? TableChips(IReadOnlyList<LivePlate> plates)
        {
            if (!_startStacks.TryGetValue("PREFLOP", out var starts))
                return null;
            var seated = plates.Where(plate => !plate.EmptySeat && !plate.SittingOut).Select(plate => plate.Index).ToList();
            if (seated.Count < 2 || seated.Any(index => !starts.ContainsKey(index)))
                return null;
            return seated.Sum(index => starts[index]) + 1.5;
        }

        public double? EffectivePot(string street, double pot, double heroCap)
        {
            if (!_committed.TryGetValue(street, out var map) || map.Keys.All(plate => plate == 0))
                return null;
            var excess = map.Where(pair => pair.Key != 0).Sum(pair => Math.Max(0, pair.Value - heroCap));
            return Math.Max(0, pot - excess);
        }

        /// <summary>Ultima accion preflop vista en la placa (sin la regla de "lo agresivo manda").</summary>
        public string? PreflopLabel(int plate) => _preflopLabels.GetValueOrDefault(plate);

        /// <summary>El heroe hizo la ultima subida preflop (es el agresor para la c-bet).</summary>
        public bool HeroIsPreflopAggressor =>
            PreflopRaises.Count > 0 ? PreflopRaises[^1] == 0 : PreflopActions.GetValueOrDefault(0) is "Raise" or "AllIn";

        /// <summary>Lo que hizo una placa en una calle, si se llego a ver.</summary>
        public string? ActionOn(string street, int plate)
        {
            var actions = street == "PREFLOP" ? PreflopActions : StreetActions.GetValueOrDefault(street);
            return actions is not null && actions.TryGetValue(plate, out var action) ? action : null;
        }

        private void TrackPreflopRaises(IReadOnlyList<LivePlate> plates, Dictionary<int, double> committed)
        {
            // Cada subida pone mas que lo maximo visto; si hubo varias entre dos lecturas, de menor a
            // mayor (la ultima es la mas grande). Solo cuenta si la placa dice que subio: una cifra
            // mal leida no se convierte en una subida.
            var raisers = plates.Where(plate => plate.Action is "Raise" or "AllIn").Select(plate => plate.Index).ToHashSet();
            foreach (var (plate, amount) in committed.Where(pair => raisers.Contains(pair.Key)).OrderBy(pair => pair.Value))
            {
                if (amount <= _preflopBet + 0.01)
                    continue;

                // Un all-in que no llega a subida completa no reabre la accion ni cuenta como subida.
                if (amount - _preflopBet + 0.01 >= _preflopFullRaise)
                {
                    _preflopFullRaise = amount - _preflopBet;
                    PreflopRaises.Add(plate);
                }

                _preflopBet = amount;
                _preflopAggressor = plate;
            }

            // Una placa que dice "Subir" sin cifras legibles cuenta al menos una subida (sin orden fiable).
            foreach (var plate in raisers)
            {
                if (!committed.ContainsKey(plate) && !PreflopRaises.Contains(plate))
                    PreflopRaises.Add(plate);
            }
        }

        private static double BlindOf(IReadOnlyDictionary<int, string>? positions, int plate) =>
            positions is not null && positions.TryGetValue(plate, out var position)
                ? position switch { "SB" => 0.5, "BB" => 1.0, _ => 0.0 }
                : 0.0;

        private Dictionary<int, string> ActionsOf(string street)
        {
            if (!StreetActions.TryGetValue(street, out var actions))
                StreetActions[street] = actions = new Dictionary<int, string>();
            return actions;
        }
    }

    /// <summary>
    /// Decision en vivo: con las cartas, la posicion, los botones (cuanto hay que pagar), el bote y
    /// las placas (quien apuesta, quien sigue) recomienda que hacer y por que. Usa las mismas reglas
    /// que la correccion al terminar la mano (TrainerDecisionRules, tablas APH y rangos preflop).
    /// </summary>
    public sealed class TrainerLiveDecisionAdvisor
    {
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
        private static readonly double[] SurvivedStreetWeights = { 0.35, 0.8, 1.0, 1.0 };
        private readonly AphPreflopProvider _charts;
        private readonly TrainerRangeEstimator _ranges;

        public TrainerLiveDecisionAdvisor(AphPreflopProvider charts)
        {
            _charts = charts;
            _ranges = new TrainerRangeEstimator(charts);
        }

        public LiveDecision Decide(
            IReadOnlyList<int> hero,
            IReadOnlyList<int> board,
            string? heroPosition,
            LiveTurnState turn,
            LiveHandContext context,
            IReadOnlyDictionary<int, string> platePositions,
            IReadOnlyDictionary<int, string> plateNames,
            Func<string, TrainerVillainProfile?> profiles)
        {
            var position = string.IsNullOrWhiteSpace(heroPosition) ? "?" : heroPosition!;
            var street = board.Count switch { >= 5 => "RIVER", 4 => "TURN", 3 => "FLOP", _ => "PREFLOP" };
            var potNote = (string?)null;
            if (turn.PotBb is { } pot)
            {
                // Un bote imposible es una cifra mal leida (el OCR se come la coma: 87,9 -> 879). Dos
                // limites: lo que ya habia en esta calle mas la apuesta actual por cada rival que sigue,
                // y todas las fichas de la mesa al empezar la mano.
                double? limit = null;
                if (context.PotSeen.TryGetValue(street, out var before))
                {
                    // La apuesta mas alta de la calle: la de las placas (un all-in leido por la pila) o lo
                    // puesto mas lo que cuesta pagar (el boton puede perder una cifra: 13,7 leido 3,7; con
                    // eso el bote real de 164 se "corregia" a 16,4).
                    var currentBet = Math.Max(context.CurrentBet(street) ?? 0, (context.Committed(street, 0) ?? 0) + (turn.ToCallBb ?? 0));
                    var stillIn = turn.Plates.Count(plate => plate.Index != 0 && plate.HasCards);
                    limit = before + Math.Max(currentBet, 1) * Math.Max(stillIn, 1) + (context.Committed(street, 0) ?? 0) + 1;
                }
                if (context.TableChips(turn.Plates) is { } chips)
                    limit = Math.Min(limit ?? double.MaxValue, chips * 1.05);

                // Tercer limite, al empezar una calle (aun sin bote visto en ella): el bote de la calle
                // anterior mas, por cada jugador que sigue con cartas (y uno de margen, por quien pago y se
                // retiro despues), la apuesta mas alta de esa calle y la de esta.
                // QJ en el river de Gabriella V: bote leido 428,5 con unas 134 reales; el precio salia
                // 8% en vez de 22% y se pago el all-in de un full.
                double? lowest = null;
                if (!context.PotSeen.ContainsKey(street) && PreviousStreet(street) is { } previous &&
                    context.PotSeen.TryGetValue(previous, out var previousPot))
                {
                    var seated = Math.Max(2, turn.Plates.Count(plate => plate.HasCards) + 1);
                    var previousBet = context.CurrentBet(previous) ?? 0;
                    var betNow = (context.Committed(street, 0) ?? 0) + (turn.ToCallBb ?? 0);
                    limit = Math.Min(limit ?? double.MaxValue, previousPot + seated * (previousBet + betNow) + 1);
                    lowest = previousPot + (turn.ToCallBb ?? 0);
                }

                if (limit is { } max && pot > max && pot / 10 <= max && pot / 10 >= Math.Max(turn.ToCallBb ?? 0, (lowest ?? 0) - 0.5))
                {
                    potNote = $"Bote leído {Bb(pot)}: imposible (como mucho {Bb(max)}); se toma {Bb(pot / 10)}, el lector se comió la coma.";
                    turn = new LiveTurnState(turn.Options, pot / 10, turn.Plates, turn.ButtonCount);
                }
                else if (limit is { } most && pot > most && lowest is { } least && least <= most)
                {
                    // Ni con la coma cuadra: se toma lo minimo seguro (el bote de antes mas lo que hay que
                    // pagar). Asi el precio sale exigente, nunca regalado.
                    potNote = $"Bote leído {Bb(pot)}: imposible (como mucho {Bb(most)}); se toma lo mínimo seguro, {Bb(least)}.";
                    turn = new LiveTurnState(turn.Options, least, turn.Plates, turn.ButtonCount);
                }
            }
            if (turn.PotBb is { } seen)
                context.PotSeen[street] = seen;

            var decision = board.Count < 3
                ? Preflop(hero, position, turn, context, platePositions, plateNames, profiles)
                : Postflop(hero, board, turn, context, platePositions, plateNames, profiles);
            if (potNote is not null)
                decision = decision with { Details = decision.Details.Prepend(potNote).ToList() };
            return decision with { FacingBet = turn.FacingBet };
        }

        private static string? PreviousStreet(string street) => street switch
        {
            "RIVER" => "TURN",
            "TURN" => "FLOP",
            "FLOP" => "PREFLOP",
            _ => null
        };

        // ------------------------------------------------------------------ preflop

        private LiveDecision Preflop(
            IReadOnlyList<int> hero,
            string heroPos,
            LiveTurnState turn,
            LiveHandContext context,
            IReadOnlyDictionary<int, string> positions,
            IReadOnlyDictionary<int, string> names,
            Func<string, TrainerVillainProfile?> profiles)
        {
            var combo = TrainerCards.ComboName(hero[0], hero[1]);
            var top = PreflopRanking.TopPercentOf(combo);
            var opponents = turn.Plates.Where(plate => plate.Index != 0 && !plate.EmptySeat).ToList();
            var raises = context.PreflopRaises;
            var heroBlind = BlindOf(heroPos);
            var heroCommitted = Math.Max(context.Committed("PREFLOP", 0) ?? 0, heroBlind);
            // Lo puesto por el heroe sale del bote: mas alla de su ciega, como mucho lo que hay sobre las
            // ciegas. J9o en el BTN con el bote en 1,5: una pila mal leida al empezar la mano daba 66,6 bb
            // "puestos" y salia "subida a 67,6 bb" (no ir) en un bote sin subir.
            if (turn.PotBb is { } potSoFar && heroCommitted > heroBlind + Math.Max(0, potSoFar - 1.5) + 0.01)
                heroCommitted = heroBlind + Math.Max(0, potSoFar - 1.5);
            var details = new List<string> { $"{combo}: top {top.ToString("0.#", Spanish)}% de las manos | posicion {heroPos} (estimada)" };
            var (toCall, toCallNote) = ToCall(turn, heroCommitted, preflop: true);
            if (toCallNote is not null) details.Add(toCallNote);
            details.Add(Options(turn));

            // Sin placas legibles la subida se deduce de lo que cuesta pagar, y los limpers del bote.
            var facingRaise = toCall is { } owedNow
                ? owedNow + heroCommitted > 1.01
                : raises.Count > 0 && raises[^1] != 0;
            // Del bote solo salen limpers entre quienes hablan antes que el heroe (sin contar las ciegas):
            // un jugador nuevo que pone la ciega grande deja 1 bb de mas en el bote y no ha entrado
            // (Lacrimosa V: 22 en HJ, primero en hablar, "1 limper delante" -> igualar en vez de no ir).
            var potLimpers = PotLimpers(turn, heroPos);
            if (heroPos != "?" && opponents.Where(plate => plate.HasCards).All(plate => positions.ContainsKey(plate.Index)))
                potLimpers = Math.Min(potLimpers, opponents.Count(plate =>
                    positions.TryGetValue(plate.Index, out var pos) && pos is not ("SB" or "BB") &&
                    TrainerHandParser.PreflopOrder(pos) < TrainerHandParser.PreflopOrder(heroPos)));
            var limpers = Math.Max(opponents.Count(plate => plate.Action is "Call" or "Check"), potLimpers);

            // Ciega puesta: pasar es gratis.
            if (turn.CanCheck)
                return FreeOption(combo, top, heroPos, limpers, turn, details);

            // Pocas ciegas y solo la ciega grande por hablar: el equilibrio exacto de all-in o retirarse.
            if (!facingRaise && limpers == 0 && heroPos == "SB" && PushSpot(combo, turn, heroCommitted, opponents, details) is { } push)
                return push;

            if (!facingRaise)
            {
                if (limpers == 0)
                {
                    var code = heroPos == "?" ? null : _charts.GetCode("Open", heroPos, "", combo);
                    details.Add($"Tabla APH Open {heroPos}: {CodeName(code)}");
                    if (code is null)
                    {
                        var (openTop, _) = TrainerDecisionRules.ColdThresholds(0, null);
                        return ByStrength(combo, top, openTop, openTop, toCall, 1, heroCommitted, turn, details, "Posicion desconocida: referencia por fuerza de mano.");
                    }

                    return code == "R"
                        ? Raise("PREFLOP", heroPos == "SB" ? 3 : 2.5, 1, heroCommitted, turn, $"{combo} esta en el rango de apertura desde {heroPos}: abre subiendo.", details)
                        : Fold("PREFLOP", $"{combo} no entra en el rango de apertura desde {heroPos}: no ir.", details);
                }

                var iso = TrainerDecisionRules.IsoPercent(heroPos);
                details.Add($"{limpers} limper(s) delante | iso-raise con el top {iso}%");
                if (top <= iso)
                    return Raise("PREFLOP", 4 + limpers, 1, heroCommitted, turn, $"{combo} es suficientemente fuerte para aislar a {limpers} limper(s).", details);
                if (TrainerDecisionRules.PlayableMultiway(combo, top))
                    return Call("PREFLOP", toCall ?? Math.Max(0, 1 - heroCommitted), turn, $"{combo} se juega barato en un bote multiway: entra pagando sin inflar el bote.", details);
                return Fold("PREFLOP", TrainerDecisionRules.MultiwayFoldReason(combo), details);
            }

            // Quien hizo la ultima subida: por lo puesto (exacto) o, sin cifras, por las placas.
            var aggressorIndex = context.Aggressor("PREFLOP") is { } byChips && byChips != 0
                ? byChips
                : raises.Count > 0 && raises[^1] != 0
                    ? raises[^1]
                    : opponents.Where(plate => plate.Action is "Raise" or "AllIn").Select(plate => (int?)plate.Index).LastOrDefault();
            var aggressor = aggressorIndex is { } index ? turn.Plates.FirstOrDefault(plate => plate.Index == index) : null;
            var aggressorPos = aggressorIndex is { } positionIndex && positions.TryGetValue(positionIndex, out var pos) ? pos : "?";
            var aggressorName = aggressorIndex is { } nameIndex ? names.TryGetValue(nameIndex, out var name) ? name : aggressor?.Name : null;
            var profile = string.IsNullOrWhiteSpace(aggressorName) ? null : profiles(aggressorName!);
            if (profile?.HasSample == true)
                details.Add($"Rival: {profile.Summary}");
            if (aggressorIndex is null)
                details.Add("Placas sin leer: la subida se deduce de lo que cuesta pagar.");

            var aggressorCommitted = aggressorIndex is { } committedIndex ? context.Committed("PREFLOP", committedIndex) : null;
            double? currentBet = toCall is { } owed ? owed + heroCommitted : context.CurrentBet("PREFLOP");
            var stack = turn.HeroStackBb;
            var callAllIn = toCall is { } owedAll && stack is { } behind && behind > 0 && owedAll >= behind - 0.01;
            // Lo puesto por quien sube sale de su stack leido en la placa; un stack mal leido (97 -> 70, la
            // coma o una cifra de mas) lo convertia en una "subida enorme" y AKo en la BB salia "NO IR con
            // ~12%" contra un open a 3 bb. Si pagar no te deja all-in, no puede pasar de lo que cuesta
            // igualar mas lo tuyo (el boton "Igualar" se lee bien): se toma eso.
            if (aggressorCommitted is { } put && toCall is not null && currentBet is { } buttonBet && !callAllIn &&
                put > buttonBet * 1.25 + 0.5)
            {
                details.Add($"Lo puesto por quien sube se leyó {Bb(put)}: imposible, igualar cuesta {Bb(toCall.Value)}; se toma {Bb(buttonBet)}.");
                aggressorCommitted = buttonBet;
            }
            var aggressorAllIn = aggressor?.Action == "AllIn" || aggressorIndex is { } allInIndex && context.AllIn.Contains(allInIndex) ||
                                 turn.CanRaise == false && !callAllIn;
            // Profundidad de la subida: lo que puso el agresor (si te cubre, mas de lo que tu pagas).
            var depth = Math.Max(currentBet ?? 0, aggressorCommitted ?? 0);

            // Subidas en orden como las cuenta la correccion; si la ultima vista es la del heroe y le
            // toca pagar, hubo otra que no se llego a leer.
            var order = raises.ToList();
            if (order.Count == 0 || order[^1] == 0 && aggressorCommitted is null)
                order.Add(aggressorIndex ?? -1);
            var n = order.Count;
            var heroRaised = order.Contains(0);

            var (callersInPot, earlierAllIns) = PlayersInPot(opponents, aggressorIndex, aggressorCommitted, context);
            if (aggressorIndex is null && turn.PotBb is { } potForCallers && currentBet is { } betForCallers && betForCallers > 0)
            {
                // Sin placas: quien ya pago se deduce del bote (lo que sobra tras la subida, lo del
                // heroe y las ciegas de otros, en subidas enteras).
                var extra = potForCallers - heroCommitted - betForCallers - (1.5 - heroBlind);
                if (extra >= betForCallers * 0.8)
                    callersInPot = Math.Max(callersInPot, (int)Math.Round(extra / betForCallers));
            }

            var priceNeeded = toCall is { } price && price > 0 && turn.PotBb is { } potNow ? price / (potNow + price) * 100 : (double?)null;
            // Una subida fuera de los tamanos de las tablas (open a 12 bb o mas...) se juega como un
            // all-in: por precio contra los rangos de quien sube y de quien ya pago.
            var oversized = TrainerDecisionRules.IsOversizedRaise(n, depth);
            if (oversized && !aggressorAllIn)
                details.Add($"Subida a {Bb(depth)}: fuera de los tamaños de las tablas, se decide por precio.");
            var shoveLike = aggressorAllIn || oversized || aggressorIndex is null && depth >= 25;

            // Ciega grande contra el all-in de la ciega pequena, sin nadie mas: equilibrio exacto.
            if (heroPos == "BB" && aggressorPos == "SB" && (aggressorAllIn || callAllIn) && callersInPot == 0 && earlierAllIns == 0 &&
                opponents.Count(plate => plate.HasCards) <= 1 &&
                CallShoveSpot(combo, toCall, depth, heroCommitted, turn, details) is { } callShove)
                return callShove;

            // Spots que decide el precio, como en la correccion: all-in del rival (tambien uno minimo),
            // pagar deja al heroe all-in o el precio es minimo. Sin placas, una subida enorme tambien.
            if (shoveLike || callAllIn || priceNeeded is < TrainerDecisionRules.TinyPricePercent)
                return PreflopByPrice(hero, combo, toCall, currentBet, depth, heroCommitted, n, aggressorAllIn, shoveLike, callAllIn,
                    callersInPot, earlierAllIns, aggressorPos, profile, turn, context, details);

            string? scenario = null;
            var raiseName = "";
            double? raiseTo = null;
            if (n == 1 && order[0] != 0)
            {
                // Con alguien que ya pago la subida: tabla de squeeze (multiway), como la correccion.
                var squeeze = callersInPot > 0 && heroPos != "?" && aggressorPos != "?" && _charts.HasChart("Squeeze", heroPos, aggressorPos);
                scenario = squeeze ? "Squeeze" : "vs Raise";
                raiseName = squeeze ? "squeeze" : "3bet";
                var inPosition = heroPos is not ("SB" or "BB") && aggressorPos != "?" &&
                                 TrainerHandParser.PreflopOrder(heroPos) > TrainerHandParser.PreflopOrder(aggressorPos);
                // x3 en posicion, x4 fuera, +1x por cada call delante (squeeze).
                raiseTo = currentBet * ((inPosition ? 3 : 4) + callersInPot);
                details.Add($"Open de {aggressorPos}" + (currentBet is { } openSize ? $" a {Bb(openSize)}" : "") +
                            (callersInPot > 0 ? $" + {callersInPot} call(s)" : ""));
            }
            else if (n == 2 && order[0] == 0)
            {
                scenario = "vs 3bet";
                raiseName = "4bet";
                raiseTo = currentBet * 2.3;
                details.Add($"Abriste y {aggressorPos} te hizo 3bet" + (currentBet is { } threeBet ? $" a {Bb(threeBet)}" : ""));
            }
            else if (n == 3 && order[1] == 0)
            {
                scenario = "vs 4bet";
                raiseName = "5bet all-in";
                raiseTo = stack is { } all && all > 0 ? all + heroCommitted : currentBet * 2.3;
                details.Add($"Hiciste 3bet y {aggressorPos} metio 4bet" + (currentBet is { } fourBet ? $" a {Bb(fourBet)}" : ""));
            }
            else if (n == 4 && order[2] == 0)
            {
                scenario = "vs 5bet";
                raiseName = "all-in";
                raiseTo = stack is { } all && all > 0 ? all + heroCommitted : currentBet * 2.2;
                details.Add($"Hiciste 4bet y {aggressorPos} metio 5bet");
            }

            var chartCode = scenario is null || heroPos == "?" || aggressorPos == "?" ? null : _charts.GetCode(scenario, heroPos, aggressorPos, combo);
            if (chartCode is null)
            {
                // Un 4bet+ despues de haber subido se decide por precio; si no, por fuerza de mano,
                // con el orden de manos premium cuando hay dos o mas subidas (igual que la correccion).
                if (n >= 3 && heroRaised)
                    return PreflopByPrice(hero, combo, toCall, currentBet, depth, heroCommitted, n, false, false, false,
                        callersInPot, earlierAllIns, aggressorPos, profile, turn, context, details);

                var strength = n >= 2 ? PreflopRanking.StrengthTopPercent(combo) : top;
                var (raiseTop, callTop) = TrainerDecisionRules.ColdThresholds(n, profile);
                details.Add(scenario is null
                    ? $"{n} subidas en la mano | {combo} es top {strength.ToString("0.#", Spanish)}% por fuerza contra rangos fuertes"
                    : $"Sin tabla exacta ({(aggressorPos == "?" ? "posicion del rival desconocida" : scenario)})");
                return ByStrength(combo, strength, raiseTop, callTop, toCall, currentBet, heroCommitted, turn, details,
                    "Referencia por fuerza de mano: " + (n >= 2 ? "frente a varias subidas solo continuan las manos premium." : "subir con lo mejor, pagar con lo jugable."));
            }

            details.Add($"Tabla APH {scenario} {heroPos} vs {aggressorPos}: {CodeName(chartCode)}");
            return chartCode switch
            {
                "R" => Raise("PREFLOP", raiseTo, currentBet, heroCommitted, turn, $"La tabla APH juega {combo} con {raiseName} en {heroPos} contra {aggressorPos}.", details),
                "C" => Call("PREFLOP", toCall, turn, $"La tabla APH paga con {combo} en {heroPos} contra {aggressorPos}{(scenario == "Squeeze" ? " y quien ya pagó" : "")}.", details),
                _ => Fold("PREFLOP", $"{combo} queda fuera del rango para continuar en {heroPos} contra {aggressorPos}.", details)
            };
        }

        /// <summary>
        /// Quien sigue en el bote frente a la ultima subida, como lo cuenta la correccion: los que la
        /// igualaron despues (o fueron all-in por menos detras de ella) y los all-in de antes. Un
        /// "Igualar" de antes de la subida no cuenta: aun tiene que volver a decidir.
        /// </summary>
        private static (int Callers, int EarlierAllIns) PlayersInPot(
            IReadOnlyList<LivePlate> opponents, int? aggressorIndex, double? aggressorCommitted, LiveHandContext context)
        {
            if (aggressorIndex is not { } aggressor)
                return (0, 0);

            var callers = 0;
            var earlier = 0;
            foreach (var plate in opponents.Where(plate => plate.Index != aggressor))
            {
                var put = context.Committed("PREFLOP", plate.Index);
                var allIn = plate.Action == "AllIn" || context.AllIn.Contains(plate.Index);
                if (aggressorCommitted is { } bet && put is { } amount)
                {
                    // Lo puesto se mide por la pila al empezar la calle; si esa se leyo despues de que
                    // abriera (2 bb), queda corto: con "Igualar" en la placa y al menos el 80% de la
                    // subida, tambien pago. A10o en el BTN: SB all-in de 34 bb y el CO pago (32,3 leidos);
                    // se calculo solo contra el all-in (40%) y se dijo pagar.
                    if (amount >= bet - 0.01 || amount >= bet * 0.8 && plate.Action is "Call" or "AllIn")
                        callers++;
                    else if (allIn && context.PreflopRaises.Contains(plate.Index))
                        earlier++;
                    else if (allIn)
                        callers++;
                    continue;
                }

                // Sin cifras: en la primera vuelta, los que actuan despues del agresor son las placas
                // de indice mayor (las placas van en el sentido de las agujas del reloj).
                if (plate.Index > aggressor && plate.Action is "Call" or "AllIn")
                    callers++;
                else if (plate.Index < aggressor && allIn)
                    earlier++;
            }

            return (callers, earlier);
        }

        /// <summary>
        /// Ciega puesta y nada que pagar. Si nadie ha entrado (ciega de jugador nuevo fuera de su
        /// sitio) se sube solo con manos de apertura; con limpers, solo con el rango de iso-raise.
        /// </summary>
        private LiveDecision FreeOption(string combo, double top, string heroPos, int limpers, LiveTurnState turn, List<string> details)
        {
            if (limpers == 0 && heroPos is not ("BB" or "?"))
            {
                var openCode = _charts.GetCode("Open", heroPos, "", combo);
                details.Add($"Ciega puesta y nadie dentro | tabla APH Open {heroPos}: {CodeName(openCode)}");
                return openCode == "R" || openCode is null && top <= TrainerDecisionRules.IsoPercent(heroPos)
                    ? Raise("PREFLOP", 2.5, 1, 1, turn, $"{combo} es mano de apertura desde {heroPos}: sube.", details)
                    : Check("PREFLOP", $"Pasa gratis: la referencia no abre {combo} desde {heroPos}.", details);
            }

            var iso = TrainerDecisionRules.IsoPercent(heroPos);
            if (limpers > 0)
                details.Add($"{limpers} limper(s) | subir con el top {iso}%");
            return top <= iso
                ? Raise("PREFLOP", 4 + limpers, 1, 1, turn, $"Con {combo} y la ciega puesta, sube por valor contra {limpers} limper(s): tienes de las mejores manos.", details)
                : Check("PREFLOP", "Pasa gratis: con una mano media no merece la pena inflar el bote fuera de posicion.", details);
        }

        /// <summary>
        /// Preflop decidido por el precio (mismas reglas que la correccion): equity contra el rango
        /// del que sube o empuja y de quien sigue en el bote, frente a lo que el heroe puede pagar y
        /// el bote al que opta. Sin subida legal nunca se recomienda subir.
        /// </summary>
        private static LiveDecision PreflopByPrice(
            IReadOnlyList<int> hero, string combo, double? toCall, double? currentBet, double depth, double heroCommitted, int raises,
            bool aggressorAllIn, bool shoveLike, bool callAllIn, int callersInPot, int earlierAllIns, string aggressorPos,
            TrainerVillainProfile? profile, LiveTurnState turn, LiveHandContext context, List<string> details)
        {
            // El rango se lee como el de un all-in tambien cuando, sin placas, la subida es enorme.
            var rangeTop = TrainerDecisionRules.PreflopAggressorTop(shoveLike, depth, raises, profile);
            var ranges = new List<IReadOnlyList<WeightedCombo>> { TrainerDecisionRules.PreflopRange(rangeTop) };
            for (var i = 0; i < callersInPot; i++)
                ranges.Add(TrainerDecisionRules.PreflopCallerRange(shoveLike, rangeTop));
            for (var i = 0; i < earlierAllIns; i++)
                ranges.Add(TrainerDecisionRules.PreflopRange(30));
            var equity = EquityCalculator.VsRanges(hero, Array.Empty<int>(), ranges, 3000);
            var others = callersInPot + earlierAllIns;
            details.Add($"Equity contra el rango de {(aggressorAllIn ? "all-in" : shoveLike ? "subida enorme" : "subida")} de {aggressorPos} (top {rangeTop.ToString("0", Spanish)}%)" +
                        (others > 0 ? $" y {others} mas en el bote" : "") + $": {equity.ToString("0", Spanish)}%");

            var stack = turn.HeroStackBb;
            var call = toCall is { } owed && stack is { } behind && behind > 0 ? Math.Min(owed, behind) : toCall;
            double? required = null;
            if (call is { } paid && paid > 0 && turn.PotBb is { } pot)
            {
                var effectivePot = EffectivePot(context, "PREFLOP", pot, toCall!.Value, paid, heroCommitted, stack,
                    onlyAggressor: callersInPot == 0 && earlierAllIns == 0);
                required = paid / (effectivePot + paid) * 100;
                details.Add($"Precio real: pagas {Bb(paid)} para ganar {Bb(effectivePot + paid)}: necesitas {required.Value.ToString("0", Spanish)}%" +
                            (effectivePot + 0.01 < pot ? " (te cubre: lo que no puedes igualar se le devuelve)" : ""));
            }

            var postflopPlay = !aggressorAllIn && !callAllIn;
            // All-in con alguien que pago y aun tiene fichas: si el heroe tampoco queda all-in, sigue
            // habiendo apuestas en el bote lateral (el que pago puede apostar y echarle): la equity no
            // se cobra entera.
            var sidePotPlay = !postflopPlay && callersInPot > 0 && stack is { } heroBehind && call is { } paying && heroBehind > paying + 1;
            var realization = TrainerDecisionRules.Realization(combo, postflopPlay || sidePotPlay);
            var realized = equity * realization;
            if (postflopPlay || sidePotPlay)
                details.Add((sidePotPlay ? "Queda juego en el bote lateral con quien pagó" : "Queda juego postflop") +
                            $": se cuenta ~{realization * 100:0}% de esa equity ({realized.ToString("0", Spanish)}%)");
            var canRaise = postflopPlay && turn.CanRaise != false;
            if (!canRaise)
                details.Add("No queda subida posible: solo pagar o no ir.");

            if (canRaise && TrainerDecisionRules.IsValueRaise(combo, rangeTop))
                return Raise("PREFLOP", currentBet * (raises >= 2 ? 2.3 : 3), currentBet, heroCommitted, turn,
                    $"{combo} es mano para subir por valor contra ese rango (~{equity:0}%).", details);

            var need = required ?? 50;
            if (required is null)
                details.Add("Bote o importe no legibles: se exige un 50% de equity.");
            return realized >= need
                ? Call("PREFLOP", call, turn, $"Paga: necesitas {need:0}% y tienes ~{realized:0}% contra ese rango.", details)
                : Fold("PREFLOP", $"No ir: con ~{realized:0}% no llegas al {need:0}% que pide el precio.", details);
        }

        /// <summary>
        /// Ciega pequena con todos fuera y pila corta (15 bb o menos): ir all-in o retirarse, como el
        /// equilibrio de Nash del solver de all-in. Pila efectiva = la menor entre el heroe y la BB.
        /// </summary>
        private static LiveDecision? PushSpot(string combo, LiveTurnState turn, double heroCommitted, IReadOnlyList<LivePlate> opponents, List<string> details)
        {
            if (turn.HeroStackBb is not { } behind || behind <= 0)
                return null;
            var heroTotal = behind + heroCommitted;
            var stillIn = opponents.Where(plate => plate.HasCards).ToList();
            if (stillIn.Count > 1)
                return null;
            var villainTotal = stillIn.FirstOrDefault()?.StackBb is { } villainBehind && villainBehind > 0 ? villainBehind + 1 : heroTotal;
            var effective = Math.Min(heroTotal, villainTotal);
            if (TrainerSolver.PushAdvice(combo, effective) is not { } advice)
                return null;

            details.Add($"Solver de all-in: {Bb(effective)} efectivas y solo la ciega grande por hablar. Con {combo} el equilibrio va all-in " +
                        $"el {(advice.Frequency * 100).ToString("0", Spanish)}% de las veces ({Signed(advice.GainBb)} frente a retirarse).");
            return (advice.Push
                ? Raise("PREFLOP", heroTotal, 1, heroCommitted, turn,
                    $"Con {Bb(effective)} efectivas, el equilibrio de all-in o retirarse va all-in con {combo}: la ciega grande no puede pagar con suficientes manos.", details)
                : Fold("PREFLOP", $"Con {Bb(effective)} efectivas, el equilibrio no va all-in con {combo}: retirarse solo pierde la ciega pequeña.", details))
                with { Source = "GTO" };
        }

        /// <summary>Ciega grande contra el all-in de la ciega pequena (sin nadie mas), hasta 25 bb: pagar o no segun el equilibrio.</summary>
        private static LiveDecision? CallShoveSpot(string combo, double? toCall, double depth, double heroCommitted, LiveTurnState turn, List<string> details)
        {
            if (turn.HeroStackBb is not { } behind || behind <= 0)
                return null;
            var heroTotal = behind + heroCommitted;
            var effective = Math.Min(heroTotal, depth > 0 ? depth : heroTotal);
            if (TrainerSolver.CallAdvice(combo, effective) is not { } advice)
                return null;

            details.Add($"Solver de all-in: {Bb(effective)} efectivas contra el all-in de la ciega pequeña. Con {combo} el equilibrio paga " +
                        $"el {(advice.Frequency * 100).ToString("0", Spanish)}% de las veces ({Signed(advice.GainBb)} frente a retirarse).");
            return (advice.Call
                ? Call("PREFLOP", toCall, turn, $"El equilibrio de all-in o retirarse paga con {combo} con {Bb(effective)} efectivas.", details)
                : Fold("PREFLOP", $"El equilibrio no paga con {combo} con {Bb(effective)} efectivas: contra su rango de all-in pierde.", details))
                with { Source = "GTO" };
        }

        private static string Signed(double bb) => $"{bb.ToString("+0.##;-0.##;0", Spanish)} bb";

        /// <summary>Las opciones sin las subidas que, cuando pagan, ganan menos de la mitad (en el orden del solver).</summary>
        public static IReadOnlyList<SolverOption> ValueRaisesOnly(IReadOnlyList<SolverOption> options) =>
            options.Where(option => option.Kind != "Raise" || option.EquityWhenCalled is not { } whenCalled || whenCalled >= 0.5).ToList();

        /// <summary>Que lecturas se corrigieron o dedujeron, segun el detalle de la decision.</summary>
        public static IReadOnlyList<string> DoubtfulReads(IEnumerable<string> details)
        {
            var found = new List<string>();
            void Add(string item)
            {
                if (!found.Contains(item))
                    found.Add(item);
            }

            foreach (var line in details)
            {
                if (line.StartsWith("Bote leído", StringComparison.Ordinal) || line.Contains("Bote o importe no legibles") ||
                    line.StartsWith("Importe o bote no legibles", StringComparison.Ordinal))
                    Add("el bote");
                // "Deducido (bote solo con las ciegas)" es seguro; el que sale del "subir a" no.
                if (line.StartsWith("Importe a pagar deducido del", StringComparison.Ordinal) || line.Contains("No se pudo leer cuanto hay que pagar"))
                    Add("lo que hay que pagar");
                if (line.StartsWith("Placas sin leer", StringComparison.Ordinal) || line.StartsWith("Lo puesto por quien sube se leyó", StringComparison.Ordinal))
                    Add("las fichas de los rivales");
            }

            return found;
        }

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Spanish) + text[1..];

        /// <summary>
        /// En una mezcla GTO, otra opcion con al menos 25% de frecuencia, que arriesga menos y gana claramente
        /// mas que la mas frecuente (mas de 0,5 bb o 2% del bote), o null. Con EV desconocido (biblioteca), null.
        /// </summary>
        public static GtoChoice? BetterGtoChoice(IReadOnlyList<GtoChoice> byFrequency, double pot)
        {
            if (byFrequency.Count < 2 || double.IsNaN(byFrequency[0].Ev))
                return null;
            var top = byFrequency[0];
            var margin = Math.Max(0.5, pot * 0.02);
            // Solo hacia lo que arriesga menos (no ir o pasar antes que pagar, pagar antes que subir): A5s
            // con pareja de cincos en un river que cierra el color, "no ir 58% · igualar 42%", se cambiaba a
            // igualar por el EV y pagaba un color. Hacia mas riesgo, manda lo que el equilibrio hace mas.
            static int Risk(GtoChoice choice) => choice.Action.Kind switch
            {
                GtoKind.Fold or GtoKind.Check => 0,
                GtoKind.Call => 1,
                _ => 2
            };
            return byFrequency
                .Skip(1)
                .Where(choice => choice.Frequency >= 0.25 && !double.IsNaN(choice.Ev) && choice.Ev > top.Ev + margin &&
                                 Risk(choice) < Risk(top))
                .OrderByDescending(choice => choice.Ev)
                .FirstOrDefault();
        }

        private static LiveDecision ByStrength(
            string combo, double top, double raiseTop, double callTop, double? toCall, double? currentBet, double heroCommitted,
            LiveTurnState turn, List<string> details, string context)
        {
            details.Add(context);
            if (top <= raiseTop)
                return Raise("PREFLOP", currentBet * 3, currentBet, heroCommitted, turn, $"{combo} (top {top:0.#}%) es de las mejores manos para este spot: sube.", details);
            if (top <= callTop)
                return Call("PREFLOP", toCall, turn, $"{combo} (top {top:0.#}%) es suficiente para pagar en este spot.", details);
            return Fold("PREFLOP", $"{combo} (top {top:0.#}%) no alcanza para continuar en este spot.", details);
        }

        // ------------------------------------------------------------------ postflop

        private LiveDecision Postflop(
            IReadOnlyList<int> hero,
            IReadOnlyList<int> board,
            LiveTurnState turn,
            LiveHandContext context,
            IReadOnlyDictionary<int, string> positions,
            IReadOnlyDictionary<int, string> names,
            Func<string, TrainerVillainProfile?> profiles)
        {
            var street = board.Count switch { 3 => "FLOP", 4 => "TURN", _ => "RIVER" };
            var insight = HandInsightReader.Read(hero, board);
            var details = new List<string> { $"Mano: {insight.Describe()}" };
            // Lo puesto en la calle esta dentro del bote: nunca mas que el bote leido.
            var heroCommitted = Math.Min(context.Committed(street, 0) ?? 0, turn.PotBb ?? double.MaxValue);

            var opponents = turn.Plates.Where(plate => plate.Index != 0 && plate.HasCards).ToList();
            var bettors = opponents.Where(plate => plate.Action is "Bet" or "Raise" or "AllIn").ToList();
            var aggressorIndex = context.Aggressor(street) is { } byChips && byChips != 0
                ? byChips
                : bettors.Select(plate => (int?)plate.Index).LastOrDefault();
            // Un all-in es subida si ya habia apuesta (del heroe o de otro); si no, es una apuesta.
            var raisedPot = heroCommitted > 0.01 || bettors.Count > 1;
            var ranges = new List<IReadOnlyList<WeightedCombo>>();
            var solverOpponents = new List<(SolverOpponent Opponent, int Index)>();
            TrainerVillainProfile? aggressorProfile = null;
            foreach (var opponent in opponents.DefaultIfEmpty(new LivePlate(-1, null, null, null, true, false, false)))
            {
                var name = names.TryGetValue(opponent.Index, out var known) ? known : opponent.Name;
                var profile = string.IsNullOrWhiteSpace(name) ? null : profiles(name!);
                // Pagar el all-in de otro en esta calle es una linea mucho mas fuerte que un call normal.
                var callsAllIn = opponent.Action == "Call" && opponents.Any(other => other.Index != opponent.Index &&
                    (other.Action == "AllIn" || context.AllIn.Contains(other.Index)));
                // Placa sin leer pero con fichas puestas en esta calle: quien abrio la apuesta aposto (o
                // subio) y los demas pagaron. QQ en 9-9-7 contra un donk-bet salia "sube" porque al que
                // aposto se le daba el rango de alguien que solo habia pagado preflop.
                var seen = opponent;
                if (string.IsNullOrWhiteSpace(opponent.Action) && context.Committed(street, opponent.Index) is { } putNow && putNow > 0.01)
                    seen = opponent with { Action = opponent.Index == aggressorIndex ? raisedPot ? "Raise" : "Bet" : "Call" };
                var range = OpponentRange(seen, context, positions, profile, board, street, raisedPot, callsAllIn, multiway: opponents.Count > 1);
                ranges.Add(range.Combos);
                solverOpponents.Add((new SolverOpponent(range.Combos, profile,
                    opponent.Index >= 0 ? context.Committed(street, opponent.Index) ?? 0 : 0), opponent.Index));
                if (opponent.Index == aggressorIndex)
                {
                    aggressorProfile = profile;
                    details.Add($"Rango de quien apuesta: {range.Description}");
                }
                else if (aggressorIndex is null && opponents.Count == 1)
                {
                    details.Add($"Rango estimado del rival: {range.Description}");
                }
            }

            // Rival conocido (40+ manos) con una fuga clara: se le explota con el solver contra su rango
            // y su forma de jugar (su fold a c-bet, si paga o farolea). Sin datos, juego reservado: GTO.
            // Multiway: frente a una apuesta, el que apuesta; sin apuesta, el pagador o maniaco con mas
            // manos (los faroles contra un nit o contra quien se retira mucho no sirven con mas rivales
            // en el bote). El titulo lleva su nombre para saber a cual.
            var leakProfile = opponents.Count == 1 ? solverOpponents[0].Opponent.Profile : MultiwayLeakTarget(solverOpponents, aggressorIndex, turn.FacingBet);
            var leakWho = opponents.Count > 1 && leakProfile is not null ? $"{leakProfile.Name}: " : "";
            var leak = leakProfile?.Leak is { } leakName ? $"{leakWho}{leakName} · {leakProfile.Hands} manos" : null;
            if (leak is not null)
                details.Add($"Rival conocido ({leak}): {leakProfile!.LeakPlan}. " +
                            (opponents.Count == 1 ? "Se le explota en vez de jugar GTO." : "El solver cuenta con su forma de jugar."));
            string? bluffNote = null;
            LiveDecision Mark(LiveDecision decision)
            {
                var marked = leak is null ? decision : decision with { Exploit = leak, ExploitPlan = leakProfile!.LeakPlan, ExploitTarget = leakProfile.Name };
                return bluffNote is null ? marked : marked with { BluffNote = bluffNote };
            }

            // Contra un solo rival: el equilibrio GTO de la calle (con los dos rangos). En el flop, de la
            // biblioteca si es un bote subido estandar ya calculado; si no, se resuelve en vivo.
            // Limite de dinero ficticio para cualquier camino (GTO, solver o reglas): ver PlayMoneyAggression.
            var limit = TrainerDecisionRules.PlayMoneyAggression(hero, board, street, turn.HeroStackBb, turn.PotBb, opponents.Count);
            // Contra un maniaco el plan es "deja que apueste el y no le hagas faroles": sin pareja (solo
            // proyecto) no se apuesta. KJs con proyecto de color en 3-9-A: semi-farol de medio bote, el
            // maniaco fue all-in y pagar ya era obligado por el precio (AQ, -83 bb).
            if (leakProfile?.Leak == "maníaco" && limit.MaxBet > 0 &&
                HandInsightReader.MadeClass(hero[0], hero[1], board) == 0)
                limit = limit with { MaxBet = 0, Why = "Contra un maníaco sin pareja no se apuesta: deja que apueste él" };
            // Frente a una apuesta grande en turn o river con una mano que pierde contra muchas: no ir.
            if (turn.FacingBet && TrainerDecisionRules.NoCallReason(hero, board, street, turn.ToCallBb, turn.PotBb,
                    maniac: leakProfile?.Leak == "maníaco", opponents: opponents.Count) is { } noCall)
                limit = limit with { NoCall = noCall };

            // Rival que se deja farolear (nit o se retira mucho a las c-bets, 40+ manos), mano a mano y
            // sin mano hecha: con "Farol" activado en la mesa se permite apostar hasta medio bote (nunca
            // subir de farol); sin activarlo, solo se avisa en el cuadro. El solver cuenta con lo que se
            // retira ese rival y solo apuesta si gana mas que pasar.
            if (opponents.Count == 1 && leakProfile?.Leak is "nit" or "se retira mucho" &&
                HandInsightReader.MadeClass(hero[0], hero[1], board) <= 1 && limit.MaxBet < 0.5 &&
                !limit.Why.StartsWith(TrainerDecisionRules.PlaysBoardText, StringComparison.Ordinal))
            {
                var who = $"{leakProfile.Name} se deja farolear ({leakProfile.Leak}, {leakProfile.Hands} manos)";
                if (context.BluffFolders)
                {
                    limit = limit with { MaxBet = 0.5, Why = $"Farol activado: {who}; como mucho medio bote" };
                    bluffNote = $"Farol activado en esta mesa: {who}.";
                }
                else
                {
                    bluffNote = $"{who}: activa «Farol» de esta mesa en Tiempo real para aprovecharlo.";
                }
                details.Add(bluffNote);
            }
            if (opponents.Count == 1 && leak is null &&
                GtoDecision(hero, board, street, turn, context, positions, names, profiles, opponents[0], details, limit) is { } gto)
                return gto;

            var multiway = ranges.Count > 1;
            var equity = EquityCalculator.VsRanges(hero, board, ranges, 2500);
            details.Add($"Equity estimada: {equity.ToString("0", Spanish)}%" + (multiway ? $" contra {ranges.Count} rivales a la vez" : ""));
            if (aggressorProfile?.HasSample == true)
                details.Add($"Rival: {aggressorProfile.Summary}");
            if (insight.Outs > 0 && street != "RIVER")
                details.Add(street == "FLOP"
                    ? $"Outs: {insight.Outs} (~{Math.Min(insight.Outs * 4, 100)}% hasta el river)"
                    : $"Outs: {insight.Outs} (~{insight.Outs * 2}% en el river)");
            details.Add(Options(turn));

            if (turn.FacingBet)
            {
                var (toCall, toCallNote) = ToCall(turn, heroCommitted, preflop: false);
                if (toCallNote is not null) details.Add(toCallNote);
                var stack = turn.HeroStackBb;
                var call = toCall is { } owed && stack is { } behind && behind > 0 ? Math.Min(owed, behind) : toCall;
                double required;
                if (call is { } paid && paid > 0 && turn.PotBb is { } total)
                {
                    var pot = EffectivePot(context, street, total, toCall!.Value, paid, heroCommitted, stack);
                    required = paid / (pot + paid) * 100;
                    details.Add($"Pot odds: pagar {Bb(paid)} en un bote de {Bb(pot)} pide {required.ToString("0", Spanish)}% de equity" +
                                (pot + 0.01 < total ? " (te cubre: lo que no puedes igualar se le devuelve)" : ""));
                }
                else
                {
                    // Sin importes legibles se supone una apuesta de medio bote (25%).
                    required = 25;
                    details.Add("Importe o bote no legibles: se supone una apuesta de medio bote (pide 25%).");
                }

                var callIsAllIn = toCall is { } owedAll && stack is { } stackNow && stackNow > 0 && owedAll >= stackNow - 0.01;
                var someoneCanCall = opponents.Any(plate => plate.Action != "AllIn" && !context.AllIn.Contains(plate.Index));
                var canRaise = turn.CanRaise != false && !callIsAllIn && someoneCanCall;
                if (!canRaise)
                    details.Add("No queda subida posible: solo pagar o no ir.");

                var reference = TrainerDecisionRules.FacingBet(equity, required, insight, street, canRaise);
                double? currentBet = toCall is { } owedBet ? owedBet + heroCommitted : context.CurrentBet(street);

                // Solver de entrenamiento: con el bote y el importe leidos, lo que gana cada opcion.
                if (call is { } callBb && callBb > 0 && turn.PotBb is { } potRead && currentBet is { } bet)
                {
                    var solverPot = EffectivePot(context, street, potRead, toCall!.Value, callBb, heroCommitted, stack);
                    var allIn = (int index) => context.AllIn.Contains(index) ||
                                               opponents.Any(plate => plate.Index == index && plate.Action == "AllIn");
                    var options = TrainerSolver.Postflop(hero, board, WithBet(solverOpponents, aggressorIndex, bet, allIn), solverPot,
                        callBb, heroCommitted, stack, bet, canRaise);
                    if (options.Count > 0)
                        return Mark(FromSolver(street, options, equity, required, insight, solverPot, bet, heroCommitted, turn, details, limit));
                }

                if (reference == "Raise" && !limit.CanRaise)
                {
                    details.Add(limit.Why + ".");
                    reference = "Call";
                }
                if (reference == "Call" && limit.NoCall is { } noCallRule)
                {
                    details.Add(noCallRule + ".");
                    return Mark(Fold(street, noCallRule + ".", details));
                }
                return Mark(reference switch
                {
                    "Raise" => Raise(street, currentBet * 3, currentBet, heroCommitted, turn,
                        $"Con ~{equity:0}% de equity sube por valor: te pagan manos peores y proteges tu mano.", details),
                    "Call" => Call(street, call, turn, equity >= required
                        ? $"Paga: necesitas {required:0}% y tienes ~{equity:0}% contra el rango que apuesta asi."
                        : $"Paga por implied odds: {insight.Outs} outs y el precio esta cerca ({equity:0}% vs {required:0}%).", details),
                    _ => Fold(street, $"No ir: con ~{equity:0}% no llegas al {required:0}% que pide el precio.", details)
                });
            }

            // Solver de entrenamiento: pasar o apostar 1/3, 1/2, 2/3 o bote contra el rango estimado.
            if (turn.PotBb is { } potNoBet && potNoBet > 0)
            {
                var options = TrainerSolver.Postflop(hero, board, solverOpponents.Select(item => item.Opponent).ToList(), potNoBet,
                    0, 0, turn.HeroStackBb, 0, canRaise: true);
                if (options.Count > 0)
                    return Mark(FromSolver(street, options, equity, null, insight, potNoBet, 0, 0, turn, details, limit));
            }

            var initiative = TrainerDecisionRules.WithInitiative(equity, insight, street, context.HeroIsPreflopAggressor, ranges.Count);
            if (initiative == "Check")
                return Mark(Check(street, $"Pasa: con ~{equity:0}% no hay valor para apostar ni un farol claro.", details));

            var fraction = TrainerDecisionRules.BetFraction(initiative);
            if (fraction > limit.MaxBet)
            {
                details.Add(limit.Why + ".");
                if (limit.MaxBet <= 0)
                    return Mark(Check(street, $"Pasa: {limit.Why.Split(':')[0].ToLower(Spanish)}.", details));
                fraction = limit.MaxBet;
            }
            details.Add($"Tamano sugerido: {fraction * 100:0}% del bote" + (turn.PotBb is null ? " (bote no legible)" : ""));
            var reason = initiative switch
            {
                "Bet (valor)" => $"Apuesta por valor: con ~{equity:0}% te pagan manos peores.",
                "Bet fino o check" => $"Apuesta fina por valor (o pasa): con ~{equity:0}% vas por delante, pero no por mucho.",
                "Bet (semi-farol)" => $"Semi-farol: con {insight.Outs} outs puedes ganar ya o ligar despues.",
                _ => $"C-bet como agresor preflop: con ~{equity:0}% y un solo rival, una apuesta pequena gana muchos botes."
            };
            return Mark(Raise(street, turn.PotBb is { } potBb ? Math.Max(1, potBb * fraction) : null, 0, 0, turn, reason, details));
        }

        // ------------------------------------------------------------------ GTO (turn y river, dos jugadores)

        private static readonly string[] PostflopOrder = { "SB", "BB", "LJ", "HJ", "CO", "BTN" };
        private (string Key, GtoSolution Solution, string Note)? _gtoCache;

        /// <summary>Se avisa antes de calcular un equilibrio que tarda (con la calle), para mostrar "calculando".</summary>
        public Action<string>? Thinking { get; set; }

        /// <summary>
        /// Equilibrio GTO de la calle contra un solo rival: los rangos de los dos al empezar la calle
        /// (tablas y lo que hizo cada uno), el bote y la pila efectiva, y las acciones de la calle hasta
        /// el turno del heroe con sus tamanos reales. Tamanos del arbol: 33%, 75% y all-in.
        /// </summary>
        private LiveDecision? GtoDecision(
            IReadOnlyList<int> hero, IReadOnlyList<int> board, string street, LiveTurnState turn, LiveHandContext context,
            IReadOnlyDictionary<int, string> positions, IReadOnlyDictionary<int, string> names,
            Func<string, TrainerVillainProfile?> profiles, LivePlate villain, List<string> details, PlayMoneyLimit limit)
        {
            try
            {
                if (turn.PotBb is not { } pot || turn.HeroStackBb is not { } heroBehind || heroBehind <= 0)
                    return null;
                var heroCommitted = Math.Min(context.Committed(street, 0) ?? 0, pot);
                var (toCall, _) = ToCall(turn, heroCommitted, preflop: false);
                if (turn.FacingBet && toCall is null)
                    return null;
                var currentBet = turn.FacingBet ? toCall!.Value + heroCommitted : heroCommitted;
                var villainCommitted = Math.Max(context.Committed(street, villain.Index) ?? 0, turn.FacingBet ? currentBet : 0);
                var potStart = pot - heroCommitted - villainCommitted;
                if (potStart < 1)
                    return null;
                var villainBehind = villain.StackBb is { } behind && behind > 0 ? behind : heroBehind;
                var stack = Math.Min(heroBehind + heroCommitted, villainBehind + villainCommitted);

                // Quien habla primero en la calle: por posiciones o, si no se saben, por quien ya actuo.
                var heroPos = positions.GetValueOrDefault(0);
                var villainPos = positions.GetValueOrDefault(villain.Index);
                bool heroOop;
                if (heroPos is not null && villainPos is not null && Array.IndexOf(PostflopOrder, heroPos) >= 0 && Array.IndexOf(PostflopOrder, villainPos) >= 0)
                    heroOop = Array.IndexOf(PostflopOrder, heroPos) < Array.IndexOf(PostflopOrder, villainPos);
                else
                    heroOop = !(heroCommitted <= 0.01 && villain.Action is "Check" or "Bet" or "Raise" or "AllIn");

                // Acciones de la calle hasta ahora (con sus tamanos reales).
                var history = new List<GtoAction>();
                GtoAction Act(GtoKind kind, double to) => new(kind, to, to >= stack - 0.01);
                if (heroOop)
                {
                    if (heroCommitted <= 0.01 && villainCommitted > 0.01)
                        history.AddRange(new[] { Act(GtoKind.Check, 0), Act(GtoKind.Bet, villainCommitted) });
                    else if (heroCommitted > 0.01 && villainCommitted > heroCommitted + 0.01)
                        history.AddRange(new[] { Act(GtoKind.Bet, heroCommitted), Act(GtoKind.Raise, villainCommitted) });
                }
                else if (villainCommitted <= 0.01)
                    history.Add(Act(GtoKind.Check, 0));
                else if (heroCommitted <= 0.01)
                    history.Add(Act(GtoKind.Bet, villainCommitted));
                else
                    history.AddRange(new[] { Act(GtoKind.Check, 0), Act(GtoKind.Bet, heroCommitted), Act(GtoKind.Raise, villainCommitted) });

                var villainName = names.TryGetValue(villain.Index, out var known) ? known : villain.Name;
                var villainProfile = string.IsNullOrWhiteSpace(villainName) ? null : profiles(villainName!);
                var heroRange = WithHand(RangeAtStreetStart(0, context, positions, null, board), hero);
                var villainRange = RangeAtStreetStart(villain.Index, context, positions, villainProfile, board);
                var oop = heroOop ? heroRange : villainRange;
                var ip = heroOop ? villainRange : heroRange;

                var key = string.Join("|", TrainerCards.ToText(hero), TrainerCards.ToText(board), potStart.ToString("0.0"), stack.ToString("0.0"),
                    heroOop, string.Join(",", history.Select(a => $"{a.Kind}{a.To:0.0}")));
                GtoSolution solution;
                string note;
                if (_gtoCache is { } cached && cached.Key == key)
                {
                    (solution, note) = (cached.Solution, cached.Note);
                }
                else if (board.Count == 3 && LibrarySpot(context, positions, villain.Index, villainProfile) is { } opener &&
                         potStart is >= 4 and <= 9.5 && stack >= 60 &&
                         FlopLibrary.Lookup(opener, board, hero[0], hero[1], history, potStart, stack) is { } fromLibrary)
                {
                    solution = fromLibrary;
                    note = $"GTO del flop de la biblioteca (bote subido de {opener} contra la ciega grande, 100 bb, rangos de las tablas APH): al instante.";
                    _gtoCache = (key, solution, note);
                }
                else
                {
                    if (board.Count == 3)
                    {
                        // Rangos muy anchos (botes sin subir) tardarian demasiado: ahi decide el solver de entrenamiento.
                        if ((long)oop.Combos.Count * ip.Combos.Count > 450_000)
                            return null;
                        Thinking?.Invoke(street);
                        var flopSolver = new FlopGtoSolver(board, potStart, stack, oop.Combos, ip.Combos, history);
                        if (flopSolver.TargetActor < 0)
                            return null;
                        solution = flopSolver.Solve(hero[0], hero[1], 150, TimeSpan.FromSeconds(5), minIterations: 25);
                        note = $"GTO del flop resuelto en vivo: {solution.Iterations} vueltas en {solution.Elapsed.TotalSeconds.ToString("0.0", Spanish)} s; " +
                               "flop con 33%, 75% y all-in, turn con 75%, el river se reparte sin apuestas (aproximado).";
                    }
                    else
                    {
                        var settings = street == "RIVER"
                            ? new GtoSettings { MaxIterations = 1000, Budget = TimeSpan.FromSeconds(1.5) }
                            : new GtoSettings { MaxIterations = 250, Budget = TimeSpan.FromSeconds(4.5) };
                        Thinking?.Invoke(street);
                        var solver = new PostflopGtoSolver(board, potStart, stack, oop.Combos, ip.Combos, history, settings);
                        solution = solver.Solve(hero[0], hero[1]);
                        note = $"{solution.Iterations} vueltas en {solution.Elapsed.TotalSeconds.ToString("0.0", Spanish)} s; tamaños 33%, 75% y all-in.";
                    }
                    _gtoCache = (key, solution, note);
                }
                if (solution.Actor != (heroOop ? 0 : 1))
                    return null;

                return FromGto(street, solution, TrainerCards.ComboName(hero[0], hero[1]), potStart + heroCommitted + villainCommitted,
                    currentBet, heroCommitted, toCall, turn, heroOop, details, note, limit);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Decision en vivo: no se pudo calcular el GTO", ex);
                return null;
            }
        }

        /// <summary>Rival a explotar en un bote multiway (ver el comentario en Postflop), o null.</summary>
        private static TrainerVillainProfile? MultiwayLeakTarget(
            IReadOnlyList<(SolverOpponent Opponent, int Index)> opponents, int? aggressorIndex, bool facingBet)
        {
            if (facingBet)
                return opponents.FirstOrDefault(item => item.Index == aggressorIndex).Opponent?.Profile is { Leak: not null } bettor ? bettor : null;
            return opponents.Select(item => item.Opponent.Profile)
                .Where(profile => profile?.Leak is "pagador" or "maníaco" or "trampero")
                .OrderByDescending(profile => profile!.Hands)
                .FirstOrDefault();
        }

        /// <summary>
        /// Si la mano es un bote subido estandar de la biblioteca del flop: una sola subida preflop, de
        /// LJ, HJ, CO, BTN o SB, pagada por la ciega grande, y el rival sin rango propio por stats.
        /// Devuelve la posicion de quien abrio.
        /// </summary>
        private static string? LibrarySpot(LiveHandContext context, IReadOnlyDictionary<int, string> positions, int villain,
            TrainerVillainProfile? villainProfile)
        {
            if (context.PreflopRaises.Count != 1 || villainProfile?.UseStatsForRange == true)
                return null;
            var raiser = context.PreflopRaises[0];
            if (raiser != 0 && raiser != villain)
                return null;
            var caller = raiser == 0 ? villain : 0;
            var opener = positions.GetValueOrDefault(raiser);
            return positions.GetValueOrDefault(caller) == "BB" && FlopLibrary.Spots.Any(spot => spot.Opener == opener) ? opener : null;
        }

        /// <summary>Rango de una placa al empezar esta calle: el preflop y lo que hizo en las calles anteriores.</summary>
        private HandRange RangeAtStreetStart(int plate, LiveHandContext context, IReadOnlyDictionary<int, string> positions,
            TrainerVillainProfile? profile, IReadOnlyList<int> board)
        {
            var range = PreflopRangeOf(plate, context, positions, profile);
            for (var cards = 3; cards < board.Count; cards++)
            {
                var past = cards == 3 ? "FLOP" : "TURN";
                var visible = board.Take(cards).ToList();
                var pastLine = LineOf(context.ActionOn(past, plate), raised: true);
                if (pastLine is null)
                {
                    range = range.WithWeights(range.Description, combo => SurvivedStreetWeights[HandInsightReader.StrengthClass(combo.A, combo.B, visible)]);
                    continue;
                }
                range = range.WithWeights($"{range.Description}; {LineName(pastLine)} en el {past.ToLowerInvariant()}",
                    TrainerRangeEstimator.LineWeight(pastLine, past, profile, visible));
            }
            return range;
        }

        /// <summary>La mano real del heroe siempre esta en su rango (si jugo fuera de tabla, con poco peso).</summary>
        private static HandRange WithHand(HandRange range, IReadOnlyList<int> hero)
        {
            var (a, b) = (Math.Min(hero[0], hero[1]), Math.Max(hero[0], hero[1]));
            var max = range.Combos.Count == 0 ? 1 : range.Combos.Max(c => c.Weight);
            if (range.Combos.Any(c => Math.Min(c.A, c.B) == a && Math.Max(c.A, c.B) == b && c.Weight > max * 0.01))
                return range;
            return new HandRange(range.Description, range.Combos
                .Where(c => !(Math.Min(c.A, c.B) == a && Math.Max(c.A, c.B) == b))
                .Append(new WeightedCombo(a, b, max * 0.05)));
        }

        /// <summary>La jugada GTO: la accion mas frecuente del equilibrio, con la mezcla y lo que gana cada una.</summary>
        private static LiveDecision FromGto(string street, GtoSolution solution, string combo, double potNow, double currentBet,
            double heroCommitted, double? toCall, LiveTurnState turn, bool heroOop, List<string> details, string note,
            PlayMoneyLimit? limit = null)
        {
            string Name(GtoAction action) => action.Kind switch
            {
                GtoKind.Fold => "no ir",
                GtoKind.Check => "pasar",
                GtoKind.Call => action.AllIn ? "igualar all-in" : "igualar",
                GtoKind.Bet => action.AllIn ? "all-in" : $"apostar {Bb(action.To - heroCommitted)} ({((action.To - heroCommitted) / Math.Max(0.01, potNow) * 100).ToString("0", Spanish)}% del bote)",
                _ => action.AllIn ? "subir all-in" : $"subir a {Bb(action.To)}"
            };

            var ordered = solution.Choices.OrderByDescending(c => c.Frequency).ToList();
            var best = ordered[0];
            var mix = ordered.Where(c => c.Frequency >= 0.05).Select(c => $"{Name(c.Action)} {(c.Frequency * 100).ToString("0", Spanish)}%").ToList();
            var reason = mix.Count > 1
                ? $"GTO con {combo}: mezcla {string.Join(" · ", mix)}. Haz lo más frecuente o reparte así."
                : $"GTO con {combo}: siempre {Name(best.Action)}.";

            // Mezcla sin terminar de converger (pocas vueltas en el turn): si otra opcion con peso real
            // gana claramente mas, se recomienda esa. ATs en Roxane V: "igualar 48%" perdia 1,36 bb de
            // media y "no ir 46%" ganaba 0.
            if (BetterGtoChoice(ordered, potNow) is { } better && !ReferenceEquals(better, best))
            {
                reason = $"GTO con {combo}: mezcla {string.Join(" · ", mix)}. {Capitalize(Name(better.Action))} gana más " +
                         $"({Signed(better.Ev)} contra {Signed(best.Ev)} de media): {Name(better.Action)}.";
                details.Add($"La opción más frecuente ({Name(best.Action)}) ganaba menos que {Name(better.Action)}: se recomienda la que más gana.");
                best = better;
            }

            // Dinero ficticio: el equilibrio supone un rival que se retira lo justo; aqui pagan casi todo.
            // Si la jugada GTO es una subida, un all-in o una apuesta que el limite no deja (farol, mesa
            // de escalera o color), se toma la permitida que mas gana de media (o la mas frecuente).
            bool Fits(GtoChoice choice) => limit is null || choice.Action.Kind switch
            {
                GtoKind.Call => limit.NoCall is null,
                GtoKind.Raise => limit.Allows(true, choice.Action.AllIn || BecomesAllIn(choice.Action.To, heroCommitted, turn),
                    (choice.Action.To - heroCommitted) / Math.Max(0.01, potNow)),
                GtoKind.Bet => limit.Allows(false, choice.Action.AllIn || BecomesAllIn(choice.Action.To, heroCommitted, turn),
                    (choice.Action.To - heroCommitted) / Math.Max(0.01, potNow)),
                _ => true
            };
            if (!Fits(best))
            {
                var allowed = ordered.Where(Fits).ToList();
                var fallback = allowed.Count > 0 && allowed.All(c => !double.IsNaN(c.Ev))
                    ? allowed.OrderByDescending(c => c.Ev).First()
                    : allowed.FirstOrDefault();
                if (fallback is not null)
                {
                    if (best.Action.Kind == GtoKind.Call && limit!.NoCall is { } noCallText)
                    {
                        reason = $"{noCallText}. El GTO prefería {Name(best.Action)}.";
                        details.Add($"{noCallText}. El GTO prefería {Name(best.Action)}.");
                    }
                    else
                    {
                        reason = $"{limit!.Why.Split(':')[0]}: {Name(fallback.Action)}" +
                                 (double.IsNaN(fallback.Ev) ? "." : $" (gana {Signed(fallback.Ev)} de media).") +
                                 $" El GTO prefería {Name(best.Action)}, pero en dinero ficticio te pagan.";
                        details.Add($"{limit.Why}. El GTO prefería {Name(best.Action)}.");
                    }
                    best = fallback;
                }
            }

            details.Add($"GTO entre dos jugadores ({street.ToLowerInvariant()}, {(heroOop ? "hablas primero" : "hablas segundo")}): {note}");
            foreach (var choice in ordered)
                details.Add(double.IsNaN(choice.Ev)
                    ? $"   {Name(choice.Action)}: {(choice.Frequency * 100).ToString("0", Spanish)}%"
                    : $"   {Name(choice.Action)}: {(choice.Frequency * 100).ToString("0", Spanish)}% · gana {Signed(choice.Ev)} de media");

            var decision = best.Action.Kind switch
            {
                GtoKind.Fold => Fold(street, reason, details),
                GtoKind.Check => Check(street, reason, details),
                GtoKind.Call => Call(street, toCall, turn, reason, details),
                GtoKind.Bet => Raise(street, best.Action.To, 0, heroCommitted, turn, reason, details),
                _ => Raise(street, best.Action.To, currentBet, heroCommitted, turn, reason, details)
            };
            return decision with { Source = "GTO" };
        }

        /// <summary>Quien apuesta ya puso la apuesta aunque su placa no se haya leido.</summary>
        private static IReadOnlyList<SolverOpponent> WithBet(IEnumerable<(SolverOpponent Opponent, int Index)> opponents, int? aggressorIndex, double bet,
            Func<int, bool>? isAllIn = null) =>
            opponents.Select(item =>
                {
                    var opponent = item.Index == aggressorIndex && item.Opponent.Committed < bet - 0.01
                        ? item.Opponent with { Committed = bet }
                        : item.Opponent;
                    return isAllIn?.Invoke(item.Index) == true ? opponent with { AllIn = true } : opponent;
                })
                .ToList();

        /// <summary>
        /// La recomendacion del solver de entrenamiento: la opcion que mas gana de media en esta calle
        /// contra el rango estimado, con las demas en el detalle y un aviso si otra gana casi lo mismo.
        /// </summary>
        private static LiveDecision FromSolver(string street, IReadOnlyList<SolverOption> options, double equity, double? required,
            HandInsight insight, double pot, double currentBet, double heroCommitted, LiveTurnState turn, List<string> details,
            PlayMoneyLimit? limit = null)
        {
            // Dinero ficticio: subir es solo por valor. Si cuando pagan se gana menos de la mitad, no se
            // sube (QQ con full en 9-9-A-8-9: subia a 9 bb ganando el 36% cuando pagan; cualquier as gana).
            var valueOnly = ValueRaisesOnly(options);
            if (valueOnly.Count > 0 && !ReferenceEquals(valueOnly[0], options[0]))
            {
                details.Add($"Subir solo es por valor: {OptionLabel(options[0], pot).ToLower(Spanish)} gana menos de la mitad cuando pagan.");
                options = valueOnly;
            }

            // Dinero ficticio: sin mano fuerte no se sube ni se va all-in, sin pareja no se farolea, y en
            // mesas de color o escalera la apuesta es chica. El solver contra rangos estimados
            // sobrevaloraba doble pareja en Q-K-A de un palo y subia con A-Q sin pareja contra un pagador.
            if (limit?.NoCall is { } noCallReason && options.Any(option => option.Kind == "Fold"))
            {
                var withoutCall = options.Where(option => option.Kind != "Call").ToList();
                if (!ReferenceEquals(withoutCall[0], options[0]))
                    details.Add($"{noCallReason} (el solver prefería {OptionLabel(options[0], pot).ToLower(Spanish)}).");
                options = withoutCall;
            }

            if (limit is { Unlimited: false } cap)
            {
                bool Fits(SolverOption option) => option.Kind switch
                {
                    "Raise" => cap.Allows(true, BecomesAllIn(option.Amount, heroCommitted, turn),
                        (option.Amount - heroCommitted) / Math.Max(0.01, pot)),
                    "Bet" => cap.Allows(false, BecomesAllIn(option.Amount, 0, turn), option.Amount / Math.Max(0.01, pot)),
                    _ => true
                };
                var safe = options.Where(Fits).ToList();
                if (safe.Count > 0 && !ReferenceEquals(safe[0], options[0]))
                {
                    details.Add($"{cap.Why} (el solver prefería {OptionLabel(options[0], pot).ToLower(Spanish)}).");
                    options = safe;
                }
            }

            var best = options[0];
            details.Add("Solver de entrenamiento: lo que gana de media cada opción en esta calle contra el rango estimado");
            // Las tres mejores y, para comparar, siempre pasar o pagar.
            var listed = options.Take(3).ToList();
            listed.AddRange(options.Where(option => option.Kind is "Check" or "Call" && !listed.Contains(option)));
            foreach (var option in listed)
            {
                var extra = option.FoldPct is { } fold
                    ? $" · se retira {(fold * 100).ToString("0", Spanish)}%" +
                      (option.EquityWhenCalled is { } whenCalled ? $" · si paga ganas {(whenCalled * 100).ToString("0", Spanish)}%" : "")
                    : "";
                details.Add($"   {OptionLabel(option, pot)}: {Signed(option.Ev)}{extra}");
            }

            var eq = equity.ToString("0", Spanish);
            var need = required is { } r ? r.ToString("0", Spanish) : "";
            var foldPct = ((best.FoldPct ?? 0) * 100).ToString("0", Spanish);
            var verb = best.Kind == "Bet" ? "Apuesta" : "Sube";
            var callOption = options.FirstOrDefault(option => option.Kind == "Call");
            var drawing = TrainerDecisionRules.HasStrongDraw(insight, street);
            var reason = best.Kind switch
            {
                "Fold" => $"No ir: pagar pierde ~{Signed(-(callOption?.Ev ?? 0)).TrimStart('+')} de media. Tienes ~{eq}% y el precio pide {need}%.",
                "Call" => $"Paga: gana {Signed(best.Ev)} de media. Tienes ~{eq}% y el precio pide {need}%.",
                "Check" => $"Pasa: contra este rango apostar no gana más que pasar (tienes ~{eq}%).",
                _ when drawing && best.EquityWhenCalled is < 0.6 =>
                    $"{verb} como semi-farol: el rival se retira ~{foldPct}% y, cuando paga, tu proyecto aún gana ~{((best.EquityWhenCalled ?? 0) * 100).ToString("0", Spanish)}%.",
                _ when best.EquityWhenCalled is >= 0.55 =>
                    $"{verb} por valor: el rival se retira ~{foldPct}% y, cuando paga, aún ganas ~{(best.EquityWhenCalled.Value * 100).ToString("0", Spanish)}%.",
                _ when best.FoldPct is >= 0.35 =>
                    $"{verb}: el rival se retira ~{foldPct}% de las veces y te llevas el bote; gana {Signed(best.Ev)} de media.",
                _ => $"{verb}: gana {Signed(best.Ev)} de media (se retira {foldPct}% y, cuando paga, aún tienes opciones)."
            };

            var second = options.Skip(1).FirstOrDefault();
            if (second is not null && best.Ev - second.Ev <= Math.Max(0.2, 0.03 * pot))
                reason += $" Casi igual: {OptionLabel(second, pot).ToLower(Spanish)}.";

            return best.Kind switch
            {
                "Fold" => Fold(street, reason, details),
                "Check" => Check(street, reason, details),
                "Call" => Call(street, best.Amount, turn, reason, details),
                "Bet" => Raise(street, best.Amount, 0, 0, turn, reason, details),
                _ => Raise(street, best.Amount, currentBet, heroCommitted, turn, reason, details)
            };
        }

        private static string OptionLabel(SolverOption option, double pot) => option.Kind switch
        {
            "Fold" => "No ir",
            "Check" => "Pasar",
            "Call" => $"Igualar {Bb(option.Amount)}",
            "Bet" => $"Apostar {Bb(option.Amount)} ({(option.Amount / Math.Max(0.01, pot) * 100).ToString("0", Spanish)}% del bote)",
            _ => $"Subir a {Bb(option.Amount)}"
        };

        private HandRange OpponentRange(
            LivePlate opponent, LiveHandContext context, IReadOnlyDictionary<int, string> positions,
            TrainerVillainProfile? profile, IReadOnlyList<int> board, string street, bool raisedPot, bool callsAllIn = false,
            bool multiway = false)
        {
            var range = PreflopRangeOf(opponent.Index, context, positions, profile);

            // Calles anteriores: lo que hizo en cada una si se vio en su placa; si no, solo que siguio
            // en la mano (menos aire).
            for (var cards = 3; cards < board.Count; cards++)
            {
                var past = cards == 3 ? "FLOP" : "TURN";
                var visible = board.Take(cards).ToList();
                var pastLine = LineOf(context.ActionOn(past, opponent.Index), raised: true);
                if (pastLine is null)
                {
                    range = range.WithWeights(range.Description, combo => SurvivedStreetWeights[HandInsightReader.StrengthClass(combo.A, combo.B, visible)]);
                    continue;
                }

                range = range.WithWeights($"{range.Description}; {LineName(pastLine)} en el {past.ToLowerInvariant()}",
                    TrainerRangeEstimator.LineWeight(pastLine, past, profile, visible, multiway));
            }

            // En esta calle, segun lo que hizo antes del turno del heroe.
            var line = callsAllIn ? "callallin" : LineOf(opponent.Action, raisedPot);
            if (line is not null)
            {
                range = range.WithWeights($"{range.Description}; {LineName(line)} en esta calle",
                    TrainerRangeEstimator.LineWeight(line, street, profile, board, multiway));
            }

            return range;
        }

        /// <summary>
        /// Rango preflop del rival con la misma funcion que la correccion: su ultima accion preflop
        /// (subir, pagar o pasar), cuantas subidas habia antes y quien las hizo.
        /// </summary>
        private HandRange PreflopRangeOf(int plate, LiveHandContext context, IReadOnlyDictionary<int, string> positions, TrainerVillainProfile? profile)
        {
            if (plate < 0)
                return HandRange.FromClasses("rango tipico que sigue en la mano", PreflopRanking.TopPercent(45));

            string PosOf(int index) => positions.TryGetValue(index, out var position) ? position : "-";
            var villainPos = PosOf(plate);
            var raises = context.PreflopRaises;
            var label = context.PreflopLabel(plate);
            var lastRaise = raises.LastIndexOf(plate);

            TrainerActionKind kind;
            int raisesBefore;
            if (label == "Raise" || label == "AllIn" && lastRaise >= 0)
            {
                kind = TrainerActionKind.Raise;
                raisesBefore = Math.Max(0, lastRaise);
            }
            else if (label is "Call" or "AllIn" || raises.Count > 0)
            {
                // Sigue en la mano despues de las subidas: las igualo (su ultima accion era pagar o no
                // se llego a ver).
                kind = TrainerActionKind.Call;
                raisesBefore = raises.Count;
            }
            else if (label == "Check" || villainPos == "BB")
            {
                kind = TrainerActionKind.Check;
                raisesBefore = 0;
            }
            else
            {
                kind = TrainerActionKind.Call;
                raisesBefore = 0;
            }

            var first = raises.Count > 0 ? PosOf(raises[0]) : "-";
            var previous = raises.Count > 0 ? PosOf(raises[Math.Clamp(raisesBefore, 1, raises.Count) - 1]) : "-";
            return _ranges.PreflopRangeFor(villainPos, kind, raisesBefore, first, previous, profile);
        }

        private static string? LineOf(string? action, bool raised) => action switch
        {
            "Bet" => "bet",
            "Raise" => "raise",
            "AllIn" => raised ? "raise" : "bet",
            "Call" => "call",
            "Check" => "check",
            _ => null
        };

        private static string LineName(string line) => line switch
        {
            "bet" => "aposto",
            "raise" => "subio",
            "call" => "pago",
            "callallin" => "pago un all-in",
            _ => "paso"
        };

        // ------------------------------------------------------------------ comun

        /// <summary>
        /// Bote al que opta el heroe: sin la parte de cada apuesta rival que el heroe no puede igualar
        /// (se le devuelve). Con lo puesto por cada placa es exacto; sin cifras, solo se descuenta lo
        /// que el boton de igualar pide de mas sobre el stack del heroe.
        /// </summary>
        /// <param name="onlyAggressor">
        /// Solo el agresor y el heroe en el bote: si el agresor cubre al heroe, lo que puso se deduce del
        /// bote (menos lo del heroe y ~0,5 bb de ciega muerta) y su exceso no cuenta. El boton de
        /// "igualar all-in" ya dice solo lo que el heroe puede pagar, asi que sin esto el exceso entraba
        /// en el bote (AKs contra un all-in de 130 bb: pedia 39% en vez de 48%).
        /// </param>
        private static double EffectivePot(LiveHandContext context, string street, double pot, double toCall, double paid,
            double heroCommitted, double? stack, bool onlyAggressor = false)
        {
            var heroCap = heroCommitted + (stack is { } behind && behind > 0 ? behind : double.PositiveInfinity);
            var estimate = Math.Max(0, pot - Math.Max(0, toCall - paid));
            if (onlyAggressor && stack is { } left && left > 0 && paid >= left - 0.01)
            {
                var aggressorPut = pot - heroCommitted - 0.5;
                estimate = Math.Min(estimate, pot - Math.Max(0, aggressorPut - heroCap));
            }

            // Lo que puso quien apuesta, hasta donde el heroe puede igualar, ya es al menos lo que el
            // heroe paga ahora: un bote menor es una cifra mal leida (el OCR se come la coma de un
            // stack, 117,5 -> 1175, y el bote salia 0: "con AA no llegas al 100%").
            var floor = heroCommitted + paid;
            var exact = context.EffectivePot(street, pot, heroCap);
            var result = exact is { } value && value >= floor - 0.01 ? value : estimate;
            return Math.Max(result, Math.Min(floor, pot));
        }

        /// <summary>
        /// Lo que cuesta pagar. Si el OCR no leyo la cifra se deduce: preflop sin subidas (bote de
        /// 1,5 bb) completar cuesta 1 bb menos lo ya puesto; con el "subir a" leido (el slider empieza
        /// en el minimo), la apuesta actual es la mitad del minimo (mas la ciega grande preflop).
        /// </summary>
        private static (double? Amount, string? Note) ToCall(LiveTurnState turn, double heroCommitted, bool preflop)
        {
            if (turn.ToCallBb is { } read)
                return (read, null);
            if (!turn.FacingBet)
                return (null, null);

            if (preflop && turn.PotBb is <= 1.55)
                return (Math.Max(0, 1 - heroCommitted), "Importe a pagar deducido (bote solo con las ciegas).");

            if (turn.Options.FirstOrDefault(option => option.Kind == LiveActionKind.Raise)?.AmountBb is { } minRaise)
            {
                // Preflop sin subidas el minimo es 2 bb y la apuesta actual la ciega grande.
                var currentBet = preflop ? minRaise <= 2.01 ? 1 : (minRaise + 1) / 2 : minRaise / 2;
                return (Math.Max(0, currentBet - heroCommitted), "Importe a pagar deducido del \"subir a\" (si el slider estaba en el minimo).");
            }

            return (null, "No se pudo leer cuanto hay que pagar.");
        }

        /// <summary>
        /// Limpers deducidos del bote preflop sin subidas: ciegas (1,5 bb) mas 1 bb por cada uno.
        /// Desde la BB cuenta tambien la SB que completa (0,5 bb); desde otro sitio, medio bb de mas
        /// es una ciega muerta. Si el heroe puso ciega fuera de su sitio, esa tampoco es un limper.
        /// </summary>
        private static int PotLimpers(LiveTurnState turn, string heroPos)
        {
            if (turn.PotBb is not { } pot)
                return 0;
            var blinds = 1.5 + (turn.CanCheck && heroPos != "BB" ? 1 : 0);
            var slack = heroPos == "BB" ? 0.5 : 0.25;
            return Math.Max(0, (int)Math.Floor(pot - blinds + slack + 0.0001));
        }

        private static double BlindOf(string position) => position switch { "SB" => 0.5, "BB" => 1.0, _ => 0.0 };

        /// <summary>
        /// Subir (o apostar) a <paramref name="raiseTo"/> bb. El importe del boton no vale de minimo
        /// porque sigue al slider: la apuesta minima es 1 bb y la subida minima nunca pasa del doble
        /// de la apuesta actual. Si la subida compromete dos tercios del stack, se mete todo. Sin la
        /// apuesta actual legible no se inventa un importe.
        /// </summary>
        private static LiveDecision Raise(string street, double? raiseTo, double? currentBet, double heroCommitted,
            LiveTurnState turn, string reason, List<string> details)
        {
            var bet = street != "PREFLOP" && turn.CanCheck;
            var kind = bet ? "Bet" : "Raise";
            var verb = bet ? "APOSTAR" : "SUBIR A";
            if (raiseTo is not { } target || currentBet is not { } current)
                return new LiveDecision(street, kind, bet ? "APOSTAR" : "SUBIR", reason + " (Importe no legible: elige el tamano en la mesa.)", details);

            var minimum = current > 0 ? current * 2 : 1;
            var amount = Math.Max(Math.Round(target * 2, MidpointRounding.AwayFromZero) / 2, minimum);
            if (turn.HeroStackBb is { } stack && stack > 0)
            {
                var max = stack + heroCommitted;
                if (amount >= max * 0.66)
                    return new LiveDecision(street, kind, $"ALL-IN ({Bb(max).ToUpperInvariant()})", reason + " Con tu stack, la subida es all-in.", details);
            }

            return new LiveDecision(street, kind, $"{verb} {Bb(amount).ToUpperInvariant()}", reason, details);
        }

        /// <summary>
        /// Si una apuesta o subida a <paramref name="to"/> acaba en all-in: <see cref="Raise"/> mete todo
        /// cuando compromete dos tercios de la pila (A-J top pair en Lehigh V: "apostar 55,5 bb" con 52,8
        /// detras se dejaba pasar porque solo contaba como all-in si llegaba a la pila entera).
        /// </summary>
        private static bool BecomesAllIn(double to, double heroCommitted, LiveTurnState turn) =>
            turn.HeroStackBb is { } stack && stack > 0 && to >= (stack + heroCommitted) * 0.66;

        private static LiveDecision Call(string street, double? toCall, LiveTurnState turn, string reason, List<string> details)
        {
            if (toCall is { } amount && turn.HeroStackBb is { } stack && stack > 0 && amount >= stack - 0.01)
                return new LiveDecision(street, "Call", $"IGUALAR ALL-IN ({Bb(stack).ToUpperInvariant()})", reason, details);
            return new LiveDecision(street, "Call", toCall is { } value ? $"IGUALAR {Bb(value).ToUpperInvariant()}" : "IGUALAR", reason, details);
        }

        private static LiveDecision Check(string street, string reason, List<string> details) =>
            new(street, "Check", "PASAR", reason, details);

        private static LiveDecision Fold(string street, string reason, List<string> details) =>
            new(street, "Fold", "NO IR", reason, details);

        private static string Options(LiveTurnState turn) =>
            "Opciones en mesa: " + string.Join(" | ", turn.Options.Select(option => option.AmountBb is { } amount
                ? $"{OptionName(option.Kind)} {Bb(amount)}"
                : OptionName(option.Kind))) +
            (turn.PotBb is { } pot ? $" | bote {Bb(pot)}" : "");

        private static string OptionName(LiveActionKind kind) => kind switch
        {
            LiveActionKind.Fold => "No ir",
            LiveActionKind.Check => "Pasar",
            LiveActionKind.Call => "Igualar",
            LiveActionKind.Bet => "Apostar",
            LiveActionKind.Raise => "Subir a",
            _ => "All-in"
        };

        private static string CodeName(string? code) => code switch
        {
            "R" => "subir",
            "C" => "pagar",
            "F" => "no ir",
            _ => "sin tabla"
        };

        private static string Bb(double value) => $"{value.ToString("0.##", Spanish)} bb";
    }
}
