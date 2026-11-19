using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hud.App.Services.Trainer;

/// <summary>
/// Genera la tabla de equidades preflop de cada mano contra cada mano (169 x 169) para el solver
/// de all-in de APH: con que frecuencia gana cada mano si las dos van all-in antes del flop.
///
/// Por cada pareja de manos se recorren todas sus combinaciones concretas sin cartas repetidas
/// (AKs contra QQ son 4 x 6 = 24, menos las que chocan) por turnos, y para cada una se reparte un
/// board al azar. Asi cada combinacion pesa lo mismo que en el reparto real. El evaluador es el
/// mismo de APH (FastHandEvaluator), compilado aqui como archivo enlazado.
///
/// Uso:
///   dotnet run --project APH.Calibration -c Release -- --equidad Hud.App\Data\APH_Equidad_HU_169.txt [repartos=200000]
/// </summary>
static class EquityTable
{
    private const string Ranks = "AKQJT98765432";

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Uso: APH.Calibration --equidad <salida.txt> [repartos_por_pareja=200000]");
            return 1;
        }

        var output = args[0];
        var samples = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200_000;
        var classes = Enumerable.Range(0, 169).Select(i => HandAt(i / 13, i % 13)).ToArray();
        var combos = classes.Select(c => TrainerCards.ExpandClass(c).ToArray()).ToArray();
        var equity = new double[169, 169];
        var pairs = new List<(int I, int J)>();
        for (var i = 0; i < 169; i++)
        {
            equity[i, i] = 0.5; // la misma mano contra si misma: los dos jugadores son simetricos
            for (var j = i + 1; j < 169; j++)
                pairs.Add((i, j));
        }

        Console.WriteLine($"Parejas de manos: {pairs.Count:N0}, repartos por pareja: {samples:N0}");
        var stopwatch = Stopwatch.StartNew();
        var done = 0;
        Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pair =>
        {
            var value = Equity(combos[pair.I], combos[pair.J], samples, Seed(pair.I, pair.J));
            equity[pair.I, pair.J] = value;
            equity[pair.J, pair.I] = 1 - value;
            var count = Interlocked.Increment(ref done);
            if (count % 1000 == 0)
                Console.WriteLine($"  {count:N0} / {pairs.Count:N0} ({stopwatch.Elapsed:mm\\:ss})");
        });

        var text = new StringBuilder();
        text.AppendLine("# APH - equidad preflop de cada mano contra cada mano si las dos van all-in antes del flop.");
        text.AppendLine($"# Generado con APH.Calibration --equidad: {samples} repartos por pareja de manos, repartidos por igual");
        text.AppendLine("# entre todas sus combinaciones concretas sin cartas repetidas. Evaluador: FastHandEvaluator de APH.");
        text.AppendLine("# Primera linea de datos: las 169 manos en el orden de la tabla 13x13. Despues una fila por mano:");
        text.AppendLine("# su equidad contra cada mano de la cabecera, en diezmilesimas (5000 = 50%), contando la mitad de los empates.");
        text.AppendLine(string.Join(' ', classes));
        for (var i = 0; i < 169; i++)
        {
            text.Append(classes[i]);
            for (var j = 0; j < 169; j++)
                text.Append(' ').Append(Math.Round(equity[i, j] * 10000).ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
        }

        File.WriteAllText(output, text.ToString().Replace("\r\n", "\n"));
        Console.WriteLine($"Listo en {stopwatch.Elapsed:mm\\:ss}: {output}");
        Console.WriteLine($"Control: AA-KK {equity[0, 14]:P2}, AKo-QQ {equity[13, 28]:P2}, 22-AKo {equity[168, 13]:P2}");
        return 0;
    }

    /// <summary>Misma convencion que AphPreflopProvider.HandAt: arriba a la derecha del mismo palo.</summary>
    private static string HandAt(int row, int col) =>
        row == col ? $"{Ranks[row]}{Ranks[col]}"
        : row < col ? $"{Ranks[row]}{Ranks[col]}s"
        : $"{Ranks[col]}{Ranks[row]}o";

    private static double Equity((int A, int B)[] hero, (int A, int B)[] villain, int samples, ulong seed)
    {
        var matchups = new List<(int A, int B, int C, int D)>();
        foreach (var h in hero)
            foreach (var v in villain)
                if (v.A != h.A && v.A != h.B && v.B != h.A && v.B != h.B)
                    matchups.Add((h.A, h.B, v.A, v.B));

        var state = seed | 1;
        var decks = matchups.Select(m => Enumerable.Range(0, 52).Where(c => c != m.A && c != m.B && c != m.C && c != m.D).ToArray()).ToArray();
        Span<int> heroCards = stackalloc int[7];
        Span<int> villainCards = stackalloc int[7];
        double score = 0;
        for (var k = 0; k < samples; k++)
        {
            var index = k % matchups.Count;
            var m = matchups[index];
            var deck = decks[index];
            for (var i = 0; i < 5; i++)
            {
                var j = i + (int)Next(ref state, (uint)(48 - i));
                (deck[i], deck[j]) = (deck[j], deck[i]);
                heroCards[2 + i] = deck[i];
                villainCards[2 + i] = deck[i];
            }

            heroCards[0] = m.A;
            heroCards[1] = m.B;
            villainCards[0] = m.C;
            villainCards[1] = m.D;
            var heroScore = FastHandEvaluator.Evaluate(heroCards);
            var villainScore = FastHandEvaluator.Evaluate(villainCards);
            score += heroScore > villainScore ? 1 : heroScore == villainScore ? 0.5 : 0;
        }

        return score / samples;
    }

    private static ulong Seed(int i, int j) => 0x9E3779B97F4A7C15UL * (ulong)(i * 169 + j + 1);

    /// <summary>xorshift64*: rapido y suficiente para repartir boards.</summary>
    private static uint Next(ref ulong state, uint bound)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        var value = (uint)((state * 0x2545F4914F6CDD1DUL) >> 32);
        return (uint)(((ulong)value * bound) >> 32);
    }
}
