using System;
using System.Collections.Generic;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Reglas de referencia compartidas por la decision en vivo y la correccion al terminar la mano.
    /// Estar en un solo sitio garantiza que lo que se recomienda en el turno del alumno sea lo mismo
    /// con lo que despues se califica su decision.
    /// </summary>
    public static class TrainerDecisionRules
    {
        /// <summary>Equity minima para apostar por valor: en el river solo pagan manos hechas.</summary>
        public static double ValueEquity(string street) => street == "RIVER" ? 60 : 55;

        /// <summary>Lo que le toca a cada jugador si todos tuvieran lo mismo: 50% contra uno, 25% contra tres.</summary>
        public static double FairShare(int opponents) => 100.0 / (Math.Max(1, opponents) + 1);

        /// <summary>
        /// Equity minima (contra todos los rivales a la vez) para apostar por valor. Contra uno, 55%
        /// (60% en el river). Con varios rivales la equity contra todos baja sola, asi que se mide
        /// frente a la parte justa, con mas margen por cada rival: alguno de los que pagan suele
        /// tener mano. Con tres rivales, 37,5% (40% en el river).
        /// </summary>
        public static double ValueEquity(string street, int opponents)
        {
            var headsUp = ValueEquity(street);
            return opponents <= 1 ? headsUp : FairShare(opponents) * (headsUp / 50 + 0.2 * (opponents - 1));
        }

        /// <summary>Equity de valor claro: 10 puntos sobre el valor contra uno, en proporcion multiway.</summary>
        public static double ClearValueEquity(string street, int opponents) =>
            ValueEquity(street, opponents) + 10 * FairShare(opponents) / 50;

        public static bool HasStrongDraw(HandInsight insight, string street) => insight.Outs >= 8 && street != "RIVER";

        /// <summary>
        /// Frente a una apuesta: "Raise", "Call" o "Fold". Sin subida legal (el heroe no tiene mas
        /// fichas que lo que paga, o todos los rivales estan all-in) nunca se recomienda subir.
        /// </summary>
        public static string FacingBet(double equity, double required, HandInsight insight, string street, bool canRaise = true)
        {
            var margin = equity - required;
            var strong = equity >= 70 && street != "RIVER" || equity >= 80;
            if (strong && canRaise)
                return "Raise";
            return margin >= 0 || HasStrongDraw(insight, street) && margin >= -6 ? "Call" : "Fold";
        }

        /// <summary>Sin apuesta delante (equity contra los rivales a la vez): tipo de apuesta recomendada o "Check".</summary>
        public static string WithInitiative(double equity, HandInsight insight, string street, bool heroIsPfa, int opponents)
        {
            if (equity >= ClearValueEquity(street, opponents)) return "Bet (valor)";
            if (equity >= ValueEquity(street, opponents)) return "Bet fino o check";
            if (HasStrongDraw(insight, street)) return "Bet (semi-farol)";
            if (heroIsPfa && street == "FLOP" && opponents <= 1 && equity >= 35) return "Bet (c-bet)";
            return "Check";
        }

        /// <summary>Tamano de apuesta sugerido (fraccion del bote) para cada tipo de apuesta.</summary>
        public static double BetFraction(string reference) => reference switch
        {
            "Bet (valor)" => 0.66,
            "Bet (semi-farol)" => 0.6,
            "Bet fino o check" => 0.4,
            "Bet (c-bet)" => 0.33,
            _ => 0
        };

        /// <summary>Rango de iso-raise contra limpers (top % de manos) segun la posicion.</summary>
        public static double IsoPercent(string position) => position switch
        {
            "BTN" or "CO" => 16,
            "SB" or "BB" => 12,
            _ => 10
        };

        /// <summary>Limite de fuerza (top %) para entrar barato en un bote multiway.</summary>
        public const double PlayableMultiwayPercent = 35;

        /// <summary>
        /// Manos que se juegan baratas en un bote multiway (limp detras o completar): las que ligan
        /// manos fuertes que no quedan dominadas. Parejas (set), suited del top 35% (color), conectores
        /// suited desde 54s (escalera y color) y offsuit solo con las dos cartas de T o mas. Fuera los
        /// ases y reyes flojos offsuit: con varios rivales su pareja suele perder contra un kicker mejor.
        /// </summary>
        public static bool PlayableMultiway(string combo, double top)
        {
            if (combo.Length < 2)
                return false;
            if (combo.Length == 2 || combo[0] == combo[1])
                return true;

            var high = TrainerCards.Ranks.IndexOf(combo[0]);
            var low = TrainerCards.Ranks.IndexOf(combo[1]);
            if (high < low)
                (high, low) = (low, high);
            if (combo.EndsWith("s", StringComparison.Ordinal))
                return top <= PlayableMultiwayPercent || high - low <= 2 && low >= TrainerCards.Ranks.IndexOf('4');
            return top <= PlayableMultiwayPercent && low >= TrainerCards.Ranks.IndexOf('T');
        }

        /// <summary>Por que no entrar en el bote multiway con esa mano.</summary>
        public static string MultiwayFoldReason(string combo)
        {
            if (combo.EndsWith("o", StringComparison.Ordinal) && combo[0] == 'A')
                return $"{combo} sin mismo palo se juega mal con varios rivales: cuando ligas el as tu kicker suele perder contra otro as, y no tienes proyecto de color. No ir.";
            if (combo.EndsWith("o", StringComparison.Ordinal))
                return $"{combo} sin mismo palo se juega mal con varios rivales: liga pocas manos fuertes y la pareja que ligas suele quedar dominada. No ir.";
            return $"{combo} no tiene valor suficiente ni para un bote multiway barato.";
        }

        /// <summary>
        /// Sin tabla exacta: hasta que top % se sube y hasta cual se paga segun las subidas que hay
        /// delante, ajustado por el perfil del agresor. Con dos o mas subidas el porcentaje se mide
        /// en el orden de manos premium (PreflopRanking.StrengthTopPercent).
        /// </summary>
        public static (double RaiseTop, double CallTop) ColdThresholds(int raises, TrainerVillainProfile? profile)
        {
            var (raiseTop, callTop) = raises switch
            {
                0 => (18.0, 18.0),
                1 => (4.0, 15.0),
                2 => (2.5, 5.0),
                _ => (2.0, 2.0)
            };

            if (profile?.HasSample == true)
            {
                if (profile.Pfr >= 30) { raiseTop *= 1.6; callTop *= 1.4; }
                if (profile.IsNit) { raiseTop *= 0.6; callTop *= 0.7; }
            }

            return (raiseTop, callTop);
        }

        // ------------------------------------------------------------------ preflop por precio

        /// <summary>Por debajo de este precio (% de equity) el spot preflop lo decide el precio, no la tabla.</summary>
        public const double TinyPricePercent = 15;

        /// <summary>
        /// Subida fuera de los tamanos de las tablas (que suponen un open de 2-5 bb, 3bet de 8-12 y
        /// 4bet de ~25): un open a 12 bb o mas, un 3bet a 30 o mas, un 4bet a 45 o mas. Se decide por
        /// precio contra los rangos de quien sube y de quien ya pago, como un all-in.
        /// </summary>
        public static bool IsOversizedRaise(int raises, double raiseToBb) =>
            raiseToBb >= (raises <= 1 ? 12 : raises == 2 ? 30 : 45);

        /// <summary>
        /// Rango (top % de manos) de quien sube o empuja cuando el spot lo decide el precio. Un
        /// all-in se estrecha con la profundidad (hasta 15 bb top 30%, hasta 40 bb top 12%, mas
        /// profundo top 6%); una subida normal, con las subidas de la mano. Con muestra manda el PFR.
        /// </summary>
        public static double PreflopAggressorTop(bool allIn, double depthBb, int raises, TrainerVillainProfile? profile)
        {
            double top = allIn
                ? depthBb <= 15 ? 30 : depthBb <= 40 ? 12 : 6
                : raises switch { <= 1 => 25, 2 => 10, 3 => 5, _ => 3 };
            if (allIn && raises >= 3)
                top = Math.Min(top, 5);
            if (profile is null || !profile.HasSample)
                return top;
            if (profile.UseStatsForRange && raises <= 1)
                return Math.Clamp(allIn && depthBb > 15 ? profile.Pfr * 0.5 : profile.Pfr, 3, 45);
            return profile.IsManiac ? top * 1.6 : profile.IsNit ? top * 0.7 : top;
        }

        /// <summary>Rango preflop del top X%; los estrechos, en orden de fuerza contra manos fuertes.</summary>
        public static IReadOnlyList<WeightedCombo> PreflopRange(double percent, double skipTopPercent = 0) =>
            HandRange.FromClasses("", percent >= 20 || skipTopPercent > 0
                ? PreflopRanking.TopPercent(percent, skipTopPercent)
                : PreflopRanking.StrengthOrder(percent)).Combos;

        /// <summary>
        /// Quien ya pago esa subida: sin lo mas fuerte, que habria resubido. Contra un all-in solo
        /// pagan manos fuertes.
        /// </summary>
        public static IReadOnlyList<WeightedCombo> PreflopCallerRange(bool aggressorAllIn, double aggressorTop) =>
            aggressorAllIn
                ? PreflopRange(Math.Max(aggressorTop * 0.6, 2.5))
                : PreflopRange(Math.Min(aggressorTop * 1.2, 30), Math.Min(aggressorTop * 0.15, 3));

        /// <summary>
        /// Parte de la equity que se cobra: entera si ya no queda juego (all-in); con juego postflop
        /// las parejas y las suited realizan mas que las offsuit.
        /// </summary>
        public static double Realization(string combo, bool postflopPlay) =>
            !postflopPlay ? 1.0 : combo.Length == 2 || combo.EndsWith("s", StringComparison.Ordinal) ? 0.85 : 0.7;

        /// <summary>
        /// Mesa peligrosa para la mano del heroe: tres o mas cartas del mismo palo sin tener color, o
        /// cuatro cartas seguidas (escalera con una sola carta) sin tener escalera. Null si no lo es.
        /// En esas mesas el solver contra rangos estimados sobrevaloraba manos como doble pareja (AQ en
        /// Q-K-A de diamantes: apuestas de bote entero a dos rivales que pagan todo). Con ellas no se
        /// sube y se apuesta como mucho medio bote.
        /// </summary>
        public static string? BoardDanger(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            if (hero.Count < 2 || board.Count < 3)
                return null;
            var score = FastHandEvaluator.Evaluate(hero.Concat(board).ToArray());
            var category = FastHandEvaluator.Category(score);

            // La mesa sola ya hace escalera o color (o mas): quien tenga una carta mas alta del palo o
            // de la escalera gana, y si la mano propia no la mejora se juega la mesa (como mucho, empate).
            if (board.Count >= 5)
            {
                var boardScore = FastHandEvaluator.Evaluate(board.ToArray());
                var boardCategory = FastHandEvaluator.Category(boardScore);
                if (score == boardScore)
                    return PlaysBoardText;
                if (boardCategory >= FastHandEvaluator.Straight && category <= boardCategory)
                    return boardCategory == FastHandEvaluator.Straight
                        ? "La mesa ya hace escalera: una carta más alta te gana"
                        : $"La mesa ya hace {HandInsightReader.CategoryName(boardCategory).ToLowerInvariant()}: una carta más alta te gana";
            }

            var suited = board.GroupBy(TrainerCards.Suit).OrderByDescending(group => group.Count()).First();
            var suitName = suited.Key switch { 0 => "tréboles", 1 => "diamantes", 2 => "corazones", _ => "picas" };
            if (suited.Count() >= 3 && category < 5)
                return $"Mesa con {suited.Count()} cartas de {suitName} y no tienes color";

            // Color bajo con cuatro del palo en la mesa: dos o mas cartas del palo mas altas que la tuya
            // siguen sin verse (con la K y la Q del palo en la mesa, la J es el segundo mejor color: no
            // es bajo; en Letaba IV se marcaba bajo y solo el as lo ganaba).
            if (suited.Count() >= 4 && category == 5)
            {
                var heroBest = hero.Where(card => TrainerCards.Suit(card) == suited.Key).Select(TrainerCards.Rank).DefaultIfEmpty(-1).Max();
                var onBoard = board.Where(card => TrainerCards.Suit(card) == suited.Key).Select(TrainerCards.Rank).ToHashSet();
                var higherUnseen = Enumerable.Range(heroBest + 1, Math.Max(0, 12 - heroBest)).Count(rank => !onBoard.Contains(rank));
                if (higherUnseen >= 2)
                    return $"Mesa con {suited.Count()} cartas de {suitName} y tu color es bajo";
            }

            var ranks = board.Select(TrainerCards.Rank).Distinct().ToList();
            if (ranks.Contains(12))
                ranks.Add(-1); // el as tambien cierra la escalera baja
            var fourInFive = Enumerable.Range(-1, 10).Any(low => ranks.Count(rank => rank >= low && rank <= low + 4) >= 4);
            if (fourInFive && category < 4)
                return "Mesa con cuatro cartas seguidas (escalera con una carta) y no tienes escalera";
            return null;
        }

        public const string PlaysBoardText = "Juegas la mesa: tus cartas no mejoran lo que ya hay en la mesa";

        /// <summary>
        /// Parte de las manos posibles del rival (todas las parejas de cartas que quedan, por igual) que
        /// le ganan ahora a la del heroe. Mide lo vulnerable que es una mano en esta mesa: un trio de
        /// sietes en 5-7-2-4 pierde contra ~4%; QQ con full en 9-9-A-8-9 contra ~18% (cualquier as); A5
        /// con pareja de ases en 7-A-3-9-Q con tres diamantes contra ~20%.
        /// </summary>
        public static double BeatenShare(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            if (hero.Count < 2 || board.Count < 3)
                return 0;
            var heroScore = FastHandEvaluator.Evaluate(hero.Concat(board).ToArray());
            var used = new HashSet<int>(hero.Concat(board));
            var deck = Enumerable.Range(0, 52).Where(card => !used.Contains(card)).ToArray();
            var cards = new int[board.Count + 2];
            for (var i = 0; i < board.Count; i++)
                cards[i + 2] = board[i];
            int total = 0, beaten = 0;
            for (var a = 0; a < deck.Length; a++)
            for (var b = a + 1; b < deck.Length; b++)
            {
                cards[0] = deck[a];
                cards[1] = deck[b];
                total++;
                if (FastHandEvaluator.Evaluate(cards) > heroScore)
                    beaten++;
            }

            return total == 0 ? 0 : (double)beaten / total;
        }

        /// <summary>Proyecto de color al mejor color posible (la carta mas alta del palo que falta es tuya).</summary>
        public static bool HasNutFlushDraw(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            foreach (var suit in Enumerable.Range(0, 4))
            {
                var onBoard = board.Where(card => TrainerCards.Suit(card) == suit).Select(TrainerCards.Rank).ToList();
                var mine = hero.Where(card => TrainerCards.Suit(card) == suit).Select(TrainerCards.Rank).ToList();
                if (mine.Count == 0 || onBoard.Count + mine.Count != 4)
                    continue;
                var topUnseen = Enumerable.Range(0, 13).Reverse().First(rank => !onBoard.Contains(rank));
                if (mine.Contains(topUnseen))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Dinero ficticio: frente a una apuesta grande (40% del bote o mas) en el turn o el river, con una
        /// mano que pierde contra muchas (river: 8% o mas de las manos posibles le ganan; turn: 15% con
        /// pareja buena o mas, o pareja floja o menos sin el mejor proyecto), no se paga: quien apuesta fuerte
        /// ahi casi siempre tiene la mano. Salvo que el precio sea muy barato (15% o menos) o el rival
        /// sea un maniaco. Devuelve el motivo, o null si se puede pagar. 18-11: A5o con pareja de ases
        /// y A5s con cincos en rivers de color, AJo con proyecto de color a la J en el turn, QJ doble
        /// pareja con escalera posible: pagos de 50-70 bb que perdian.
        /// </summary>
        public static string? NoCallReason(IReadOnlyList<int> hero, IReadOnlyList<int> board, string street, double? toCall, double? potWithBet, bool maniac,
            int opponents = 1)
        {
            if (maniac || street is not ("TURN" or "RIVER") || toCall is not { } owed || potWithBet is not { } pot || owed <= 0)
                return null;
            var potBefore = Math.Max(0.01, pot - owed);
            var price = owed / (pot + owed);
            if (owed < potBefore * 0.4 || price <= 0.15)
                return null;

            var beaten = BeatenShare(hero, board);
            var danger = BoardDanger(hero, board);
            var made = HandInsightReader.MadeClass(hero[0], hero[1], board);
            // Turn: con el proyecto al mejor color se paga por el proyecto; con pareja buena o mas, si pierde
            // contra muchas; con pareja floja o menos (sin el mejor proyecto), no se paga una apuesta grande.
            // Con varios rivales en la mano, mas estricto: alguno suele tener la mano que gana.
            var multiway = opponents >= 2;
            var vulnerable = street == "RIVER"
                ? beaten >= (multiway ? 0.05 : 0.08)
                : !HasNutFlushDraw(hero, board) && (made >= 2 ? beaten >= (multiway ? 0.10 : 0.15) : true);
            if (!vulnerable)
                return null;
            var streetName = street == "RIVER" ? "river" : "turn";
            return $"Te apuestan fuerte en el {streetName} y tu mano pierde contra muchas ({beaten * 100:0}% de las manos posibles te ganan" +
                   (danger is not null ? $"; {danger.ToLowerInvariant()}" : "") + "): en dinero ficticio quien apuesta así casi siempre la tiene, no ir";
        }

        /// <summary>
        /// Dinero ficticio: si te suben en el turn o el river (sobre tu apuesta o la de otro), solo se resube
        /// con una mano casi imbatible (menos del 2% de las manos posibles le ganan). Esa subida casi
        /// siempre es mano hecha, asi que un color o un set que pierde contra un 4-7% se paga, pero no se
        /// resube all-in. Salvo que ya estes comprometido (pila de 60% del bote o menos). 19-11: color al K
        /// en K-Q-Q con cuatro diamantes (~7% le ganan), el solver nuevo resubia all-in 230 bb contra un
        /// pagador que habia subido turn y river. Devuelve el motivo, o null si se puede resubir.
        /// </summary>
        public static string? NoReRaiseReason(IReadOnlyList<int> hero, IReadOnlyList<int> board, string street,
            double? stackBehind = null, double? pot = null)
        {
            if (street is not ("TURN" or "RIVER") || hero.Count < 2 || board.Count < 4)
                return null;
            if (stackBehind is { } behind && pot is { } total && total > 0 && behind <= total * 0.6)
                return null;
            var beaten = BeatenShare(hero, board);
            if (beaten < 0.02)
                return null;
            var streetName = street == "RIVER" ? "river" : "turn";
            return $"Te suben en el {streetName} y tu mano no es de las mejores ({beaten * 100:0}% de las manos posibles te ganan): " +
                   "en dinero ficticio esa subida casi siempre es mano hecha; se paga, pero no se resube";
        }

        /// <summary>
        /// Cuanta agresion se recomienda en dinero ficticio con esta mano en esta mesa. Ahi casi todo
        /// se paga: los faroles y las subidas con manos medianas pierden, y en mesas de escalera o de
        /// color las manos que parecen buenas van perdiendo contra quien paga todo. Por eso, aunque el
        /// GTO o el solver digan otra cosa: sin mano fuerte (doble pareja real o mejor) no se sube ni
        /// se va all-in; sin pareja ni proyecto fuerte no se apuesta; y en mesa peligrosa ni se sube ni
        /// all-in, y la apuesta es chica.
        /// </summary>
        public static PlayMoneyLimit PlayMoneyAggression(IReadOnlyList<int> hero, IReadOnlyList<int> board, string street,
            double? stackBehind = null, double? pot = null, int opponents = 1)
        {
            // Con dos o mas rivales: apuestas mas chicas con una pareja y ningun semi-farol (alguno paga).
            var multiway = opponents >= 2;
            if (hero.Count < 2 || board.Count < 3)
                return PlayMoneyLimit.None;
            var river = street == "RIVER" || board.Count >= 5;
            var made = HandInsightReader.MadeClass(hero[0], hero[1], board);
            var withDraws = HandInsightReader.StrengthClass(hero[0], hero[1], board);
            // Con muy poco detras (menos de 60% del bote) ir all-in es solo terminar de meter la pila.
            var committed = stackBehind is { } behind && pot is { } total && total > 0 && behind <= total * 0.6;

            var danger = BoardDanger(hero, board);
            if (danger == PlaysBoardText)
                return new PlayMoneyLimit(false, false, 0, $"{danger}: no se apuesta ni se sube (como mucho, empate)");
            if (danger is not null)
            {
                // Sin pareja (ni proyecto fuerte antes del river y mano a mano) tampoco se farolea en una
                // mesa peligrosa: antes esta rama dejaba apostar 35% con as alto en el river (AQ en
                // 5-2-9-6-4), y con el ritmo de la sala ese farol salia "rentable".
                if (made == 0 && (river || multiway || withDraws < 2))
                    return new PlayMoneyLimit(false, false, 0, "Sin pareja ni proyecto fuerte: en dinero ficticio no se farolea, te pagan");
                var max = river ? 0.35 : 0.55;
                return new PlayMoneyLimit(false, false, max,
                    $"{danger}: no se sube ni se va all-in, y se apuesta como mucho {max * 100:0}% del bote");
            }

            // Mano muy fuerte (set, o escalera, color, full o mas con cartas propias y por encima de lo
            // que hace la mesa): sin limite. Trio con una sola carta (mesa doblada) o doble pareja: se
            // apuesta y se sube por valor, pero sin sobreapuestas ni all-in mas grande que el bote;
            // ahi solo paga quien gana (62s en 7-2-2-8-J: all-in de 3 botes, pago Q2, -90 bb).
            if (made >= 3)
            {
                var all = hero.Concat(board).ToArray();
                var score = FastHandEvaluator.Evaluate(all);
                var boardScore = FastHandEvaluator.Evaluate(board.ToArray());
                var category = FastHandEvaluator.Category(score);
                var r0 = TrainerCards.Rank(hero[0]);
                var r1 = TrainerCards.Rank(hero[1]);
                var set = r0 == r1 && board.Any(card => TrainerCards.Rank(card) == r0);
                var monster = set || category >= FastHandEvaluator.Straight && score > boardScore;
                if (monster || committed)
                    return PlayMoneyLimit.None;
                return new PlayMoneyLimit(true, true, 1.0,
                    "Mano fuerte pero no de las mejores (trío con una carta o doble pareja): sin sobreapuestas ni all-in más grande que el bote, solo te paga quien gana",
                    AllInMaxPot: 1.0);
            }

            return made switch
            {
                2 => new PlayMoneyLimit(false, committed, multiway ? 0.6 : 0.75,
                    "Con una pareja (aunque sea top pair) en dinero ficticio no se sube ni se va all-in: te pagan con algo mejor"),
                1 => new PlayMoneyLimit(false, false, multiway ? (river ? 0.25 : 0.4) : river ? 0.35 : 0.55,
                    "Con una pareja floja no se sube ni se va all-in, y se apuesta chico"),
                _ when withDraws >= 2 && !river && !multiway => new PlayMoneyLimit(false, false, 0.55,
                    "Sin pareja (solo proyecto): no se sube ni se va all-in; como mucho, una apuesta de medio bote"),
                _ when withDraws >= 2 && !river => new PlayMoneyLimit(false, false, 0,
                    "Sin pareja y con varios rivales: el semi-farol no funciona (alguno paga); pasa y juega el proyecto por precio"),
                _ => new PlayMoneyLimit(false, false, 0,
                    "Sin pareja ni proyecto fuerte: en dinero ficticio no se farolea, te pagan")
            };
        }

        /// <summary>Mano para subir por valor contra un rango del top X%.</summary>
        public static bool IsValueRaise(string combo, double rangeTop) =>
            PreflopRanking.StrengthTopPercent(combo) <= Math.Max(2.5, rangeTop * 0.25);
    }

    /// <summary>
    /// Limite de agresion en dinero ficticio (ver <see cref="TrainerDecisionRules.PlayMoneyAggression"/>):
    /// si se puede subir, ir all-in y la apuesta mas grande (fraccion del bote; 0 = solo pasar).
    /// </summary>
    public sealed record PlayMoneyLimit(bool CanRaise, bool CanAllIn, double MaxBet, string Why,
        double AllInMaxPot = double.PositiveInfinity)
    {
        public static readonly PlayMoneyLimit None = new(true, true, double.PositiveInfinity, "");

        /// <summary>Si no se debe pagar (apuesta grande en turn o river con una mano que pierde contra muchas): el motivo.</summary>
        public string? NoCall { get; init; }

        public bool Unlimited => CanRaise && CanAllIn && double.IsPositiveInfinity(MaxBet) && double.IsPositiveInfinity(AllInMaxPot);

        /// <summary>
        /// Si la jugada cabe: una subida, un all-in o una apuesta. <paramref name="potFraction"/> es lo
        /// que se mete en la jugada (apuesta, o subida menos lo ya puesto) dividido por el bote.
        /// </summary>
        public bool Allows(bool raise, bool allIn, double potFraction)
        {
            if (raise && !CanRaise || allIn && !CanAllIn)
                return false;
            if (allIn)
                return potFraction <= AllInMaxPot + 0.02;
            return raise || potFraction <= MaxBet + 0.02;
        }
    }
}
