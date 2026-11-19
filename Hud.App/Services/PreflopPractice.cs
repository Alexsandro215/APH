using System.Globalization;
using System.IO;
using System.Text.Json;
using Hud.App.Services.Solver;
using S = Hud.App.Services.PreflopScenarios;

namespace Hud.App.Services
{
    public enum PracticeLevel { Easy, Medium, Hard, Expert }

    /// <summary>Un tema de practica: que situaciones entran (por ejemplo "abrir desde UTG").</summary>
    public sealed record PracticeTopic(string Id, string NameKey, string DescKey, Func<(string Scenario, string Hero, string Villain), bool>? Filter);

    /// <summary>Una pregunta: la situacion, la mano y la respuesta de la tabla.</summary>
    public sealed record PracticeQuestion(
        string TopicId,
        PracticeLevel Level,
        string Scenario,
        string Hero,
        string Villain,
        string Caller,
        string Hand,
        string Cards,
        string Correct,
        IReadOnlyList<string> Options,
        int Distance,
        PreflopSpot? RealSpot,
        double Stack = 0); // solo en all-in o retirarse: ciegas grandes de la pila efectiva

    public sealed record PracticeMistake(string Scenario, string Hero, string Villain, string Hand, double Stack = 0);

    /// <summary>
    /// Practica preflop: genera preguntas por tema y nivel a partir de las tablas de APH y de las
    /// manos reales de los jugadores, y explica cada respuesta.
    ///
    /// La dificultad sale de la propia tabla: una mano "facil" esta lejos del borde del rango
    /// (todas las manos vecinas se juegan igual, como AA o 72o) y una "dificil" esta en el borde
    /// (una casilla vecina ya se juega distinto, como ATo o KJo segun la posicion).
    /// </summary>
    public sealed class PreflopPracticeEngine
    {
        public const string MixTopic = "mix";
        public const string MyHandsTopic = "my-hands";
        public const string MistakesTopic = "mistakes";
        public const string PushFoldTopic = "push-fold";

        // Pilas de las preguntas de all-in o retirarse (en ciegas grandes).
        private static readonly double[] PushFoldStacks = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20 };

        public static readonly IReadOnlyList<PracticeTopic> Topics = new PracticeTopic[]
        {
            new("open-LJ", "Practice.Topic.OpenLJ", "Practice.Topic.OpenLJ.Desc", c => c.Scenario == S.Open && c.Hero == "LJ"),
            new("open-HJ", "Practice.Topic.OpenHJ", "Practice.Topic.OpenHJ.Desc", c => c.Scenario == S.Open && c.Hero == "HJ"),
            new("open-CO", "Practice.Topic.OpenCO", "Practice.Topic.OpenCO.Desc", c => c.Scenario == S.Open && c.Hero == "CO"),
            new("open-BTN", "Practice.Topic.OpenBTN", "Practice.Topic.OpenBTN.Desc", c => c.Scenario == S.Open && c.Hero == "BTN"),
            new("open-SB", "Practice.Topic.OpenSB", "Practice.Topic.OpenSB.Desc", c => c.Scenario == S.Open && c.Hero == "SB"),
            new("bb-defense", "Practice.Topic.BbDefense", "Practice.Topic.BbDefense.Desc", c => c.Scenario == S.VsRaise && c.Hero == "BB"),
            new("vs-raise", "Practice.Topic.VsRaise", "Practice.Topic.VsRaise.Desc", c => c.Scenario == S.VsRaise),
            new("vs-limp", "Practice.Topic.VsLimp", "Practice.Topic.VsLimp.Desc", c => c.Scenario == S.VsLimp),
            new("squeeze", "Practice.Topic.Squeeze", "Practice.Topic.Squeeze.Desc", c => c.Scenario == S.Squeeze),
            new("vs-3bet", "Practice.Topic.Vs3Bet", "Practice.Topic.Vs3Bet.Desc", c => c.Scenario == S.Vs3Bet),
            new("vs-4bet", "Practice.Topic.Vs4Bet", "Practice.Topic.Vs4Bet.Desc", c => c.Scenario is S.Vs4Bet or S.Vs5Bet),
            new("heads-up", "Practice.Topic.HeadsUp", "Practice.Topic.HeadsUp.Desc", c => S.IsHeadsUp(c.Scenario)),
            new(PushFoldTopic, "Practice.Topic.PushFold", "Practice.Topic.PushFold.Desc", null),
            new(MixTopic, "Practice.Topic.Mix", "Practice.Topic.Mix.Desc", c => !S.IsHeadsUp(c.Scenario)),
            new(MyHandsTopic, "Practice.Topic.MyHands", "Practice.Topic.MyHands.Desc", null),
            new(MistakesTopic, "Practice.Topic.Mistakes", "Practice.Topic.Mistakes.Desc", null),
        };

        // Si no hay manos propias: frecuencia aproximada de cada situacion en mesas de 6 con
        // muchos limps (como las de dinero ficticio de los alumnos).
        private static readonly Dictionary<string, double> DefaultMix = new()
        {
            [S.Open] = 34, [S.VsLimp] = 30, [S.VsRaise] = 18, [S.Squeeze] = 6, [S.Vs3Bet] = 8, [S.Vs4Bet] = 3, [S.Vs5Bet] = 1
        };

        private readonly AphPreflopProvider _charts;
        private readonly IReadOnlyList<PreflopSpot> _spots;
        private readonly Random _random;
        private readonly List<(string Scenario, string Hero, string Villain)> _available;
        private readonly PushFoldSolver? _solver;

        public PreflopPracticeEngine(AphPreflopProvider charts, IReadOnlyList<PreflopSpot> spots, Random? random = null, PushFoldSolver? solver = null)
        {
            _charts = charts;
            _random = random ?? new Random();
            _solver = solver ?? PushFoldSolver.Default;
            _available = charts.AvailableCharts().ToList();
            _spots = spots.Where(s => charts.HasChart(s.Scenario, s.Hero, s.Villain)).ToList();
        }

        public int RealSpotCount => _spots.Count;

        /// <summary>Cuantas veces aparece cada situacion en las manos reales (para "Mezcla").</summary>
        public IReadOnlyDictionary<string, double> MixWeights(PracticeLevel level)
        {
            var weights = _spots.Count >= 20
                ? _spots.GroupBy(s => s.Scenario).Where(g => !S.IsHeadsUp(g.Key)).ToDictionary(g => g.Key, g => (double)g.Count())
                : new Dictionary<string, double>(DefaultMix);
            foreach (var scenario in S.SixMax)
                weights.TryAdd(scenario, 0);
            // En experto las situaciones raras salen mas: ahi es donde se aprende.
            if (level == PracticeLevel.Expert)
                foreach (var key in weights.Keys.ToList())
                    weights[key] = Math.Sqrt(weights[key]) + 2;
            else
                foreach (var key in weights.Keys.ToList())
                    weights[key] = Math.Max(weights[key], 0.5);
            return weights;
        }

        public PracticeQuestion? Next(string topicId, PracticeLevel level, IReadOnlyList<PracticeMistake>? mistakes = null)
        {
            if (topicId == MyHandsTopic)
                return FromRealHands(level);
            if (topicId == MistakesTopic)
                return FromMistakes(mistakes ?? Array.Empty<PracticeMistake>());
            if (topicId == PushFoldTopic)
                return PushFoldQuestion(level);

            var topic = Topics.FirstOrDefault(t => t.Id == topicId);
            if (topic?.Filter is null)
                return null;

            var charts = _available.Where(topic.Filter).ToList();
            if (charts.Count == 0)
                return null;

            (string Scenario, string Hero, string Villain) chart;
            if (topicId == MixTopic)
            {
                var weights = MixWeights(level);
                var scenario = Pick(weights.Where(w => charts.Any(c => c.Scenario == w.Key)).ToList());
                var options = charts.Where(c => c.Scenario == scenario).ToList();
                chart = options[_random.Next(options.Count)];
            }
            else
            {
                chart = charts[_random.Next(charts.Count)];
            }

            var (hand, distance) = PickHand(chart, level);
            return Build(topicId, level, chart, hand, distance, null);
        }

        private PracticeQuestion? FromRealHands(PracticeLevel level)
        {
            if (_spots.Count == 0)
                return null;

            // En los niveles faciles se eligen manos claras; en los dificiles, las del borde.
            var scored = _spots.Select(s => (Spot: s, Distance: Distance(s.Scenario, s.Hero, s.Villain, s.Hand))).ToList();
            var wanted = scored.Where(x => FitsLevel(x.Distance, level)).ToList();
            var pool = wanted.Count >= 5 ? wanted : scored;
            var pick = pool[_random.Next(pool.Count)];
            return Build(MyHandsTopic, level, (pick.Spot.Scenario, pick.Spot.Hero, pick.Spot.Villain), pick.Spot.Hand, pick.Distance, pick.Spot);
        }

        private PracticeQuestion? FromMistakes(IReadOnlyList<PracticeMistake> mistakes)
        {
            var valid = mistakes.Where(m => m.Scenario == S.HuAllIn
                ? _solver is not null && m.Stack >= PushFoldSolver.MinStack
                : _charts.HasChart(m.Scenario, m.Hero, m.Villain)).ToList();
            if (valid.Count == 0)
                return null;
            var mistake = valid[_random.Next(valid.Count)];
            if (mistake.Scenario == S.HuAllIn)
                return BuildPushFold(MistakesTopic, PracticeLevel.Medium, mistake.Stack, mistake.Hero, mistake.Hand);
            var chart = (mistake.Scenario, mistake.Hero, mistake.Villain);
            return Build(MistakesTopic, PracticeLevel.Medium, chart, mistake.Hand, Distance(mistake.Scenario, mistake.Hero, mistake.Villain, mistake.Hand), null);
        }

        private PracticeQuestion Build(string topicId, PracticeLevel level, (string Scenario, string Hero, string Villain) chart,
            string hand, int distance, PreflopSpot? real)
        {
            var correct = Code(chart, hand);
            var options = new List<string> { "F" };
            // "Pagar" solo se ofrece si la tabla lo usa en esa situacion (al abrir no existe).
            if (Enumerable.Range(0, 169).Any(i => Code(chart, AphPreflopProvider.HandAt(i / 13, i % 13)) == "C"))
                options.Add("C");
            options.Add("R");

            return new PracticeQuestion(topicId, level, chart.Scenario, chart.Hero, chart.Villain, Caller(chart),
                hand, real?.Cards ?? RandomCards(hand), correct, options, distance, real);
        }

        private string Code((string Scenario, string Hero, string Villain) chart, string hand) =>
            _charts.GetCode(chart.Scenario, chart.Hero, chart.Villain, hand) ?? "F";

        /// <summary>
        /// Elige primero la accion correcta (repartidas entre subir, pagar y retirarse, para que no
        /// sea casi siempre "retirarse") y despues una mano de esa accion segun el nivel.
        /// </summary>
        private (string Hand, int Distance) PickHand((string Scenario, string Hero, string Villain) chart, PracticeLevel level)
        {
            var cells = Enumerable.Range(0, 169)
                .Select(i => (Hand: AphPreflopProvider.HandAt(i / 13, i % 13), Row: i / 13, Col: i % 13))
                .Select(c => (c.Hand, Code: Code(chart, c.Hand), Distance: Distance(chart.Scenario, chart.Hero, chart.Villain, c.Row, c.Col)))
                .ToList();

            var codes = cells.Select(c => c.Code).Distinct().ToList();
            var code = codes[_random.Next(codes.Count)];
            var sameAction = cells.Where(c => c.Code == code).ToList();
            var fitting = sameAction.Where(c => FitsLevel(c.Distance, level)).ToList();
            if (fitting.Count == 0)
                fitting = cells.Where(c => FitsLevel(c.Distance, level)).ToList();
            if (fitting.Count == 0)
                fitting = sameAction;

            var pick = fitting[_random.Next(fitting.Count)];
            return (pick.Hand, pick.Distance);
        }

        // ---------------------------------------------------------------- all-in o retirarse

        /// <summary>
        /// Pregunta de all-in o retirarse en heads-up: una pila al azar, boton (hacer all-in) o
        /// ciega grande (pagarlo), y la respuesta del solver. La dificultad es cuanto cuesta
        /// equivocarse: facil si la mala jugada pierde una ciega o mas, dificil si casi da igual.
        /// Las manos que el equilibrio mezcla (a veces si, a veces no) no se preguntan.
        /// </summary>
        private PracticeQuestion? PushFoldQuestion(PracticeLevel level)
        {
            if (_solver is null)
                return null;

            for (var attempt = 0; attempt < 12; attempt++)
            {
                var stack = PushFoldStacks[_random.Next(PushFoldStacks.Length)];
                var hero = _random.Next(2) == 0 ? "SB" : "BB";
                var solution = _solver.Solve(stack);
                var cells = Enumerable.Range(0, PreflopEquityMatrix.Size)
                    .Select(i => (Hand: PreflopEquityMatrix.HandAt(i),
                        Frequency: hero == "SB" ? solution.Push[i] : solution.Call[i],
                        Gain: hero == "SB" ? solution.PushGain[i] : solution.CallGain[i]))
                    .Where(c => c.Frequency <= 0.1 || c.Frequency >= 0.9)
                    .Select(c => (c.Hand, Code: c.Frequency >= 0.5 ? (hero == "SB" ? "R" : "C") : "F", Distance: PushFoldDistance(c.Gain)))
                    .ToList();

                // Primero la respuesta (mitad y mitad) y despues una mano de ese nivel.
                var codes = cells.Select(c => c.Code).Distinct().ToList();
                var code = codes[_random.Next(codes.Count)];
                var fitting = cells.Where(c => c.Code == code && FitsLevel(c.Distance, level)).ToList();
                if (fitting.Count == 0)
                    fitting = cells.Where(c => FitsLevel(c.Distance, level)).ToList();
                if (fitting.Count == 0)
                    continue;

                var pick = fitting[_random.Next(fitting.Count)];
                return BuildPushFold(PushFoldTopic, level, stack, hero, pick.Hand);
            }

            return null;
        }

        private PracticeQuestion? BuildPushFold(string topicId, PracticeLevel level, double stack, string hero, string hand)
        {
            if (_solver is null)
                return null;
            var solution = _solver.Solve(stack);
            var button = hero == "SB";
            var correct = button ? solution.ButtonCode(hand) : solution.BigBlindCode(hand);
            var gain = button ? solution.PushGainOf(hand) : solution.CallGainOf(hand);
            var options = button ? new[] { "F", "R" } : new[] { "F", "C" };
            return new PracticeQuestion(topicId, level, S.HuAllIn, hero, "", "", hand, RandomCards(hand), correct, options,
                PushFoldDistance(gain), null, stack);
        }

        /// <summary>3 = equivocarse cuesta una ciega o mas por mano; 2 = de 0,3 a 1; 1 = menos de 0,3.</summary>
        public static int PushFoldDistance(double gain) => Math.Abs(gain) >= 1 ? 3 : Math.Abs(gain) >= 0.3 ? 2 : 1;

        /// <summary>Accion correcta de una mano en la tabla de la pregunta (para dibujarla).</summary>
        public string CodeAt(PracticeQuestion q, string hand)
        {
            if (q.Scenario != S.HuAllIn)
                return Code((q.Scenario, q.Hero, q.Villain), hand);
            if (_solver is null)
                return "F";
            var solution = _solver.Solve(q.Stack);
            return q.Hero == "SB" ? solution.ButtonCode(hand) : solution.BigBlindCode(hand);
        }

        public static string ChartTitle(PracticeQuestion q) => q.Scenario == S.HuAllIn
            ? Format(q.Hero == "SB" ? "PushFold.ChartTitle.SB" : "PushFold.ChartTitle.BB", q.Stack)
            : S.ChartTitle(q.Scenario, q.Hero, q.Villain) + (S.IsDraft(q.Scenario) ? " · " + T("Preflop.Draft") : "");

        public static bool FitsLevel(int distance, PracticeLevel level) => level switch
        {
            PracticeLevel.Easy => distance >= 3,
            PracticeLevel.Medium => distance == 2,
            _ => distance == 1
        };

        public int Distance(string scenario, string hero, string villain, string hand)
        {
            for (var row = 0; row < 13; row++)
                for (var col = 0; col < 13; col++)
                    if (AphPreflopProvider.HandAt(row, col) == hand)
                        return Distance(scenario, hero, villain, row, col);
            return 0;
        }

        /// <summary>
        /// Distancia en la tabla 13x13 hasta la casilla mas cercana que se juega distinto
        /// (1 = esta en el borde del rango; 4 o mas = muy lejos del borde).
        /// </summary>
        public int Distance(string scenario, string hero, string villain, int row, int col)
        {
            var code = _charts.GetCode(scenario, hero, villain, AphPreflopProvider.HandAt(row, col)) ?? "F";
            for (var d = 1; d <= 4; d++)
                for (var r = Math.Max(0, row - d); r <= Math.Min(12, row + d); r++)
                    for (var c = Math.Max(0, col - d); c <= Math.Min(12, col + d); c++)
                    {
                        if (Math.Max(Math.Abs(r - row), Math.Abs(c - col)) != d) continue;
                        if ((_charts.GetCode(scenario, hero, villain, AphPreflopProvider.HandAt(r, c)) ?? "F") != code)
                            return d;
                    }
            return 5;
        }

        /// <summary>En un squeeze, quien pago la subida (una posicion entre el que abrio y el heroe).</summary>
        private string Caller((string Scenario, string Hero, string Villain) chart)
        {
            if (chart.Scenario != S.Squeeze)
                return "";
            var order = S.PositionOrder.ToList();
            var between = order.Where(p => order.IndexOf(p) > order.IndexOf(chart.Villain) && order.IndexOf(p) < order.IndexOf(chart.Hero)).ToList();
            return between.Count == 0 ? "" : between[_random.Next(between.Count)];
        }

        /// <summary>Cartas concretas al azar para una clase de mano ("AKs" -> "AhKh").</summary>
        private string RandomCards(string hand)
        {
            const string suits = "shdc";
            var s1 = suits[_random.Next(4)];
            if (hand.Length == 3 && hand[2] == 's')
                return $"{hand[0]}{s1}{hand[1]}{s1}";
            char s2;
            do { s2 = suits[_random.Next(4)]; } while (s2 == s1);
            return $"{hand[0]}{s1}{hand[1]}{s2}";
        }

        private string Pick(IReadOnlyList<KeyValuePair<string, double>> weights)
        {
            var total = weights.Sum(w => w.Value);
            var roll = _random.NextDouble() * total;
            foreach (var (key, weight) in weights)
            {
                roll -= weight;
                if (roll <= 0) return key;
            }
            return weights[^1].Key;
        }

        // ---------------------------------------------------------------- explicacion

        /// <summary>
        /// Explicacion de la respuesta, en frases cortas: cuanto se juega en esa situacion, como se
        /// juega la misma mano en otras posiciones, la mano vecina que ya se juega distinto, el
        /// tamano de la subida y como juega la poblacion real (las manos de la calibracion).
        /// </summary>
        public IReadOnlyList<string> Explain(PracticeQuestion q)
        {
            if (q.Scenario == S.HuAllIn)
                return ExplainPushFold(q);

            var lines = new List<string>();
            var chart = (q.Scenario, q.Hero, q.Villain);
            var combos = new Dictionary<string, int> { ["R"] = 0, ["C"] = 0, ["F"] = 0 };
            for (var i = 0; i < 169; i++)
            {
                var hand = AphPreflopProvider.HandAt(i / 13, i % 13);
                combos[Code(chart, hand)] += S.Combos(hand);
            }
            double Pct(string code) => 100.0 * combos[code] / 1326;

            lines.Add(combos["C"] == 0
                ? Format("Practice.Why.Share2", S.ActionLabel(q.Scenario, q.Hero, "R").ToLowerInvariant(), Pct("R"))
                : Format("Practice.Why.Share3", S.ActionLabel(q.Scenario, q.Hero, "R").ToLowerInvariant(), Pct("R"),
                    S.ActionLabel(q.Scenario, q.Hero, "C").ToLowerInvariant(), Pct("C")));

            // La misma mano en las otras posiciones o contra los otros rivales.
            var openLike = !_available.Any(c => c.Scenario == q.Scenario && c.Villain != "");
            var others = openLike
                ? S.PositionOrder.Where(p => _charts.HasChart(q.Scenario, p, "")).Select(p => (Label: S.PositionLabel(p, q.Scenario), Code: Code((q.Scenario, p, ""), q.Hand)))
                : S.PositionOrder.Where(v => _charts.HasChart(q.Scenario, q.Hero, v)).Select(v => (Label: Format("Practice.Why.Vs", S.PositionLabel(v, q.Scenario)), Code: Code((q.Scenario, q.Hero, v), q.Hand)));
            var otherList = others.ToList();
            if (otherList.Count > 1 && otherList.Select(o => o.Code).Distinct().Count() > 1)
            {
                var groups = otherList.GroupBy(o => o.Code)
                    .Select(g => $"{S.ActionLabel(q.Scenario, q.Hero, g.Key).ToLowerInvariant()}: {string.Join(", ", g.Select(o => o.Label))}");
                lines.Add(Format(openLike ? "Practice.Why.ByPosition" : "Practice.Why.ByVillain", q.Hand, string.Join(" · ", groups)));
            }
            else if (otherList.Count > 1)
            {
                lines.Add(Format(openLike ? "Practice.Why.SameEverywhere" : "Practice.Why.SameEveryVillain", q.Hand,
                    S.ActionLabel(q.Scenario, q.Hero, q.Correct).ToLowerInvariant()));
            }

            // La mano parecida mas cercana que ya se juega distinto.
            var neighbor = NearestDifferent(hand => Code(chart, hand), q.Hand);
            if (neighbor is not null)
                lines.Add(Format("Practice.Why.Neighbor", neighbor.Value.Hand, S.ActionLabel(q.Scenario, q.Hero, neighbor.Value.Code).ToLowerInvariant(),
                    S.ActionLabel(q.Scenario, q.Hero, q.Correct).ToLowerInvariant(), q.Hand));

            if (q.Correct == "R")
                lines.Add(S.Sizing(q.Scenario, q.Hero));

            var population = PopulationFact(q);
            if (population is not null)
                lines.Add(population);

            if (q.RealSpot is { } real)
                lines.Add(Format("Practice.Why.RealHand", real.Table, S.ActionLabel(q.Scenario, q.Hero, real.Action).ToLowerInvariant()));

            return lines;
        }

        /// <summary>
        /// All-in o retirarse: cuanto se juega con esa pila, cuanto gana o pierde la jugada, la
        /// cuenta que la explica (cuanto se retira el rival y que equidad hay) y hasta cuantas
        /// ciegas se juega esa mano.
        /// </summary>
        private IReadOnlyList<string> ExplainPushFold(PracticeQuestion q)
        {
            var lines = new List<string>();
            if (_solver is null)
                return lines;

            var solution = _solver.Solve(q.Stack);
            var detail = _solver.Detail(solution, q.Hand);
            var button = q.Hero == "SB";
            lines.Add(Format("Practice.Why.PushShare", q.Stack, solution.PushPercent, solution.CallPercent));

            var gain = button ? solution.PushGainOf(q.Hand) : solution.CallGainOf(q.Hand);
            var key = (button ? "Practice.Why.Push" : "Practice.Why.Call") + (gain >= 0 ? "Wins" : "Loses");
            lines.Add(Format(key, q.Hand, Math.Abs(gain)));
            lines.Add(button
                ? Format("Practice.Why.PushMath", detail.FoldPercent, detail.EquityWhenCalled)
                : Format("Practice.Why.CallMath", q.Hand, detail.EquityVsPush, detail.EquityNeeded));

            var index = PreflopEquityMatrix.IndexOf(q.Hand);
            if (index >= 0)
            {
                var chart = _solver.Chart();
                var upTo = button ? chart.PushUpTo[index] : chart.CallUpTo[index];
                var prefix = button ? "Practice.Why.PushUpTo" : "Practice.Why.CallUpTo";
                lines.Add(upTo >= chart.MaxStack ? Format(prefix + ".Always", q.Hand, chart.MaxStack)
                    : upTo <= 0 ? Format(prefix + ".Never", q.Hand)
                    : Format(prefix, q.Hand, upTo));
            }

            var neighbor = NearestDifferent(hand => CodeAt(q, hand), q.Hand);
            if (neighbor is not null)
                lines.Add(Format("Practice.Why.Neighbor", neighbor.Value.Hand, S.ActionLabel(q.Scenario, q.Hero, neighbor.Value.Code).ToLowerInvariant(),
                    S.ActionLabel(q.Scenario, q.Hero, q.Correct).ToLowerInvariant(), q.Hand));

            lines.Add(T("Practice.Why.PushNash"));
            return lines;
        }

        private static (string Hand, string Code)? NearestDifferent(Func<string, string> codeOf, string hand)
        {
            int row = -1, col = -1;
            for (var i = 0; i < 169 && row < 0; i++)
                if (AphPreflopProvider.HandAt(i / 13, i % 13) == hand) { row = i / 13; col = i % 13; }
            if (row < 0) return null;

            var code = codeOf(hand);
            static int Kind(int r, int c) => r == c ? 0 : r < c ? 1 : 2; // pareja, del mismo palo, de distinto palo
            (string Hand, string Code)? best = null;
            var bestScore = int.MaxValue;
            for (var r = 0; r < 13; r++)
                for (var c = 0; c < 13; c++)
                {
                    // Solo manos del mismo tipo: una pareja se compara con parejas, AJo con otras de distinto palo.
                    if (Kind(r, c) != Kind(row, col)) continue;
                    var other = AphPreflopProvider.HandAt(r, c);
                    var otherCode = codeOf(other);
                    if (otherCode == code) continue;
                    // Preferir la misma fila o columna (misma carta alta o mismo acompanante).
                    var score = Math.Max(Math.Abs(r - row), Math.Abs(c - col)) * 10 + (r == row || c == col ? 0 : 15);
                    if (score < bestScore) { bestScore = score; best = (other, otherCode); }
                }
            return bestScore <= 25 ? best : null;
        }

        private static string? PopulationFact(PracticeQuestion q)
        {
            if (S.IsHeadsUp(q.Scenario))
                return null;
            var position = q.Hero switch { "LJ" => "EP", _ => q.Hero };
            switch (q.Scenario)
            {
                case S.Open when PopulationBaselines.Preflop("openRaisePctByPosition", position) is { } open:
                    return Format("Practice.Why.PopOpen", open, S.PositionLabel(q.Hero, q.Scenario));
                case S.VsLimp when PopulationBaselines.Preflop("limpPctByPosition", q.Villain == "LJ" ? "EP" : q.Villain) is { } limp:
                    return Format("Practice.Why.PopLimp", limp, S.PositionLabel(q.Villain, q.Scenario));
                case S.VsRaise when PopulationBaselines.Preflop("vsOpen", "foldPct") is { } fold &&
                                     PopulationBaselines.Preflop("vsOpen", "threeBetPct") is { } threeBet:
                    return Format("Practice.Why.PopVsOpen", fold, threeBet);
                case S.Vs3Bet when PopulationBaselines.Preflop("vsThreeBet", "foldPct") is { } fold3 &&
                                    PopulationBaselines.Preflop("vsThreeBet", "fourBetPct") is { } fourBet:
                    return Format("Practice.Why.PopVs3Bet", fold3, fourBet);
                default:
                    return null;
            }
        }

        // ---------------------------------------------------------------- texto de la situacion

        public static string Situation(PracticeQuestion q)
        {
            if (q.Scenario == S.HuAllIn)
                return T("Practice.Sit.TableHu") + " · " + Format(q.Hero == "SB" ? "Practice.Sit.PushFold" : "Practice.Sit.VsPush", q.Stack);

            string P(string pos) => S.PositionLabel(pos, q.Scenario);
            var key = S.BaseOf(q.Scenario) switch
            {
                S.Open => "Practice.Sit.Open",
                S.VsLimp => "Practice.Sit.VsLimp",
                S.VsRaise => "Practice.Sit.VsRaise",
                S.Squeeze => "Practice.Sit.Squeeze",
                S.Vs3Bet => "Practice.Sit.Vs3Bet",
                S.Vs4Bet => "Practice.Sit.Vs4Bet",
                _ => "Practice.Sit.Vs5Bet"
            };
            var table = T(S.IsHeadsUp(q.Scenario) ? "Practice.Sit.TableHu" : "Practice.Sit.Table6");
            return table + " · " + Format(key, P(q.Hero), P(q.Villain), P(q.Caller));
        }

        private static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(key), args);

        private static string T(string key) => LocalizationManager.Text(key);
    }

    /// <summary>Progreso guardado de la practica: aciertos por tema y nivel, y errores para repasar.</summary>
    public sealed class PracticeProgress
    {
        public Dictionary<string, int[]> Results { get; set; } = new(); // "tema|nivel" -> [respondidas, acertadas]
        public List<PracticeMistake> Mistakes { get; set; } = new();
        public int BestStreak { get; set; }

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "practica.json");

        public static PracticeProgress Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<PracticeProgress>(File.ReadAllText(FilePath)) ?? new PracticeProgress();
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo leer el progreso de la practica", ex);
            }
            return new PracticeProgress();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo guardar el progreso de la practica", ex);
            }
        }

        public void Record(PracticeQuestion q, bool correct)
        {
            var key = $"{q.TopicId}|{q.Level}";
            if (!Results.TryGetValue(key, out var counts))
                Results[key] = counts = new int[2];
            counts[0]++;
            if (correct) counts[1]++;

            var mistake = new PracticeMistake(q.Scenario, q.Hero, q.Villain, q.Hand, q.Stack);
            if (correct)
                Mistakes.Remove(mistake);
            else if (!Mistakes.Contains(mistake))
            {
                Mistakes.Add(mistake);
                if (Mistakes.Count > 60) Mistakes.RemoveAt(0);
            }
        }

        /// <summary>Respondidas y acertadas de un tema (todos los niveles, o uno).</summary>
        public (int Answered, int Correct) Totals(string topicId, PracticeLevel? level = null)
        {
            var answered = 0;
            var correct = 0;
            foreach (var (key, counts) in Results)
            {
                var parts = key.Split('|');
                if (parts[0] != topicId || (level is not null && parts[1] != level.ToString())) continue;
                answered += counts[0];
                correct += counts[1];
            }
            return (answered, correct);
        }
    }
}
