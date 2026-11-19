using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Carta como entero 0..51: rango * 4 + palo. Rango 0 = '2' ... 12 = 'A'; palo 0..3 = c, d, h, s.
    /// El evaluador del motor APH trabaja con LINQ y listas; para el lector en vivo hace falta
    /// simular miles de manos por decision, asi que el entrenador usa esta representacion compacta.
    /// </summary>
    public static class TrainerCards
    {
        public const string Ranks = "23456789TJQKA";
        public const string Suits = "cdhs";

        public static int Rank(int card) => card >> 2;
        public static int Suit(int card) => card & 3;

        public static bool TryParse(string? token, out int card)
        {
            card = -1;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var clean = token.Trim().Trim('[', ']', '(', ')', ',', '.', ';', ':');
            if (clean.StartsWith("10", StringComparison.Ordinal))
                clean = "T" + clean[2..];
            if (clean.Length != 2)
                return false;

            var rank = Ranks.IndexOf(char.ToUpperInvariant(clean[0]));
            var suit = clean[1] switch
            {
                '\u2663' => 0,
                '\u2666' => 1,
                '\u2665' => 2,
                '\u2660' => 3,
                var s => Suits.IndexOf(char.ToLowerInvariant(s))
            };
            if (rank < 0 || suit < 0)
                return false;

            card = rank * 4 + suit;
            return true;
        }

        public static List<int> ParseMany(string? text)
        {
            var cards = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return cards;

            foreach (var token in text.Split(new[] { ' ', ',', '[', ']', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParse(token, out var card) && !cards.Contains(card))
                    cards.Add(card);
            }

            return cards;
        }

        public static string ToText(int card) =>
            card is < 0 or > 51 ? "?" : $"{Ranks[Rank(card)]}{Suits[Suit(card)]}";

        public static string ToText(IEnumerable<int> cards) => string.Join(" ", cards.Select(ToText));

        /// <summary>Nombre de clase preflop: "AKs", "QQ", "T9o" (la carta alta primero).</summary>
        public static string ComboName(int a, int b)
        {
            var high = Math.Max(Rank(a), Rank(b));
            var low = Math.Min(Rank(a), Rank(b));
            if (high == low)
                return $"{Ranks[high]}{Ranks[low]}";
            return $"{Ranks[high]}{Ranks[low]}{(Suit(a) == Suit(b) ? 's' : 'o')}";
        }

        /// <summary>Todas las combinaciones concretas de una clase ("AKs" -> 4, "AKo" -> 12, "QQ" -> 6).</summary>
        public static IEnumerable<(int A, int B)> ExpandClass(string handClass)
        {
            if (handClass.Length < 2)
                yield break;

            var r1 = Ranks.IndexOf(char.ToUpperInvariant(handClass[0]));
            var r2 = Ranks.IndexOf(char.ToUpperInvariant(handClass[1]));
            if (r1 < 0 || r2 < 0)
                yield break;

            if (r1 == r2)
            {
                for (var s1 = 0; s1 < 4; s1++)
                for (var s2 = s1 + 1; s2 < 4; s2++)
                    yield return (r1 * 4 + s1, r2 * 4 + s2);
                yield break;
            }

            var suited = handClass.Length > 2 && char.ToLowerInvariant(handClass[2]) == 's';
            for (var s1 = 0; s1 < 4; s1++)
            for (var s2 = 0; s2 < 4; s2++)
            {
                if (suited != (s1 == s2))
                    continue;
                yield return (r1 * 4 + s1, r2 * 4 + s2);
            }
        }

        public static int ComboCount(string handClass) =>
            handClass.Length == 2 ? 6 : handClass.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? 4 : 12;

        public static IReadOnlyList<string> AllClasses { get; } = BuildAllClasses();

        private static IReadOnlyList<string> BuildAllClasses()
        {
            var result = new List<string>(169);
            for (var high = 12; high >= 0; high--)
            for (var low = high; low >= 0; low--)
            {
                if (high == low)
                {
                    result.Add($"{Ranks[high]}{Ranks[low]}");
                    continue;
                }

                result.Add($"{Ranks[high]}{Ranks[low]}s");
                result.Add($"{Ranks[high]}{Ranks[low]}o");
            }

            return result;
        }
    }

    /// <summary>
    /// Evaluador de 5 a 7 cartas sin asignaciones. Devuelve un entero comparable (mayor = mejor):
    /// categoria en los bits 20-23 y hasta cinco rangos de desempate de 4 bits debajo.
    /// </summary>
    public static class FastHandEvaluator
    {
        public const int HighCard = 0;
        public const int OnePair = 1;
        public const int TwoPair = 2;
        public const int Trips = 3;
        public const int Straight = 4;
        public const int Flush = 5;
        public const int FullHouse = 6;
        public const int Quads = 7;
        public const int StraightFlush = 8;

        public static int Category(int score) => score >> 20;

        public static int Evaluate(ReadOnlySpan<int> cards)
        {
            Span<int> rankCounts = stackalloc int[13];
            Span<int> suitMasks = stackalloc int[4];
            Span<int> suitCounts = stackalloc int[4];
            var rankMask = 0;

            foreach (var card in cards)
            {
                var rank = card >> 2;
                var suit = card & 3;
                rankCounts[rank]++;
                suitMasks[suit] |= 1 << rank;
                suitCounts[suit]++;
                rankMask |= 1 << rank;
            }

            var flushMask = 0;
            for (var suit = 0; suit < 4; suit++)
            {
                if (suitCounts[suit] < 5)
                    continue;

                var straightFlush = StraightHigh(suitMasks[suit]);
                if (straightFlush >= 0)
                    return Pack(StraightFlush, straightFlush);
                flushMask = suitMasks[suit];
            }

            int quad = -1, trips1 = -1, trips2 = -1, pair1 = -1, pair2 = -1;
            for (var rank = 12; rank >= 0; rank--)
            {
                switch (rankCounts[rank])
                {
                    case 4:
                        quad = rank;
                        break;
                    case 3:
                        if (trips1 < 0) trips1 = rank;
                        else if (trips2 < 0) trips2 = rank;
                        break;
                    case 2:
                        if (pair1 < 0) pair1 = rank;
                        else if (pair2 < 0) pair2 = rank;
                        break;
                }
            }

            if (quad >= 0)
                return Pack(Quads, quad, TopRanks(rankMask & ~(1 << quad), 1));

            if (trips1 >= 0 && (trips2 >= 0 || pair1 >= 0))
                return Pack(FullHouse, trips1, Math.Max(trips2, pair1));

            if (flushMask != 0)
                return PackMask(Flush, flushMask, 5);

            var straight = StraightHigh(rankMask);
            if (straight >= 0)
                return Pack(Straight, straight);

            if (trips1 >= 0)
                return PackWithKickers(Trips, rankMask & ~(1 << trips1), 2, trips1);

            if (pair1 >= 0 && pair2 >= 0)
                return PackWithKickers(TwoPair, rankMask & ~(1 << pair1) & ~(1 << pair2), 1, pair1, pair2);

            if (pair1 >= 0)
                return PackWithKickers(OnePair, rankMask & ~(1 << pair1), 3, pair1);

            return PackMask(HighCard, rankMask, 5);
        }

        public static int StraightHigh(int mask)
        {
            for (var high = 12; high >= 4; high--)
            {
                var window = 0x1F << (high - 4);
                if ((mask & window) == window)
                    return high;
            }

            const int wheel = (1 << 12) | 0xF;
            return (mask & wheel) == wheel ? 3 : -1;
        }

        private static int Pack(int category, int first, int second = 0) =>
            (category << 20) | (first << 16) | (second << 12);

        private static int PackMask(int category, int mask, int count) =>
            (category << 20) | (TopRanks(mask, count) << (4 * (5 - count)));

        private static int PackWithKickers(int category, int kickerMask, int kickerCount, int first, int second = -1)
        {
            var value = category;
            value = (value << 4) | first;
            var used = 1;
            if (second >= 0)
            {
                value = (value << 4) | second;
                used++;
            }

            value = (value << (4 * kickerCount)) | TopRanks(kickerMask, kickerCount);
            used += kickerCount;
            return value << (4 * (5 - used));
        }

        /// <summary>Los <paramref name="count"/> rangos mas altos de la mascara, empaquetados de 4 en 4 bits.</summary>
        private static int TopRanks(int mask, int count)
        {
            var value = 0;
            var taken = 0;
            for (var rank = 12; rank >= 0 && taken < count; rank--)
            {
                if ((mask & (1 << rank)) == 0)
                    continue;
                value = (value << 4) | rank;
                taken++;
            }

            return value << (4 * (count - taken));
        }
    }

    /// <summary>Combinacion concreta con peso (para rangos estrechados por la accion).</summary>
    public readonly record struct WeightedCombo(int A, int B, double Weight);

    public static class EquityCalculator
    {
        /// <summary>
        /// Equity del heroe contra un rango ponderado. En el river enumera el rango entero; antes,
        /// Monte Carlo con semilla fija para que la misma situacion de siempre el mismo numero.
        /// </summary>
        public static double VsRange(IReadOnlyList<int> hero, IReadOnlyList<int> board, IReadOnlyList<WeightedCombo> range, int iterations = 4000)
        {
            if (hero.Count < 2)
                return 0;

            var dead = 0L;
            foreach (var card in hero) dead |= 1L << card;
            foreach (var card in board) dead |= 1L << card;

            var live = range.Where(combo =>
                    combo.Weight > 0 &&
                    (dead & (1L << combo.A)) == 0 &&
                    (dead & (1L << combo.B)) == 0)
                .ToList();
            if (live.Count == 0)
                return VsRandom(hero, board, iterations);

            Span<int> heroCards = stackalloc int[7];
            Span<int> villainCards = stackalloc int[7];
            heroCards[0] = hero[0];
            heroCards[1] = hero[1];
            for (var i = 0; i < board.Count; i++)
            {
                heroCards[2 + i] = board[i];
                villainCards[2 + i] = board[i];
            }

            var missing = 5 - board.Count;
            if (missing <= 0)
            {
                var heroScore = FastHandEvaluator.Evaluate(heroCards[..(2 + board.Count)]);
                double wins = 0, total = 0;
                foreach (var combo in live)
                {
                    villainCards[0] = combo.A;
                    villainCards[1] = combo.B;
                    var villainScore = FastHandEvaluator.Evaluate(villainCards[..(2 + board.Count)]);
                    wins += combo.Weight * (heroScore > villainScore ? 1 : heroScore == villainScore ? 0.5 : 0);
                    total += combo.Weight;
                }

                return total <= 0 ? 0 : wins * 100.0 / total;
            }

            var cumulative = new double[live.Count];
            var sum = 0.0;
            for (var i = 0; i < live.Count; i++)
            {
                sum += live[i].Weight;
                cumulative[i] = sum;
            }

            var random = new Random(Seed(hero, board));
            var deck = new int[52];
            double score = 0;
            var samples = 0;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var pick = Array.BinarySearch(cumulative, random.NextDouble() * sum);
                if (pick < 0) pick = ~pick;
                if (pick >= live.Count) pick = live.Count - 1;
                var villain = live[pick];

                var used = dead | (1L << villain.A) | (1L << villain.B);
                var deckCount = 0;
                for (var card = 0; card < 52; card++)
                {
                    if ((used & (1L << card)) == 0)
                        deck[deckCount++] = card;
                }

                for (var i = 0; i < missing; i++)
                {
                    var j = i + random.Next(deckCount - i);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                    heroCards[2 + board.Count + i] = deck[i];
                    villainCards[2 + board.Count + i] = deck[i];
                }

                villainCards[0] = villain.A;
                villainCards[1] = villain.B;
                var heroScore = FastHandEvaluator.Evaluate(heroCards);
                var villainScore = FastHandEvaluator.Evaluate(villainCards);
                score += heroScore > villainScore ? 1 : heroScore == villainScore ? 0.5 : 0;
                samples++;
            }

            return samples == 0 ? 0 : score * 100.0 / samples;
        }

        public static double VsRandom(IReadOnlyList<int> hero, IReadOnlyList<int> board, int iterations = 4000) =>
            VsRange(hero, board, HandRange.Any.Combos, iterations);

        /// <summary>
        /// Equity multiway: en cada simulacion se reparte una mano a cada rival desde su rango (sin
        /// cartas repetidas) y la misma runout para todos; el heroe gana si supera a todos y reparte
        /// en empates. Multiplicar las equities uno contra uno no vale: si el heroe liga, les gana
        /// a todos a la vez, y el producto lo hundia (T4s en un bote de 5 salia con 0,4%).
        /// </summary>
        public static double VsRanges(
            IReadOnlyList<int> hero,
            IReadOnlyList<int> board,
            IReadOnlyList<IReadOnlyList<WeightedCombo>> ranges,
            int iterations = 4000)
        {
            if (hero.Count < 2)
                return 0;
            if (ranges.Count == 0)
                return 100;
            if (ranges.Count == 1)
                return VsRange(hero, board, ranges[0], iterations);

            var dead = 0L;
            foreach (var card in hero) dead |= 1L << card;
            foreach (var card in board) dead |= 1L << card;

            var pools = new List<(WeightedCombo[] Combos, double[] Cumulative, double Total)>();
            foreach (var range in ranges)
            {
                var live = range.Where(combo =>
                        combo.Weight > 0 &&
                        (dead & (1L << combo.A)) == 0 &&
                        (dead & (1L << combo.B)) == 0)
                    .ToArray();
                if (live.Length == 0)
                    live = HandRange.Any.Combos.Where(combo => (dead & (1L << combo.A)) == 0 && (dead & (1L << combo.B)) == 0).ToArray();

                var cumulative = new double[live.Length];
                var total = 0.0;
                for (var i = 0; i < live.Length; i++)
                {
                    total += live[i].Weight;
                    cumulative[i] = total;
                }

                pools.Add((live, cumulative, total));
            }

            var random = new Random(Seed(hero, board) ^ ranges.Count);
            var deck = new int[52];
            var villainHands = new (int A, int B)[pools.Count];
            Span<int> cards = stackalloc int[7];
            var missing = 5 - board.Count;
            double score = 0;
            var samples = 0;

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var used = dead;
                var dealt = true;
                for (var v = 0; v < pools.Count && dealt; v++)
                {
                    var pool = pools[v];
                    dealt = false;
                    // Muestreo con rechazo: si la mano choca con otra ya repartida se prueba otra.
                    for (var attempt = 0; attempt < 24; attempt++)
                    {
                        var pick = Array.BinarySearch(pool.Cumulative, random.NextDouble() * pool.Total);
                        if (pick < 0) pick = ~pick;
                        if (pick >= pool.Combos.Length) pick = pool.Combos.Length - 1;
                        var combo = pool.Combos[pick];
                        var mask = (1L << combo.A) | (1L << combo.B);
                        if ((used & mask) != 0)
                            continue;

                        used |= mask;
                        villainHands[v] = (combo.A, combo.B);
                        dealt = true;
                        break;
                    }
                }

                if (!dealt)
                    continue;

                var deckCount = 0;
                for (var card = 0; card < 52; card++)
                {
                    if ((used & (1L << card)) == 0)
                        deck[deckCount++] = card;
                }

                for (var i = 0; i < board.Count; i++)
                    cards[2 + i] = board[i];
                for (var i = 0; i < missing; i++)
                {
                    var j = i + random.Next(deckCount - i);
                    (deck[i], deck[j]) = (deck[j], deck[i]);
                    cards[2 + board.Count + i] = deck[i];
                }

                cards[0] = hero[0];
                cards[1] = hero[1];
                var heroScore = FastHandEvaluator.Evaluate(cards);
                var best = int.MinValue;
                var tiedAtBest = 0;
                foreach (var (a, b) in villainHands)
                {
                    cards[0] = a;
                    cards[1] = b;
                    var villainScore = FastHandEvaluator.Evaluate(cards);
                    if (villainScore > best)
                    {
                        best = villainScore;
                        tiedAtBest = 1;
                    }
                    else if (villainScore == best)
                    {
                        tiedAtBest++;
                    }
                }

                if (heroScore > best)
                    score += 1;
                else if (heroScore == best)
                    score += 1.0 / (tiedAtBest + 1);
                samples++;
            }

            return samples == 0 ? 0 : score * 100.0 / samples;
        }

        private static int Seed(IReadOnlyList<int> hero, IReadOnlyList<int> board)
        {
            unchecked
            {
                var seed = 17;
                foreach (var card in hero) seed = seed * 31 + card;
                foreach (var card in board) seed = seed * 31 + card + 7;
                return seed;
            }
        }
    }

    /// <summary>Rango como lista de combinaciones ponderadas, con su descripcion para el alumno.</summary>
    public sealed class HandRange
    {
        public HandRange(string description, IEnumerable<WeightedCombo> combos)
        {
            Description = description;
            Combos = combos.ToList();
        }

        public string Description { get; }
        public IReadOnlyList<WeightedCombo> Combos { get; }

        public static HandRange Any { get; } = FromClasses("cualquier mano", TrainerCards.AllClasses);

        public static HandRange FromClasses(string description, IEnumerable<string> classes) =>
            new(description, classes.SelectMany(TrainerCards.ExpandClass).Select(combo => new WeightedCombo(combo.A, combo.B, 1)));

        public HandRange WithWeights(string description, Func<WeightedCombo, double> factor) =>
            new(description, Combos.Select(combo => combo with { Weight = combo.Weight * factor(combo) }));
    }

    /// <summary>
    /// Orden de las 169 manos iniciales. Las equities estan precalculadas con los mismos repartos
    /// para todas las manos (400.000 contra una mano aleatoria y 150.000 contra el top 8%): con pocas
    /// simulaciones al arrancar, el ruido ponia por ejemplo A8o por delante de A8s. Sirve para los
    /// rangos "top X%" y, contra subidas fuertes, para el orden de manos premium.
    /// </summary>
    public static class PreflopRanking
    {
        private const string VsRandomTable =
            "AA:85.22 KK:82.35 QQ:79.87 JJ:77.48 TT:75.00 99:72.01 88:69.18 AKs:67.08 77:66.24 AQs:66.19 AJs:65.45 AKo:65.34 " +
            "ATs:64.63 AQo:64.40 AJo:63.58 KQs:63.36 66:63.31 A9s:62.83 ATo:62.81 KJs:62.59 A8s:62.04 KTs:61.82 KQo:61.44 A7s:61.08 " +
            "A9o:60.86 KJo:60.57 55:60.44 QJs:60.26 K9s:60.04 A5s:60.03 A6s:59.98 A8o:59.97 KTo:59.89 QTs:59.49 A4s:59.05 A7o:58.96 " +
            "K8s:58.32 A3s:58.29 QJo:58.06 K9o:57.88 A6o:57.77 A5o:57.70 JTs:57.65 K7s:57.65 Q9s:57.58 A2s:57.43 QTo:57.37 44:57.07 " +
            "A4o:56.77 K6s:56.69 Q8s:56.12 K8o:56.03 K5s:55.91 A3o:55.87 J9s:55.69 JTo:55.45 K7o:55.32 Q9o:55.29 A2o:55.05 K4s:54.84 " +
            "Q7s:54.31 K6o:54.27 K3s:54.12 J8s:54.08 T9s:54.07 33:53.86 Q6s:53.73 Q8o:53.62 J9o:53.32 K5o:53.28 K2s:53.21 Q5s:52.95 " +
            "T8s:52.41 J7s:52.41 K4o:52.26 Q4s:51.88 Q7o:51.77 J8o:51.57 T9o:51.55 K3o:51.43 Q3s:51.12 Q6o:51.07 98s:50.88 J6s:50.79 " +
            "T7s:50.71 K2o:50.58 22:50.43 Q2s:50.20 J5s:50.18 Q5o:50.16 J7o:49.80 T8o:49.78 97s:49.20 Q4o:49.15 J4s:49.10 T6s:49.06 " +
            "J3s:48.34 Q3o:48.31 98o:48.11 87s:48.00 T7o:47.99 J6o:47.98 96s:47.51 T5s:47.47 J2s:47.38 Q2o:47.37 J5o:47.29 T4s:46.59 " +
            "86s:46.39 97o:46.30 T6o:46.21 J4o:46.15 T3s:45.89 95s:45.84 76s:45.42 J3o:45.31 87o:45.09 T2s:44.90 85s:44.79 96o:44.49 " +
            "J2o:44.43 T5o:44.36 94s:43.81 75s:43.80 T4o:43.53 65s:43.33 86o:43.32 93s:43.29 84s:42.79 T3o:42.66 95o:42.65 76o:42.30 " +
            "92s:42.29 74s:41.92 T2o:41.73 54s:41.60 85o:41.50 64s:41.50 83s:40.98 75o:40.59 94o:40.56 82s:40.36 73s:40.14 65o:40.01 " +
            "93o:40.00 53s:39.92 63s:39.61 84o:39.47 92o:39.05 43s:38.76 74o:38.59 54o:38.23 72s:38.20 64o:38.07 52s:37.94 62s:37.71 " +
            "83o:37.54 82o:36.91 42s:36.83 73o:36.64 53o:36.41 32s:36.10 63o:36.06 43o:35.16 72o:34.62 52o:34.40 62o:34.16 42o:33.22 " +
            "32o:32.38";

        private const string VsStrongTable =
            "AA:83.83 KK:71.67 QQ:64.70 JJ:58.03 AKs:54.81 TT:53.16 AKo:52.68 99:48.85 AQs:47.75 88:45.19 AQo:45.07 77:41.41 " +
            "AJs:40.49 KQs:38.48 66:37.78 AJo:37.19 KTs:36.89 ATs:36.82 QTs:36.65 55:36.56 JTs:36.46 KJs:36.41 QJs:36.33 44:36.03 " +
            "T9s:35.95 33:35.65 22:35.29 K9s:35.12 Q9s:35.03 KQo:35.00 J9s:34.83 98s:34.70 A9s:34.53 T8s:34.09 A8s:33.73 K8s:33.71 " +
            "KTo:33.42 87s:33.38 Q8s:33.32 J8s:33.30 A5s:33.19 QTo:33.17 97s:33.16 K7s:33.10 KJo:33.09 ATo:33.04 QJo:33.01 A7s:32.99 " +
            "JTo:32.96 A4s:32.61 T7s:32.51 A3s:32.47 T9o:32.38 K5s:32.24 K6s:32.13 A2s:31.97 A6s:31.91 76s:31.80 K4s:31.78 Q7s:31.68 " +
            "86s:31.52 K3s:31.48 K9o:31.46 Q9o:31.44 J7s:31.41 J9o:31.21 96s:31.21 K2s:31.16 98o:31.13 Q6s:31.02 65s:31.01 Q5s:30.99 " +
            "54s:30.96 75s:30.92 85s:30.78 A9o:30.64 T6s:30.61 95s:30.55 T8o:30.51 Q4s:30.46 Q3s:30.27 A8o:30.02 J5s:29.94 K8o:29.91 " +
            "T5s:29.87 64s:29.86 87o:29.84 Q2s:29.83 53s:29.77 Q8o:29.69 97o:29.64 A5o:29.57 T4s:29.54 J6s:29.53 J8o:29.52 J4s:29.48 " +
            "K7o:29.40 74s:29.31 43s:29.28 T3s:29.26 J3s:29.18 A7o:29.15 84s:29.10 93s:29.03 94s:29.00 A4o:28.99 T2s:28.86 T7o:28.85 " +
            "A3o:28.82 J2s:28.79 92s:28.70 K6o:28.64 63s:28.53 K5o:28.49 76o:28.42 73s:28.33 A2o:28.26 52s:28.23 A6o:28.23 86o:28.15 " +
            "K4o:28.13 83s:28.08 82s:27.91 Q7o:27.87 42s:27.86 K3o:27.78 J7o:27.72 96o:27.70 32s:27.69 54o:27.62 Q6o:27.46 65o:27.44 " +
            "Q5o:27.32 75o:27.31 K2o:27.26 85o:27.13 62s:27.09 T6o:27.08 95o:26.95 Q4o:26.88 72s:26.77 Q3o:26.62 64o:26.27 53o:26.24 " +
            "J5o:26.22 T5o:26.12 T4o:26.00 Q2o:25.98 J6o:25.95 J4o:25.83 74o:25.77 43o:25.76 T3o:25.58 84o:25.51 J3o:25.51 93o:25.35 " +
            "94o:25.30 T2o:24.98 63o:24.92 J2o:24.85 92o:24.77 73o:24.59 52o:24.49 83o:24.33 42o:24.11 82o:24.07 32o:23.94 62o:23.23 " +
            "72o:22.83";

        private static readonly Lazy<IReadOnlyList<(string Class, double Equity)>> Ordered = new(() => Parse(VsRandomTable));
        private static readonly Lazy<IReadOnlyList<string>> StrongOrdered = new(() => Parse(VsStrongTable).Select(item => item.Class).ToList());

        public static IReadOnlyList<(string Class, double Equity)> All => Ordered.Value;

        public static double EquityVsRandom(string handClass) =>
            All.FirstOrDefault(item => item.Class == handClass).Equity;

        /// <summary>Percentil de la mano: 2 = esta en el 2% mejor de las 1326 combinaciones.</summary>
        public static double TopPercentOf(string handClass)
        {
            var combos = 0;
            foreach (var (name, _) in All)
            {
                combos += TrainerCards.ComboCount(name);
                if (name == handClass)
                    return combos * 100.0 / 1326;
            }

            return 100;
        }

        public static IReadOnlyList<string> TopPercent(double percent, double skipTopPercent = 0)
        {
            var maxCombos = Math.Clamp(percent, 0.5, 100) / 100.0 * 1326;
            var skipCombos = Math.Clamp(skipTopPercent, 0, 99) / 100.0 * 1326;
            var result = new List<string>();
            var combos = 0;
            foreach (var (name, _) in All)
            {
                var before = combos;
                combos += TrainerCards.ComboCount(name);
                if (before < skipCombos)
                    continue;
                if (before >= maxCombos)
                    break;
                result.Add(name);
            }

            return result;
        }

        /// <summary>
        /// Rango estrecho (4bet, all-in profundo, varias subidas) en orden de fuerza contra manos
        /// fuertes. Contra una mano aleatoria las parejas pequenas y los ases debiles suben de mas;
        /// contra quien sube fuerte AKo va por delante de 77 y AJo por delante de 55.
        /// </summary>
        public static IReadOnlyList<string> StrengthOrder(double percent)
        {
            var maxCombos = Math.Clamp(percent, 0.5, 100) / 100.0 * 1326;
            var result = new List<string>();
            var combos = 0;
            foreach (var name in StrongOrdered.Value)
            {
                if (combos >= maxCombos)
                    break;
                combos += TrainerCards.ComboCount(name);
                result.Add(name);
            }

            return result;
        }

        /// <summary>Percentil de la mano en el orden de fuerza contra manos fuertes.</summary>
        public static double StrengthTopPercent(string handClass)
        {
            var combos = 0;
            foreach (var name in StrongOrdered.Value)
            {
                combos += TrainerCards.ComboCount(name);
                if (name == handClass)
                    return combos * 100.0 / 1326;
            }

            return 100;
        }

        private static IReadOnlyList<(string Class, double Equity)> Parse(string table) =>
            table.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split(':'))
                .Select(parts => (parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture)))
                .OrderByDescending(item => item.Item2)
                .ToList();
    }
}
