using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Hud.App.Services.Trainer;

namespace Hud.App.Services.Solver
{
    /// <summary>
    /// Equidad de cada mano contra cada mano si las dos van all-in antes del flop (169 x 169, en el
    /// orden de la tabla 13x13) y cuantas parejas de combinaciones concretas pueden darse a la vez
    /// (AA contra AA solo 6 de 36, porque comparten ases). La equidad viene de
    /// Data/APH_Equidad_HU_169.txt, que genera APH.Calibration --equidad.
    /// </summary>
    public sealed class PreflopEquityMatrix
    {
        public const int Size = 169;

        private static readonly Lazy<IReadOnlyDictionary<string, int>> Indexes = new(() =>
            Enumerable.Range(0, Size).ToDictionary(i => HandAt(i), i => i, StringComparer.Ordinal));

        private readonly double[] _equity;
        private readonly double[] _pairs;

        private PreflopEquityMatrix(double[] equity)
        {
            _equity = equity;
            _pairs = CountPairs();
        }

        /// <summary>Probabilidad de ganar (mas la mitad de los empates) de la mano contra el rival.</summary>
        public double Equity(int hand, int villain) => _equity[hand * Size + villain];

        /// <summary>Parejas de combinaciones concretas sin cartas repetidas.</summary>
        public double Pairs(int hand, int villain) => _pairs[hand * Size + villain];

        internal double[] EquityValues => _equity;
        internal double[] PairValues => _pairs;

        public static string HandAt(int index) => AphPreflopProvider.HandAt(index / 13, index % 13);

        public static int IndexOf(string hand) => Indexes.Value.TryGetValue(hand, out var index) ? index : -1;

        public static PreflopEquityMatrix Load(string path)
        {
            var rows = File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();
            if (rows.Count != Size + 1 || rows[0].Length != Size)
                throw new InvalidDataException($"{Path.GetFileName(path)}: se esperaban {Size} manos y {Size} filas");

            // Las manos se buscan por nombre: el archivo no depende del orden interno de APH.
            var columns = rows[0].Select(IndexOf).ToArray();
            if (columns.Any(c => c < 0) || columns.Distinct().Count() != Size)
                throw new InvalidDataException($"{Path.GetFileName(path)}: la cabecera no tiene las {Size} manos");

            var equity = new double[Size * Size];
            var seen = new HashSet<int>();
            foreach (var row in rows.Skip(1))
            {
                var hand = IndexOf(row[0]);
                if (hand < 0 || row.Length != Size + 1 || !seen.Add(hand))
                    throw new InvalidDataException($"{Path.GetFileName(path)}: fila mal formada ({row[0]})");
                for (var c = 0; c < Size; c++)
                    equity[hand * Size + columns[c]] = int.Parse(row[c + 1], CultureInfo.InvariantCulture) / 10000.0;
            }

            return new PreflopEquityMatrix(equity);
        }

        private static double[] CountPairs()
        {
            var masks = Enumerable.Range(0, Size)
                .Select(i => TrainerCards.ExpandClass(HandAt(i)).Select(c => (1UL << c.A) | (1UL << c.B)).ToArray())
                .ToArray();
            var pairs = new double[Size * Size];
            for (var i = 0; i < Size; i++)
                for (var j = 0; j < Size; j++)
                {
                    var count = 0;
                    foreach (var a in masks[i])
                        foreach (var b in masks[j])
                            if ((a & b) == 0)
                                count++;
                    pairs[i * Size + j] = count;
                }
            return pairs;
        }
    }

    /// <summary>
    /// Equilibrio de all-in o retirarse en heads-up con una pila efectiva dada. Las manos van en el
    /// orden de la tabla 13x13 (PreflopEquityMatrix.HandAt). Los valores estan en ciegas grandes.
    /// </summary>
    public sealed class PushFoldSolution
    {
        internal PushFoldSolution(double stack, double ante, double[] push, double[] call, double[] pushGain, double[] callGain,
            double pushPercent, double callPercent, double buttonValue, double exploitability, int iterations)
        {
            Stack = stack;
            Ante = ante;
            Push = push;
            Call = call;
            PushGain = pushGain;
            CallGain = callGain;
            PushPercent = pushPercent;
            CallPercent = callPercent;
            ButtonValue = buttonValue;
            Exploitability = exploitability;
            Iterations = iterations;
        }

        /// <summary>Pila efectiva en ciegas grandes (la mas corta de las dos, antes de poner las ciegas).</summary>
        public double Stack { get; }

        /// <summary>Ante de cada jugador, en ciegas grandes.</summary>
        public double Ante { get; }

        /// <summary>Con que frecuencia hace all-in el boton (ciega pequena) con cada mano.</summary>
        public IReadOnlyList<double> Push { get; }

        /// <summary>Con que frecuencia paga la ciega grande el all-in con cada mano.</summary>
        public IReadOnlyList<double> Call { get; }

        /// <summary>Cuanto gana el all-in frente a retirarse, contra la ciega grande del equilibrio.</summary>
        public IReadOnlyList<double> PushGain { get; }

        /// <summary>Cuanto gana pagar frente a retirarse, contra el rango de all-in del equilibrio.</summary>
        public IReadOnlyList<double> CallGain { get; }

        /// <summary>Porcentaje de las 1326 combinaciones con las que el boton hace all-in.</summary>
        public double PushPercent { get; }

        /// <summary>Porcentaje de combinaciones con las que la ciega grande paga.</summary>
        public double CallPercent { get; }

        /// <summary>Lo que gana el boton por mano si los dos juegan el equilibrio.</summary>
        public double ButtonValue { get; }

        /// <summary>
        /// Lo mas que podrian ganar de mas, sumados, dos rivales perfectos que conocieran esta
        /// estrategia (uno en cada lado). En el equilibrio exacto es 0.
        /// </summary>
        public double Exploitability { get; }

        public int Iterations { get; }

        public double PushOf(string hand) => Value(Push, hand);
        public double CallOf(string hand) => Value(Call, hand);
        public double PushGainOf(string hand) => Value(PushGain, hand);
        public double CallGainOf(string hand) => Value(CallGain, hand);

        /// <summary>"R" si el boton hace all-in (mas de la mitad de las veces), "F" si no.</summary>
        public string ButtonCode(string hand) => PushOf(hand) >= 0.5 ? "R" : "F";

        /// <summary>"C" si la ciega grande paga (mas de la mitad de las veces), "F" si no.</summary>
        public string BigBlindCode(string hand) => CallOf(hand) >= 0.5 ? "C" : "F";

        private static double Value(IReadOnlyList<double> values, string hand)
        {
            var index = PreflopEquityMatrix.IndexOf(hand);
            return index < 0 ? 0 : values[index];
        }
    }

    /// <summary>Hasta cuantas ciegas se hace all-in (o se paga) con cada mano.</summary>
    public sealed class PushFoldChart
    {
        internal PushFoldChart(double ante, double maxStack, double[] pushUpTo, double[] callUpTo)
        {
            Ante = ante;
            MaxStack = maxStack;
            PushUpTo = pushUpTo;
            CallUpTo = callUpTo;
        }

        public double Ante { get; }

        /// <summary>La pila mas alta calculada: una mano con este valor se juega con esa pila o mas.</summary>
        public double MaxStack { get; }

        /// <summary>Pila mas alta con la que el boton hace all-in con cada mano (0 = nunca).</summary>
        public IReadOnlyList<double> PushUpTo { get; }

        /// <summary>Pila mas alta con la que la ciega grande paga con cada mano (0 = nunca).</summary>
        public IReadOnlyList<double> CallUpTo { get; }
    }

    /// <summary>Lo que explica una mano concreta: cuanto se retira el rival y que equidad hay.</summary>
    public sealed record PushFoldHandDetail(
        double FoldPercent,
        double EquityWhenCalled,
        double EquityVsPush,
        double EquityNeeded);

    /// <summary>
    /// Solver de all-in o retirarse en heads-up. El boton (ciega pequena) solo puede hacer all-in
    /// o retirarse; la ciega grande, pagar o retirarse. Es el modelo clasico de "push/fold": con
    /// pocas ciegas es practicamente el juego entero, y su equilibrio de Nash se calcula exacto.
    ///
    /// Se resuelve con CFR+ (minimizacion del arrepentimiento contrafactual) sobre las 169 manos,
    /// pesando cada pareja de manos por sus combinaciones concretas posibles. Cada iteracion cada
    /// mano compara lo que gana cada jugada contra la estrategia actual del rival y se inclina
    /// hacia la que mas gana; el promedio de las estrategias converge al equilibrio. Se para cuando
    /// ningun rival perfecto podria ganarle mas de una diezmilesima de ciega por mano.
    /// </summary>
    public sealed class PushFoldSolver
    {
        public const double MinStack = 1;
        public const double MaxChartStack = 25;
        public const double ChartStep = 0.5;
        public const double DefaultTolerance = 0.00001;
        private const int MinIterations = 400;
        // Una mano que gana o pierde menos que esto con su jugada esta indiferente: puede mezclar.
        private const double Indifferent = 0.002;

        private static readonly Lazy<(PushFoldSolver? Solver, string? Error)> Shared = new(LoadDefault);

        private readonly PreflopEquityMatrix _matrix;
        private readonly ConcurrentDictionary<(double Stack, double Ante), PushFoldSolution> _solutions = new();
        private readonly ConcurrentDictionary<double, Lazy<PushFoldChart>> _charts = new();
        private readonly double[] _rowWeights = new double[PreflopEquityMatrix.Size];
        private readonly double _totalWeight;

        public PushFoldSolver(PreflopEquityMatrix matrix)
        {
            _matrix = matrix;
            const int n = PreflopEquityMatrix.Size;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    _rowWeights[i] += matrix.Pairs(i, j);
            _totalWeight = _rowWeights.Sum();
        }

        public PreflopEquityMatrix Matrix => _matrix;

        /// <summary>El solver con la tabla de equidades de APH, o null si falta el archivo.</summary>
        public static PushFoldSolver? Default => Shared.Value.Solver;

        public static string? LoadError => Shared.Value.Error;

        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "APH_Equidad_HU_169.txt");

        private static (PushFoldSolver?, string?) LoadDefault()
        {
            try
            {
                return (new PushFoldSolver(PreflopEquityMatrix.Load(DefaultPath)), null);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Solver de all-in: no se pudo leer la tabla de equidades", ex);
                return (null, ex.Message);
            }
        }

        /// <summary>Equilibrio con esa pila (se guarda: pedirlo otra vez es inmediato).</summary>
        public PushFoldSolution Solve(double stack, double ante = 0)
        {
            stack = Math.Round(Math.Max(MinStack, stack), 2);
            ante = Math.Round(Math.Max(0, ante), 3);
            return _solutions.GetOrAdd((stack, ante), key => Compute(key.Stack, key.Ante, DefaultTolerance, 6000));
        }

        /// <summary>
        /// Tabla de "hasta cuantas ciegas": resuelve cada media ciega de 1 a 25 y guarda, por mano,
        /// la pila mas alta con la que se juega. Tarda unos segundos la primera vez.
        /// </summary>
        public PushFoldChart Chart(double ante = 0)
        {
            ante = Math.Round(Math.Max(0, ante), 3);
            return _charts.GetOrAdd(ante, a => new Lazy<PushFoldChart>(() => BuildChart(a))).Value;
        }

        public bool HasChart(double ante = 0) =>
            _charts.TryGetValue(Math.Round(Math.Max(0, ante), 3), out var chart) && chart.IsValueCreated;

        public static IReadOnlyList<double> ChartStacks { get; } =
            Enumerable.Range(0, (int)Math.Round((MaxChartStack - MinStack) / ChartStep) + 1)
                .Select(k => MinStack + k * ChartStep)
                .ToList();

        private PushFoldChart BuildChart(double ante)
        {
            const int n = PreflopEquityMatrix.Size;
            var pushUpTo = new double[n];
            var callUpTo = new double[n];
            var solutions = ChartStacks.AsParallel().AsOrdered().Select(stack => Solve(stack, ante)).ToList();
            foreach (var solution in solutions)
                for (var i = 0; i < n; i++)
                {
                    if (solution.Push[i] >= 0.5) pushUpTo[i] = solution.Stack;
                    if (solution.Call[i] >= 0.5) callUpTo[i] = solution.Stack;
                }

            return new PushFoldChart(ante, MaxChartStack, pushUpTo, callUpTo);
        }

        /// <summary>
        /// Para explicar una mano: cuanto se retira la ciega grande ante el all-in del boton, que
        /// equidad tiene la mano si le pagan, que equidad tiene contra el rango de all-in y cuanta
        /// necesita la ciega grande para pagar.
        /// </summary>
        public PushFoldHandDetail Detail(PushFoldSolution solution, string hand)
        {
            const int n = PreflopEquityMatrix.Size;
            var index = PreflopEquityMatrix.IndexOf(hand);
            if (index < 0)
                return new PushFoldHandDetail(0, 0, 0, 0);

            double weight = 0, folds = 0, called = 0, calledEquity = 0, pushes = 0, pushEquity = 0;
            for (var j = 0; j < n; j++)
            {
                var w = _matrix.Pairs(index, j);
                weight += w;
                folds += w * (1 - solution.Call[j]);
                called += w * solution.Call[j];
                calledEquity += w * solution.Call[j] * _matrix.Equity(index, j);
                // Como ciega grande: el rival es el boton con su rango de all-in.
                pushes += w * solution.Push[j];
                pushEquity += w * solution.Push[j] * _matrix.Equity(index, j);
            }

            var needed = (solution.Stack - 1 - solution.Ante) / (2 * solution.Stack);
            return new PushFoldHandDetail(
                weight <= 0 ? 0 : 100 * folds / weight,
                called <= 0 ? 0 : 100 * calledEquity / called,
                pushes <= 0 ? 0 : 100 * pushEquity / pushes,
                100 * Math.Max(0, needed));
        }

        // ---------------------------------------------------------------- CFR+

        private PushFoldSolution Compute(double stack, double ante, double tolerance, int maxIterations)
        {
            const int n = PreflopEquityMatrix.Size;
            var pairs = _matrix.PairValues;
            var equity = _matrix.EquityValues;

            var win = 1 + ante;          // el boton hace all-in y la ciega grande se retira
            var buttonFold = -(0.5 + ante);
            var bigBlindFold = -(1 + ante);

            // showdown[i, j]: lo que gana el boton con i si la ciega grande paga con j.
            // calledDelta[i, j] = peso * (showdown - win): cuanto cambia el all-in de i si j paga.
            var showdown = new double[n * n];
            var calledDelta = new double[n * n];
            for (var k = 0; k < n * n; k++)
            {
                showdown[k] = stack * (2 * equity[k] - 1);
                calledDelta[k] = pairs[k] * (showdown[k] - win);
            }

            var regretPush = new double[n];
            var regretFold = new double[n];
            var regretCall = new double[n];
            var regretPass = new double[n];
            var push = Enumerable.Repeat(0.5, n).ToArray();
            var call = Enumerable.Repeat(0.5, n).ToArray();
            var averagePush = new double[n];
            var averageCall = new double[n];
            double averageWeight = 0;
            var exploitability = double.MaxValue;
            var iterations = 0;

            for (var t = 1; t <= maxIterations; t++)
            {
                iterations = t;

                // Boton contra la ciega grande actual.
                for (var i = 0; i < n; i++)
                {
                    var pushValue = win * _rowWeights[i];
                    var row = i * n;
                    for (var j = 0; j < n; j++)
                        pushValue += calledDelta[row + j] * call[j];
                    var foldValue = buttonFold * _rowWeights[i];
                    var value = push[i] * pushValue + (1 - push[i]) * foldValue;
                    regretPush[i] = Math.Max(0, regretPush[i] + pushValue - value);
                    regretFold[i] = Math.Max(0, regretFold[i] + foldValue - value);
                    var sum = regretPush[i] + regretFold[i];
                    push[i] = sum > 0 ? regretPush[i] / sum : 0.5;
                }

                // Ciega grande contra el boton recien actualizado.
                for (var j = 0; j < n; j++)
                {
                    double callValue = 0, reach = 0;
                    for (var i = 0; i < n; i++)
                    {
                        var k = i * n + j;
                        var w = pairs[k] * push[i];
                        reach += w;
                        callValue -= w * showdown[k];
                    }
                    var foldValue = bigBlindFold * reach;
                    var value = call[j] * callValue + (1 - call[j]) * foldValue;
                    regretCall[j] = Math.Max(0, regretCall[j] + callValue - value);
                    regretPass[j] = Math.Max(0, regretPass[j] + foldValue - value);
                    var sum = regretCall[j] + regretPass[j];
                    call[j] = sum > 0 ? regretCall[j] / sum : 0.5;
                }

                // Promedio ponderado por el cuadrado de la iteracion: las primeras, aun lejos del
                // equilibrio (todas empiezan al 50%), casi no pesan.
                var weight = (double)t * t;
                for (var i = 0; i < n; i++)
                {
                    averagePush[i] += weight * push[i];
                    averageCall[i] += weight * call[i];
                }
                averageWeight += weight;

                if (t >= MinIterations && (t % 25 == 0 || t == maxIterations))
                {
                    exploitability = Evaluate(Normalize(averagePush, averageWeight), Normalize(averageCall, averageWeight),
                        showdown, win, buttonFold, bigBlindFold, out _, out _, out _);
                    if (exploitability < tolerance)
                        break;
                }
            }

            var finalPush = Normalize(averagePush, averageWeight);
            var finalCall = Normalize(averageCall, averageWeight);

            // En el equilibrio solo mezclan las manos indiferentes (las que ganan lo mismo con las dos
            // jugadas). Las demas juegan siempre su mejor jugada: asi no quedan restos de las primeras
            // iteraciones, como un 2% de all-in con 72o.
            Evaluate(finalPush, finalCall, showdown, win, buttonFold, bigBlindFold, out var buttonGain, out _, out _);
            for (var i = 0; i < n; i++)
                finalPush[i] = Purify(finalPush[i], buttonGain[i]);
            Evaluate(finalPush, finalCall, showdown, win, buttonFold, bigBlindFold, out _, out var bigBlindGain, out _);
            for (var j = 0; j < n; j++)
                finalCall[j] = Purify(finalCall[j], bigBlindGain[j]);

            exploitability = Evaluate(finalPush, finalCall, showdown, win, buttonFold, bigBlindFold,
                out var pushGain, out var callGain, out var buttonValue);

            double pushCombos = 0, callCombos = 0;
            for (var i = 0; i < n; i++)
            {
                var combos = TrainerCards.ComboCount(PreflopEquityMatrix.HandAt(i));
                pushCombos += combos * finalPush[i];
                callCombos += combos * finalCall[i];
            }

            return new PushFoldSolution(stack, ante, finalPush, finalCall, pushGain, callGain,
                100 * pushCombos / 1326, 100 * callCombos / 1326, buttonValue, exploitability, iterations);
        }

        /// <summary>
        /// Valor de cada jugada por mano y explotabilidad: lo que ganarian de mas, sumados, la mejor
        /// respuesta al boton y la mejor respuesta a la ciega grande (en ciegas por mano).
        /// </summary>
        private double Evaluate(double[] push, double[] call, double[] showdown, double win, double buttonFold, double bigBlindFold,
            out double[] pushGain, out double[] callGain, out double buttonValue)
        {
            const int n = PreflopEquityMatrix.Size;
            var pairs = _matrix.PairValues;
            pushGain = new double[n];
            callGain = new double[n];

            double gameValue = 0, buttonBest = 0, bigBlindBest = 0;
            for (var i = 0; i < n; i++)
            {
                double pushValue = 0;
                var row = i * n;
                for (var j = 0; j < n; j++)
                    pushValue += pairs[row + j] * ((1 - call[j]) * win + call[j] * showdown[row + j]);
                var foldValue = buttonFold * _rowWeights[i];
                gameValue += push[i] * pushValue + (1 - push[i]) * foldValue;
                buttonBest += Math.Max(pushValue, foldValue);
                pushGain[i] = _rowWeights[i] <= 0 ? 0 : (pushValue - foldValue) / _rowWeights[i];
            }

            for (var j = 0; j < n; j++)
            {
                double callValue = 0, reach = 0, foldedTo = 0;
                for (var i = 0; i < n; i++)
                {
                    var k = i * n + j;
                    reach += pairs[k] * push[i];
                    callValue -= pairs[k] * push[i] * showdown[k];
                    foldedTo += pairs[k] * (1 - push[i]);
                }
                var foldValue = bigBlindFold * reach;
                // Cuando el boton se retira, la ciega grande gana su ciega y su ante.
                bigBlindBest += -buttonFold * foldedTo + Math.Max(callValue, foldValue);
                callGain[j] = reach <= 0 ? 0 : (callValue - foldValue) / reach;
            }

            buttonValue = gameValue / _totalWeight;
            return (buttonBest + bigBlindBest) / _totalWeight;
        }

        private static double[] Normalize(double[] sum, double weight) => sum.Select(v => weight <= 0 ? 0 : v / weight).ToArray();

        private static double Purify(double frequency, double gain) =>
            gain > Indifferent ? 1 : gain < -Indifferent ? 0 : frequency < 0.01 ? 0 : frequency > 0.99 ? 1 : frequency;
    }
}
