using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HandReader.Core.Models;
using Hud.App.Views;

namespace Hud.App.Services.Trainer
{
    /// <summary>Stats del rival que usa el entrenador, vengan del historial guardado o de la sesion.</summary>
    public sealed record TrainerVillainProfile(
        string Name,
        int Hands,
        double Vpip,
        double Pfr,
        double ThreeBet,
        double Af,
        double FoldVsCBet,
        double Wtsd,
        string Source)
    {
        public const int MinHandsForRange = 40;

        public bool HasSample => Hands >= 15;
        public bool UseStatsForRange => Hands >= MinHandsForRange && Vpip > 0;

        public bool IsCallingStation => HasSample && (Wtsd >= Limits.StationWtsd || (Vpip >= Limits.LooseVpip && Af < Limits.PassiveAf));
        public bool IsNit => HasSample && Vpip > 0 && Vpip <= Limits.NitVpip;
        public bool IsManiac => HasSample && Vpip >= Limits.ManiacVpip && Af >= Limits.ManiacAf;
        public bool FoldsToCBets => HasSample && FoldVsCBet >= Limits.FoldsToCBet;

        /// <summary>
        /// Trampero: casi nunca sube antes del flop (entra pagando con todo, hasta con QQ) pero despues
        /// del flop es agresivo, y esas apuestas y subidas son de mano (kastensyster, 18-11: VPIP 36,
        /// PFR 1,6, AF 2,6; ensenando full, color y doble pareja tras pasar y subir).
        /// </summary>
        public bool IsTrapper => HasSample && !IsManiac && Vpip >= Limits.TrapperVpip && Pfr <= Limits.TrapperPfr && Af >= Limits.TrapperAf;

        /// <summary>
        /// Fuga que se explota en vivo, solo con muestra para su rango (40+ manos); null si no hay
        /// datos o juega sin una fuga clara (ahi manda el GTO: juego reservado).
        /// </summary>
        public string? Leak => !UseStatsForRange ? null
            : IsManiac ? "maníaco"
            : IsTrapper ? "trampero"
            : IsCallingStation ? "pagador"
            : IsNit ? "nit"
            : FoldsToCBets ? "se retira mucho"
            : null;

        /// <summary>Como se le explota, en una frase.</summary>
        public string? LeakPlan => Leak switch
        {
            "maníaco" => "farolea y sube mucho: paga más ligero con mano media, deja que apueste él y no le hagas faroles",
            "pagador" => "paga casi todo: apuesta por valor más fino y más grande, y no le hagas faroles",
            "trampero" => "casi nunca sube antes del flop, pero cuando sube o va all-in después del flop tiene mano: créele y retírate sin una mano muy fuerte; si solo paga, apuéstale por valor",
            "nit" => "se retira mucho: más faroles, y cuando él apuesta o sube suele tener mano",
            "se retira mucho" => $"se retira al {FoldVsCBet:0}% de las c-bets: apuesta más a menudo, sobre todo de c-bet",
            _ => null
        };

        public string Kind
        {
            get
            {
                if (!HasSample) return "Sin muestra";
                if (IsManiac) return "Maniaco";
                if (IsTrapper) return "Trampero";
                if (Vpip >= Limits.LooseVpip && Pfr < Vpip * Limits.FishPfrRatio) return "Fish pasivo";
                if (IsNit) return "Nit";
                if (Vpip <= Limits.TagVpip && Pfr >= Vpip * Limits.TagPfrRatio) return "Regular TAG";
                if (Vpip <= Limits.LooseVpip && Pfr >= Vpip * Limits.LagPfrRatio) return "LAG";
                return "Recreacional";
            }
        }

        /// <summary>
        /// Limites de cada tipo de rival como percentiles de la poblacion real
        /// (Data/population_baselines.json). Entre parentesis el numero fijo de antes, que se usa si
        /// no esta el archivo; con la calibracion actual quedan casi iguales.
        /// </summary>
        private static class Limits
        {
            public static readonly double StationWtsd = P("WTSD", 75, 35);          // va al showdown mas que 3 de cada 4
            public static readonly double LooseVpip = P("VPIP", 70, 40);
            public static readonly double PassiveAf = P("AF", 20, 1.5);
            public static readonly double NitVpip = P("VPIP", 15, 16);
            public static readonly double ManiacVpip = P("VPIP", 60, 35);
            public static readonly double ManiacAf = P("AF", 75, 3);
            public static readonly double FoldsToCBet = P("FVCBF", 55, 55);
            public static readonly double FishPfrRatio = P("PFR_VPIP_RATIO", 50, 0.4);
            public static readonly double TagVpip = P("VPIP", 45, 28);
            public static readonly double TagPfrRatio = P("PFR_VPIP_RATIO", 85, 0.7);
            public static readonly double LagPfrRatio = P("PFR_VPIP_RATIO", 75, 0.6);
            public static readonly double TrapperVpip = P("VPIP", 50, 30);          // juega al menos como la media
            public static readonly double TrapperPfr = P("PFR", 15, 5);             // y casi no sube preflop
            public static readonly double TrapperAf = P("AF", 55, 2.2);             // pero despues del flop apuesta y sube

            private static double P(string key, double percentile, double fallback) =>
                PopulationBaselines.Percentile(key, percentile, fallback);
        }

        public string Summary => HasSample
            ? string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} | VPIP {2:0}/PFR {3:0}/3B {4:0} AF {5:0.#} FvCB {6:0} WTSD {7:0} ({8} manos, {9})",
                Name, Kind, Vpip, Pfr, ThreeBet, Af, FoldVsCBet, Wtsd, Hands, Source)
            : $"{Name}: sin muestra suficiente ({Hands} manos)";

        public static TrainerVillainProfile? FromSession(PlayerStats? stats) =>
            stats is null
                ? null
                : new TrainerVillainProfile(stats.Name, stats.HandsReceived, stats.VPIPPct, stats.PFRPct, stats.ThreeBetPct,
                    stats.AF, stats.FoldVsCBetFlopPct, stats.WTSDPct, "sesion");

        public static TrainerVillainProfile? FromHistory(string name) =>
            VillainHistoryStore.TryGet(PokerStarsHandHistory.NormalizeName(name), out var row)
                ? new TrainerVillainProfile(row.Name, row.TotalHands, row.VPIPPct, row.PFRPct, row.ThreeBetPct,
                    row.AF, row.FoldVsCBetFlopPct, row.WTSDPct, "historial")
                : null;

        /// <summary>Se queda con la fuente con mas manos: el historial suele ganar salvo rivales nuevos.</summary>
        public static TrainerVillainProfile? Best(string name, PlayerStats? sessionStats)
        {
            var history = FromHistory(name);
            var session = FromSession(sessionStats);
            if (history is null) return session;
            if (session is null) return history;
            return session.Hands > history.Hands ? session : history;
        }
    }

    /// <summary>Lectura de la mano del heroe: que tiene, que proyectos y cuantos outs.</summary>
    public sealed record HandInsight(string Made, string Draws, int Outs, int Category, int StrengthClass)
    {
        public string Describe()
        {
            var text = Made;
            if (!string.IsNullOrWhiteSpace(Draws))
                text += $" + {Draws}";
            if (Outs > 0)
                text += $" ({Outs} outs)";
            return text;
        }
    }

    public static class HandInsightReader
    {
        private static readonly string[] CategoryNames =
        {
            "Carta alta", "Pareja", "Doble pareja", "Trio", "Escalera", "Color", "Full", "Poker", "Escalera de color"
        };

        public static string CategoryName(int category) =>
            category >= 0 && category < CategoryNames.Length ? CategoryNames[category] : "?";

        public static HandInsight Read(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            if (hero.Count < 2)
                return new HandInsight("Sin cartas", "", 0, 0, 0);

            if (board.Count < 3)
            {
                var name = TrainerCards.ComboName(hero[0], hero[1]);
                var top = PreflopRanking.TopPercentOf(name);
                return new HandInsight($"{name} (top {top:0.#}% de manos)", "", 0, 0, 0);
            }

            var all = hero.Concat(board).ToArray();
            var score = FastHandEvaluator.Evaluate(all);
            var category = FastHandEvaluator.Category(score);
            var made = DescribeMade(hero, board, category);
            var (draws, drawClass) = DescribeDraws(hero, board, category);
            var outs = board.Count < 5 ? CountOuts(hero, board) : 0;
            return new HandInsight(made, draws, outs, category, StrengthClass(hero[0], hero[1], board));
        }

        /// <summary>
        /// Clase de fuerza para estrechar rangos: 0 aire, 1 debil (pareja baja, gutshot),
        /// 2 media (top pair/overpair, proyecto fuerte), 3 fuerte (doble pareja real o mejor).
        /// </summary>
        public static int StrengthClass(int a, int b, IReadOnlyList<int> board) => Strength(a, b, board, draws: true);

        /// <summary>
        /// Como <see cref="StrengthClass"/> pero solo con la mano hecha (los proyectos no cuentan):
        /// 0 sin pareja propia, 1 pareja debil, 2 top pair/overpair, 3 doble pareja real o mejor.
        /// </summary>
        public static int MadeClass(int a, int b, IReadOnlyList<int> board) => Strength(a, b, board, draws: false);

        private static int Strength(int a, int b, IReadOnlyList<int> board, bool draws)
        {
            Span<int> cards = stackalloc int[7];
            cards[0] = a;
            cards[1] = b;
            for (var i = 0; i < board.Count; i++) cards[2 + i] = board[i];
            var total = 2 + board.Count;

            var score = FastHandEvaluator.Evaluate(cards[..total]);
            var boardScore = FastHandEvaluator.Evaluate(cards.Slice(2, board.Count));
            var category = FastHandEvaluator.Category(score);
            var boardCategory = FastHandEvaluator.Category(boardScore);
            var drawClass = draws && board.Count < 5 ? DrawClass(a, b, board) : 0;

            // Fuerte: la mano propia mejora la de la mesa con algo mas que una pareja (doble pareja
            // con las dos cartas, trio, escalera, color o mejor por encima de lo que hace la mesa).
            if (category >= FastHandEvaluator.Straight && score > boardScore ||
                category == FastHandEvaluator.Trips && category > boardCategory ||
                category == FastHandEvaluator.TwoPair && category > boardCategory && OwnPairs(a, b, board) >= 2)
                return 3;

            // Pareja propia (tambien sobre una mesa doblada, donde la "doble pareja" lleva la de la
            // mesa): overpair o top pair = media; por debajo = debil.
            var ra = TrainerCards.Rank(a);
            var rb = TrainerCards.Rank(b);
            var boardRanks = board.Select(TrainerCards.Rank).ToHashSet();
            if (ra == rb || boardRanks.Contains(ra) || boardRanks.Contains(rb))
                return Math.Max(PairClass(a, b, board), drawClass);

            return drawClass;
        }

        /// <summary>Cuantas de las parejas de la mejor mano usan una carta propia.</summary>
        private static int OwnPairs(int a, int b, IReadOnlyList<int> board)
        {
            var ra = TrainerCards.Rank(a);
            var rb = TrainerCards.Rank(b);
            var pairs = board.Select(TrainerCards.Rank).Append(ra).Append(rb)
                .GroupBy(rank => rank)
                .Where(group => group.Count() >= 2)
                .Select(group => group.Key)
                .OrderByDescending(rank => rank)
                .Take(2);
            return pairs.Count(rank => rank == ra || rank == rb);
        }

        private static int PairClass(int a, int b, IReadOnlyList<int> board)
        {
            var topBoard = board.Max(TrainerCards.Rank);
            var ra = TrainerCards.Rank(a);
            var rb = TrainerCards.Rank(b);
            if (ra == rb)
                return ra > topBoard ? 2 : 1;

            var boardRanks = board.Select(TrainerCards.Rank).ToHashSet();
            var paired = boardRanks.Contains(ra) ? ra : boardRanks.Contains(rb) ? rb : -1;
            return paired == topBoard ? 2 : paired >= 0 ? 1 : 0;
        }

        /// <summary>2 = proyecto de color o escalera abierta, 1 = gutshot, 0 = nada.</summary>
        private static int DrawClass(int a, int b, IReadOnlyList<int> board)
        {
            if (FlushDrawUsesHole(a, b, board))
                return 2;

            var straightRanks = StraightOutRanks(a, b, board);
            return straightRanks >= 2 ? 2 : straightRanks == 1 ? 1 : 0;
        }

        private static bool FlushDrawUsesHole(int a, int b, IReadOnlyList<int> board)
        {
            for (var suit = 0; suit < 4; suit++)
            {
                var onBoard = board.Count(card => TrainerCards.Suit(card) == suit);
                var inHand = (TrainerCards.Suit(a) == suit ? 1 : 0) + (TrainerCards.Suit(b) == suit ? 1 : 0);
                if (inHand > 0 && onBoard + inHand == 4)
                    return true;
            }

            return false;
        }

        /// <summary>Cuantos rangos distintos completan escalera usando cartas propias (2 = abierta).</summary>
        private static int StraightOutRanks(int a, int b, IReadOnlyList<int> board)
        {
            var boardMask = board.Aggregate(0, (mask, card) => mask | 1 << TrainerCards.Rank(card));
            var mask = boardMask | 1 << TrainerCards.Rank(a) | 1 << TrainerCards.Rank(b);
            if (FastHandEvaluator.StraightHigh(mask) >= 0)
                return 0;

            var count = 0;
            for (var rank = 0; rank < 13; rank++)
            {
                if ((mask & (1 << rank)) != 0)
                    continue;
                if (FastHandEvaluator.StraightHigh(mask | 1 << rank) >= 0 &&
                    FastHandEvaluator.StraightHigh(boardMask | 1 << rank) < 0)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Outs "limpios": cartas que suben la mano del heroe a doble pareja o mejor usando sus
        /// cartas. Doble pareja y trio solo cuentan si la carta nueva empareja una carta propia
        /// (si empareja la mesa, la mejora es para todos); escalera o mejor, si la mesa sola no la hace.
        /// </summary>
        public static int CountOuts(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            var known = hero.Concat(board).ToHashSet();
            var current = FastHandEvaluator.Category(FastHandEvaluator.Evaluate(hero.Concat(board).ToArray()));
            var heroRanks = hero.Select(TrainerCards.Rank).ToHashSet();
            var outs = 0;
            for (var card = 0; card < 52; card++)
            {
                if (known.Contains(card))
                    continue;

                var nextBoard = board.Append(card).ToArray();
                var next = FastHandEvaluator.Category(FastHandEvaluator.Evaluate(hero.Concat(nextBoard).ToArray()));
                if (next <= current || next < FastHandEvaluator.TwoPair)
                    continue;

                var isOut = next <= FastHandEvaluator.Trips
                    ? heroRanks.Contains(TrainerCards.Rank(card))
                    : next > FastHandEvaluator.Category(FastHandEvaluator.Evaluate(nextBoard));
                if (isOut)
                    outs++;
            }

            return outs;
        }

        private static string DescribeMade(IReadOnlyList<int> hero, IReadOnlyList<int> board, int category)
        {
            var boardScore = FastHandEvaluator.Evaluate(board.ToArray());
            var boardCategory = FastHandEvaluator.Category(boardScore);
            var ra = TrainerCards.Rank(hero[0]);
            var rb = TrainerCards.Rank(hero[1]);
            var boardRanks = board.Select(TrainerCards.Rank).OrderByDescending(rank => rank).ToList();

            if (category == FastHandEvaluator.Trips && ra == rb && boardRanks.Contains(ra))
                return $"Set de {RankName(ra)}";

            if (category == FastHandEvaluator.OnePair)
            {
                if (boardCategory == FastHandEvaluator.OnePair)
                    return $"Pareja en la mesa (carta alta {RankName(Math.Max(ra, rb))})";
                return OwnPairText(ra, rb, boardRanks);
            }

            // Mesa doblada: la "doble pareja" lleva la pareja de la mesa; lo propio es una pareja.
            if (category == FastHandEvaluator.TwoPair && boardCategory == FastHandEvaluator.OnePair &&
                OwnPairs(hero[0], hero[1], board) < 2)
                return $"{OwnPairText(ra, rb, boardRanks)} con la mesa doblada";

            // Doble pareja en la mesa: lo propio es la pareja que la mejora.
            if (category == FastHandEvaluator.TwoPair && boardCategory == FastHandEvaluator.TwoPair &&
                OwnPairs(hero[0], hero[1], board) >= 1)
                return $"{OwnPairText(ra, rb, boardRanks)} con doble pareja en la mesa";

            // Escalera o color que ya estan en la mesa: solo cuentan si las cartas propias la mejoran.
            if (category >= FastHandEvaluator.Straight && category == boardCategory)
            {
                var score = FastHandEvaluator.Evaluate(hero.Concat(board).ToArray());
                return score > boardScore
                    ? $"{CategoryName(category)} (mejor que la de la mesa)"
                    : $"{CategoryName(category)} (de la mesa)";
            }

            if (category > boardCategory)
                return CategoryName(category);

            return category == FastHandEvaluator.HighCard
                ? $"Carta alta {RankName(Math.Max(ra, rb))}"
                : $"{CategoryName(category)} (de la mesa)";
        }

        /// <summary>Nombre de la pareja que hacen las cartas propias (overpair, top pair, segunda...).</summary>
        private static string OwnPairText(int ra, int rb, IReadOnlyList<int> boardRanks)
        {
            var topBoard = boardRanks.Max();
            if (ra == rb)
                return ra > topBoard ? $"Overpair de {RankName(ra)}" : $"Pareja de mano de {RankName(ra)} (bajo la mesa)";

            var paired = boardRanks.Contains(ra) && boardRanks.Contains(rb)
                ? Math.Max(ra, rb)
                : boardRanks.Contains(ra) ? ra : rb;
            var kicker = paired == ra ? rb : ra;
            var distinctBoard = boardRanks.Distinct().OrderByDescending(rank => rank).ToList();
            return distinctBoard.IndexOf(paired) switch
            {
                0 => $"Top pair de {RankName(paired)} (kicker {RankName(kicker)})",
                1 => $"Segunda pareja ({RankName(paired)})",
                _ => $"Pareja baja ({RankName(paired)})"
            };
        }

        private static (string Text, int Class) DescribeDraws(IReadOnlyList<int> hero, IReadOnlyList<int> board, int category)
        {
            if (board.Count >= 5 || category >= FastHandEvaluator.Straight)
                return ("", 0);

            var parts = new List<string>();
            if (FlushDrawUsesHole(hero[0], hero[1], board))
                parts.Add("proyecto de color");

            var straightRanks = StraightOutRanks(hero[0], hero[1], board);
            if (straightRanks >= 2) parts.Add("proyecto de escalera abierto");
            else if (straightRanks == 1) parts.Add("gutshot");

            return (string.Join(" + ", parts), parts.Count > 0 ? 2 : 0);
        }

        public static string RankName(int rank) => rank switch
        {
            12 => "A",
            11 => "K",
            10 => "Q",
            9 => "J",
            8 => "10",
            _ => (rank + 2).ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Estima el rango del rival: primero por su linea preflop (tablas APH o stats si hay muestra)
    /// y despues lo estrecha con lo que hizo en cada calle (apostar, pagar, pasar).
    /// Es una lectura de manos simplificada; el alumno ve siempre de donde sale el rango.
    /// </summary>
    public sealed class TrainerRangeEstimator
    {
        private readonly AphPreflopProvider _charts;

        public TrainerRangeEstimator(AphPreflopProvider charts) => _charts = charts;

        public HandRange Estimate(TrainerHand hand, string villain, TrainerVillainProfile? profile, int beforeActionIndex)
        {
            var range = PreflopRange(hand, villain, profile);
            var actions = hand.Actions.Where(action => action.Index < beforeActionIndex).ToList();

            foreach (var street in new[] { "FLOP", "TURN", "RIVER" })
            {
                var streetActions = actions.Where(action => action.Street == street).ToList();
                if (streetActions.Count == 0)
                    break;

                var board = streetActions[0].Board;
                if (board.Count < 3)
                    break;

                var villainActions = streetActions.Where(action => PokerStarsHandHistory.SamePlayer(action.Player, villain)).ToList();
                if (villainActions.Count == 0)
                    continue;

                // Pagar el all-in de otro es una linea mucho mas fuerte que un call normal (igual que en vivo).
                var callsAllIn = villainActions.Any(action => action.Kind == TrainerActionKind.Call &&
                    streetActions.Any(other => other.Index < action.Index && other.IsAllIn &&
                                               !PokerStarsHandHistory.SamePlayer(other.Player, villain)));
                var line = villainActions.Any(action => action.Kind == TrainerActionKind.Raise) ? "raise"
                    : villainActions.Any(action => action.Kind == TrainerActionKind.Bet) ? "bet"
                    : villainActions.Any(action => action.Kind == TrainerActionKind.Call) ? callsAllIn ? "callallin" : "call"
                    : "check";

                var playersInStreet = streetActions.Select(action => action.Player).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                range = range.WithWeights(
                    $"{range.Description}; {StreetName(street)}: {LineName(line)}",
                    LineWeight(line, street, profile, board, multiway: playersInStreet > 2));
            }

            return range;
        }

        /// <summary>
        /// Peso por clase de fuerza [aire, debil, media, fuerte] segun la linea del rival. En el
        /// river ya no hay proyectos: apostar o subir ahi polariza mucho mas que en el flop.
        /// </summary>
        public static double[] Factors(string line, string street, TrainerVillainProfile? profile, bool multiway = false)
        {
            var station = profile?.IsCallingStation == true;
            var maniac = profile?.IsManiac == true;
            // Trampero: sus apuestas y subidas postflop son de mano; cuando solo paga, paga ligero.
            var trapper = profile?.IsTrapper == true;
            // Con varios rivales en el bote, apostar o subir ya polariza como en el river: quien apuesta
            // contra dos o tres jugadores casi siempre tiene mano (QQ contra tres limpers, AK 3-bet a cuatro).
            var river = street == "RIVER" || multiway && line is "bet" or "raise";
            return line switch
            {
                "raise" when river => new[] { maniac ? 0.3 : trapper ? 0.02 : 0.08, trapper ? 0.05 : 0.12, trapper ? 0.25 : 0.45, 1.0 },
                "raise" => new[] { maniac ? 0.45 : trapper ? 0.05 : 0.15, trapper ? 0.12 : 0.3, trapper ? 0.45 : 0.7, 1.0 },
                "bet" when river => new[] { maniac ? 0.6 : trapper ? 0.1 : 0.22, trapper ? 0.2 : 0.3, trapper ? 0.6 : 0.8, 1.0 },
                "bet" => new[] { maniac ? 0.7 : trapper ? 0.2 : 0.35, trapper ? 0.45 : 0.6, 1.0, 1.0 },
                "call" => new[] { station || trapper ? 0.4 : 0.15, station || trapper ? 0.95 : 0.75, 1.0, 1.0 },
                // Pagar un all-in: casi nunca con aire o con poco; aun un pagador lo hace sobre todo con mano.
                "callallin" => new[] { station ? 0.1 : 0.03, station ? 0.4 : 0.2, station ? 0.85 : 0.65, 1.0 },
                _ => new[] { 1.0, 1.0, 1.0, maniac ? 0.5 : 0.7 }
            };
        }

        /// <summary>
        /// Peso de cada combinacion del rival por su linea en una calle: el de su clase de fuerza y, si
        /// apuesta o sube, dentro de las manos fuertes cuenta lo arriba que esta. Antes un color con el 2
        /// pesaba igual que el color con as, y una doble pareja floja igual que un set: contra una
        /// apuesta grande el solver le daba muchas manos peores que la del heroe y decia pagar.
        /// </summary>
        public static Func<WeightedCombo, double> LineWeight(string line, string street, TrainerVillainProfile? profile,
            IReadOnlyList<int> board, bool multiway = false)
        {
            var factors = Factors(line, street, profile, multiway);
            var cards = board.ToList();
            var aggressive = line is "bet" or "raise";
            var strong = aggressive && cards.Count >= 3 ? StrongPercentiles(cards) : null;
            // Cuanto pesa la mas floja de las fuertes: subir o apostar en el river (o a varios) es mas polar.
            var floor = line == "raise" || street == "RIVER" || multiway ? 0.2 : 0.4;
            return combo =>
            {
                var strength = HandInsightReader.StrengthClass(combo.A, combo.B, cards);
                var weight = factors[strength];
                if (strength == 3 && strong is not null && strong.TryGetValue(Key(combo.A, combo.B), out var percentile))
                    weight *= floor + (1 - floor) * percentile;
                return weight;
            };
        }

        private static int Key(int a, int b) => Math.Min(a, b) * 52 + Math.Max(a, b);

        private static readonly object StrongCacheSync = new();
        private static readonly Dictionary<string, Dictionary<int, double>> StrongCache = new();

        /// <summary>
        /// Para cada combinacion de clase fuerte en esta mesa, en que parte de las fuertes esta (0 = la mas
        /// floja, 1 = la mejor). Se calcula una vez por mesa.
        /// </summary>
        public static Dictionary<int, double> StrongPercentiles(IReadOnlyList<int> board)
        {
            var cacheKey = string.Join(",", board);
            lock (StrongCacheSync)
            {
                if (StrongCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            var used = new HashSet<int>(board);
            var deck = Enumerable.Range(0, 52).Where(card => !used.Contains(card)).ToArray();
            var cards = new int[board.Count + 2];
            for (var i = 0; i < board.Count; i++)
                cards[i + 2] = board[i];
            var strong = new List<(int Key, int Score)>();
            for (var x = 0; x < deck.Length; x++)
            for (var y = x + 1; y < deck.Length; y++)
            {
                if (HandInsightReader.StrengthClass(deck[x], deck[y], board) != 3)
                    continue;
                cards[0] = deck[x];
                cards[1] = deck[y];
                strong.Add((Key(deck[x], deck[y]), FastHandEvaluator.Evaluate(cards)));
            }

            var result = new Dictionary<int, double>();
            if (strong.Count > 0)
            {
                var scores = strong.Select(item => item.Score).OrderBy(score => score).ToList();
                foreach (var (key, score) in strong)
                {
                    // Percentil por puntuacion (los empates comparten valor).
                    var below = scores.BinarySearch(score);
                    while (below > 0 && scores[below - 1] == score) below--;
                    result[key] = scores.Count == 1 ? 1 : (double)below / (scores.Count - 1);
                }
            }

            lock (StrongCacheSync)
            {
                if (StrongCache.Count > 64)
                    StrongCache.Clear();
                StrongCache[cacheKey] = result;
            }

            return result;
        }

        public HandRange PreflopRange(TrainerHand hand, string villain, TrainerVillainProfile? profile)
        {
            var seat = hand.SeatOf(villain);
            var villainPos = seat?.ChartPosition ?? "-";
            var preflop = hand.Actions.Where(action => action.Street == "PREFLOP" && action.Kind != TrainerActionKind.Post).ToList();
            var raises = preflop.Where(action => action.Kind == TrainerActionKind.Raise).ToList();
            var villainActions = preflop.Where(action => PokerStarsHandHistory.SamePlayer(action.Player, villain)).ToList();
            var last = villainActions.LastOrDefault(action => action.Kind is TrainerActionKind.Raise or TrainerActionKind.Call or TrainerActionKind.Check);

            string PosOf(TrainerAction action) => hand.SeatOf(action.Player)?.ChartPosition ?? "-";

            if (last is null)
                return HandRange.Any;

            var raisesBefore = last.PreflopRaisesBefore;
            var firstRaiser = raises.Count > 0 ? PosOf(raises[0]) : "-";
            var previousRaiser = raises.Count > 0 ? PosOf(raises[Math.Clamp(raisesBefore, 1, raises.Count) - 1]) : "-";
            return PreflopRangeFor(villainPos, last.Kind, raisesBefore, firstRaiser, previousRaiser, profile);
        }

        /// <summary>
        /// Rango preflop segun la ultima accion preflop del rival (subir, pagar o pasar), cuantas
        /// subidas habia antes y quien las hizo. Lo usan la correccion (con el historial) y la
        /// decision en vivo (con lo leido en las placas), para que los dos estimen lo mismo.
        /// </summary>
        public HandRange PreflopRangeFor(
            string villainPos, TrainerActionKind kind, int raisesBefore, string firstRaiserPos, string previousRaiserPos,
            TrainerVillainProfile? profile)
        {
            var stats = profile?.UseStatsForRange == true ? profile : null;

            if (kind == TrainerActionKind.Raise)
            {
                if (raisesBefore == 0)
                {
                    if (stats is not null)
                        return Top($"open de {villainPos} por stats (PFR {stats.Pfr:0}%)", Math.Max(stats.Pfr, 5));
                    return Chart($"open de {villainPos} (tabla APH)", "Open", villainPos, "", "R") ?? Top($"open de {villainPos}", 22);
                }

                if (raisesBefore == 1)
                {
                    if (stats is not null && stats.ThreeBet > 0)
                        return Top($"3bet de {villainPos} por stats (3B {stats.ThreeBet:0}%)", Math.Clamp(stats.ThreeBet * 0.6, 3, 15));
                    return Chart($"3bet de {villainPos} vs {firstRaiserPos} (tabla APH)", "vs Raise", villainPos, firstRaiserPos, "R") ?? Top($"3bet de {villainPos}", 7);
                }

                return Chart($"4bet+ de {villainPos} vs {previousRaiserPos} (tabla APH)", "vs 3bet", villainPos, previousRaiserPos, "R") ?? Top($"4bet+ de {villainPos}", 3);
            }

            if (kind == TrainerActionKind.Call)
            {
                if (raisesBefore == 0)
                {
                    return stats is not null
                        ? Top($"limp de {villainPos} por stats (VPIP {stats.Vpip:0}%)", Math.Max(stats.Vpip, 10), Math.Min(stats.Pfr, 8))
                        : Top($"limp de {villainPos}", 45, 6);
                }

                if (raisesBefore == 1)
                {
                    if (stats is not null)
                        return Top($"call de {villainPos} por stats (VPIP {stats.Vpip:0}%)", Math.Max(stats.Vpip, 12), 3);
                    return Chart($"call de {villainPos} vs {firstRaiserPos} (tabla APH)", "vs Raise", villainPos, firstRaiserPos, "C") ?? Top($"call de {villainPos}", 25, 4);
                }

                var scenario = raisesBefore == 2 ? "vs 3bet" : raisesBefore == 3 ? "vs 4bet" : "vs 5bet";
                return Chart($"call de {villainPos} {scenario} de {previousRaiserPos} (tabla APH)", scenario, villainPos, previousRaiserPos, "C")
                       ?? Top($"call de {villainPos} {scenario}", raisesBefore == 2 ? 8 : 4, 1);
            }

            // Check en la ciega grande sin subidas: cualquier mano menos las que habria subido.
            return Top($"check de {villainPos} en ciega grande", 100, 8);
        }

        private HandRange? Chart(string description, string scenario, string hero, string villain, string code)
        {
            var classes = _charts.GetClasses(scenario, hero, villain, code);
            return classes.Count == 0 ? null : HandRange.FromClasses(description, classes);
        }

        private static HandRange Top(string description, double percent, double skipTop = 0) =>
            HandRange.FromClasses(description, PreflopRanking.TopPercent(percent, skipTop));

        public static string StreetName(string street) => street switch
        {
            "PREFLOP" => "Preflop",
            "FLOP" => "Flop",
            "TURN" => "Turn",
            "RIVER" => "River",
            _ => street
        };

        private static string LineName(string line) => line switch
        {
            "raise" => "subio",
            "bet" => "aposto",
            "call" => "pago",
            "callallin" => "pago un all-in",
            _ => "paso"
        };
    }
}
