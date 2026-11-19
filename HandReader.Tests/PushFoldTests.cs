using System.Diagnostics;
using Hud.App.Services;
using Hud.App.Services.Solver;
using Xunit;
using Xunit.Abstractions;
using S = Hud.App.Services.PreflopScenarios;

namespace HandReader.Tests;

/// <summary>
/// Solver de all-in o retirarse en heads-up: la tabla de equidades, el equilibrio (que nadie
/// pueda explotarlo), que se parezca a lo conocido de la teoria y la practica de este tema.
/// </summary>
public class PushFoldTests
{
    private static readonly Lazy<PushFoldSolver> Solver = new(() =>
        new PushFoldSolver(PreflopEquityMatrix.Load(Path.Combine(AppContext.BaseDirectory, "Data", "APH_Equidad_HU_169.txt"))));

    private readonly ITestOutputHelper _output;

    public PushFoldTests(ITestOutputHelper output) => _output = output;

    private static int I(string hand) => PreflopEquityMatrix.IndexOf(hand);

    // ---------------- Tabla de equidades ----------------

    [Theory]
    [InlineData("AA", "KK", 81.95)]   // valores exactos conocidos de las calculadoras de equidad
    [InlineData("AA", "AKo", 93.1)]
    [InlineData("AA", "AKs", 87.9)]
    [InlineData("QQ", "AKo", 56.9)]
    [InlineData("22", "AKo", 52.8)]
    [InlineData("AKo", "JTs", 59.4)]
    [InlineData("KK", "AA", 18.05)]
    public void La_equidad_coincide_con_los_valores_conocidos(string hand, string villain, double expected)
    {
        var equity = 100 * Solver.Value.Matrix.Equity(I(hand), I(villain));
        Assert.InRange(equity, expected - 0.6, expected + 0.6);
    }

    [Fact]
    public void Las_dos_manos_suman_el_cien_por_cien()
    {
        var matrix = Solver.Value.Matrix;
        for (var i = 0; i < PreflopEquityMatrix.Size; i++)
            for (var j = 0; j < PreflopEquityMatrix.Size; j++)
                Assert.InRange(matrix.Equity(i, j) + matrix.Equity(j, i), 0.9998, 1.0002);
    }

    [Fact]
    public void Cuenta_las_parejas_de_combinaciones_sin_cartas_repetidas()
    {
        var matrix = Solver.Value.Matrix;
        Assert.Equal(6, matrix.Pairs(I("AA"), I("AA")));     // dos ases para cada uno
        Assert.Equal(12, matrix.Pairs(I("AKs"), I("AKs")));  // 4 x 3: el rival no puede tener el mismo palo
        Assert.Equal(6 * 6, matrix.Pairs(I("AA"), I("KK")));
        double total = 0;
        for (var i = 0; i < PreflopEquityMatrix.Size; i++)
            for (var j = 0; j < PreflopEquityMatrix.Size; j++)
                total += matrix.Pairs(i, j);
        Assert.Equal(1326.0 * 1225, total);                 // todas las parejas de manos posibles
    }

    [Fact]
    public void Contra_una_mano_al_azar_da_lo_mismo_que_el_ranking_de_APH()
    {
        var matrix = Solver.Value.Matrix;
        foreach (var hand in new[] { "AA", "KQs", "T9s", "72o", "22" })
        {
            double weight = 0, sum = 0;
            for (var j = 0; j < PreflopEquityMatrix.Size; j++)
            {
                weight += matrix.Pairs(I(hand), j);
                sum += matrix.Pairs(I(hand), j) * matrix.Equity(I(hand), j);
            }
            var expected = Hud.App.Services.Trainer.PreflopRanking.EquityVsRandom(hand);
            Assert.InRange(100 * sum / weight, expected - 0.3, expected + 0.3);
        }
    }

    // ---------------- Equilibrio ----------------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public void Ningun_rival_perfecto_puede_explotar_la_solucion(double stack)
    {
        var solution = Solver.Value.Solve(stack);
        Assert.True(solution.Exploitability < 0.001, $"explotabilidad {solution.Exploitability:0.00000} bb con {stack} bb");
    }

    [Fact]
    public void Cada_mano_hace_la_jugada_que_mas_gana()
    {
        var solution = Solver.Value.Solve(10);
        for (var i = 0; i < PreflopEquityMatrix.Size; i++)
        {
            // Si se juega, ganar al menos lo mismo que retirarse; si no, no ganar mas (con tolerancia).
            if (solution.Push[i] >= 0.999) Assert.True(solution.PushGain[i] > -0.002, PreflopEquityMatrix.HandAt(i));
            if (solution.Push[i] <= 0.001) Assert.True(solution.PushGain[i] < 0.002, PreflopEquityMatrix.HandAt(i));
            if (solution.Call[i] >= 0.999) Assert.True(solution.CallGain[i] > -0.002, PreflopEquityMatrix.HandAt(i));
            if (solution.Call[i] <= 0.001) Assert.True(solution.CallGain[i] < 0.002, PreflopEquityMatrix.HandAt(i));
        }
    }

    [Fact]
    public void Con_10_ciegas_se_parece_al_equilibrio_conocido()
    {
        // La teoria (Sklansky-Chubukov / tablas de Nash de heads-up): con 10 bb el boton va all-in
        // con algo mas de la mitad de las manos y la ciega grande paga con algo mas de un tercio.
        var solution = Solver.Value.Solve(10);
        Assert.InRange(solution.PushPercent, 52, 64);
        Assert.InRange(solution.CallPercent, 32, 44);
        foreach (var hand in new[] { "AA", "22", "A2o", "K2s", "Q9o", "J8s", "T9s" })
            Assert.Equal(1, solution.PushOf(hand));
        foreach (var hand in new[] { "72o", "32o", "82o", "93o" })
            Assert.Equal(0, solution.PushOf(hand));
        foreach (var hand in new[] { "AA", "22", "A2o", "KTo", "QJs" })
            Assert.Equal(1, solution.CallOf(hand));
        foreach (var hand in new[] { "72o", "J4o", "T7s", "95s" })
            Assert.Equal(0, solution.CallOf(hand));
    }

    [Fact]
    public void Con_mas_ciegas_se_juegan_menos_manos()
    {
        var previous = (Push: 101.0, Call: 101.0);
        foreach (var stack in new[] { 1.5, 3, 5, 8, 12, 16, 20, 25 })
        {
            var solution = Solver.Value.Solve(stack);
            _output.WriteLine($"{stack,4} bb: all-in {solution.PushPercent:0.0}% · paga {solution.CallPercent:0.0}% · boton {solution.ButtonValue:+0.000;-0.000} · error {solution.Exploitability:0.00000} · {solution.Iterations} iteraciones");
            Assert.True(solution.PushPercent <= previous.Push + 0.5, $"all-in con {stack} bb");
            Assert.True(solution.CallPercent <= previous.Call + 0.5, $"pagar con {stack} bb");
            previous = (solution.PushPercent, solution.CallPercent);
        }
    }

    [Fact]
    public void Con_ante_se_juegan_mas_manos()
    {
        var plain = Solver.Value.Solve(10);
        var ante = Solver.Value.Solve(10, 0.1);
        Assert.True(ante.PushPercent > plain.PushPercent);
        Assert.True(ante.CallPercent > plain.CallPercent);
    }

    [Fact]
    public void La_ciega_grande_paga_si_su_equidad_alcanza_la_necesaria()
    {
        var solver = Solver.Value;
        var solution = solver.Solve(8);
        foreach (var hand in new[] { "A5o", "K9s", "66", "QTo", "J9s" })
        {
            var detail = solver.Detail(solution, hand);
            var calls = solution.CallOf(hand) >= 0.5;
            // Pagar es bueno justo cuando la equidad contra el rango de all-in supera la necesaria.
            Assert.Equal(detail.EquityVsPush >= detail.EquityNeeded - 0.3, calls);
        }
        Assert.InRange(solver.Detail(solution, "AA").EquityNeeded, 43.7, 43.8); // (8 - 1) / 16
    }

    [Fact]
    public void La_tabla_de_hasta_cuantas_ciegas_es_coherente()
    {
        var watch = Stopwatch.StartNew();
        var chart = Solver.Value.Chart();
        _output.WriteLine($"Tabla completa en {watch.ElapsedMilliseconds} ms");
        Assert.Equal(PushFoldSolver.MaxChartStack, chart.PushUpTo[I("AA")]);
        Assert.Equal(PushFoldSolver.MaxChartStack, chart.CallUpTo[I("AA")]);
        Assert.InRange(chart.PushUpTo[I("72o")], 1, 4);
        Assert.True(chart.PushUpTo[I("K9o")] > chart.PushUpTo[I("K3o")]);
        Assert.True(chart.CallUpTo[I("A9o")] > chart.CallUpTo[I("A2o")]);

        // Huecos: manos que se juegan con una pila, no con otra mas baja y otra vez con una mas alta.
        var gaps = new List<string>();
        for (var i = 0; i < PreflopEquityMatrix.Size; i++)
        {
            var seenFold = false;
            foreach (var stack in PushFoldSolver.ChartStacks)
            {
                var push = Solver.Value.Solve(stack).Push[i] >= 0.5;
                if (!push) seenFold = true;
                else if (seenFold) { gaps.Add($"{PreflopEquityMatrix.HandAt(i)}@{stack}"); break; }
            }
        }
        _output.WriteLine("Huecos: " + string.Join(", ", gaps));

        foreach (var row in Enumerable.Range(0, 13))
            _output.WriteLine(string.Join(" ", Enumerable.Range(0, 13).Select(col =>
            {
                var i = row * 13 + col;
                return $"{PreflopEquityMatrix.HandAt(i),-3} {chart.PushUpTo[i],4:0.#}/{chart.CallUpTo[i],4:0.#}";
            })));
    }

    // ---------------- Practica ----------------

    [Theory]
    [InlineData(PracticeLevel.Easy)]
    [InlineData(PracticeLevel.Medium)]
    [InlineData(PracticeLevel.Hard)]
    [InlineData(PracticeLevel.Expert)]
    public void La_practica_pregunta_lo_que_dice_el_solver(PracticeLevel level)
    {
        var charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var engine = new PreflopPracticeEngine(charts, Array.Empty<PreflopSpot>(), new Random(11), Solver.Value);
        var answers = new HashSet<string>();
        for (var k = 0; k < 40; k++)
        {
            var q = engine.Next(PreflopPracticeEngine.PushFoldTopic, level);
            Assert.NotNull(q);
            Assert.Equal(S.HuAllIn, q!.Scenario);
            Assert.InRange(q.Stack, 2, 20);
            var solution = Solver.Value.Solve(q.Stack);
            var expected = q.Hero == "SB" ? solution.ButtonCode(q.Hand) : solution.BigBlindCode(q.Hand);
            Assert.Equal(expected, q.Correct);
            Assert.Contains(q.Correct, q.Options);
            Assert.Equal(2, q.Options.Count);
            Assert.True(PreflopPracticeEngine.FitsLevel(q.Distance, level), $"{q.Hand} {q.Stack} bb nivel {level}");
            Assert.Equal(q.Correct, engine.CodeAt(q, q.Hand));
            answers.Add(q.Correct);

            var lines = engine.Explain(q);
            Assert.True(lines.Count >= 4);
            Assert.DoesNotContain(lines, line => line.Contains('{'));
            Assert.DoesNotContain("{", PreflopPracticeEngine.Situation(q));
        }
        Assert.Contains("F", answers);
        Assert.True(answers.Contains("R") || answers.Contains("C"));
    }

    [Fact]
    public void Los_errores_de_all_in_se_repasan_con_su_pila()
    {
        var charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));
        var engine = new PreflopPracticeEngine(charts, Array.Empty<PreflopSpot>(), new Random(4), Solver.Value);
        var mistake = new PracticeMistake(S.HuAllIn, "BB", "", "K7o", 12);
        var q = engine.Next(PreflopPracticeEngine.MistakesTopic, PracticeLevel.Medium, new[] { mistake });
        Assert.NotNull(q);
        Assert.Equal(12, q!.Stack);
        Assert.Equal("BB", q.Hero);
        Assert.Equal("K7o", q.Hand);
        Assert.Equal(Solver.Value.Solve(12).BigBlindCode("K7o"), q.Correct);
    }
}
