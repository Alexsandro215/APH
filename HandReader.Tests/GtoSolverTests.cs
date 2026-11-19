using System.Diagnostics;
using Hud.App.Services;
using Hud.App.Services.Solver;
using Hud.App.Services.Trainer;
using Xunit;
using Xunit.Abstractions;

namespace HandReader.Tests;

/// <summary>Solver GTO del turn y el river entre dos jugadores (CFR+ por vectores).</summary>
public class GtoSolverTests
{
    private readonly ITestOutputHelper _output;

    public GtoSolverTests(ITestOutputHelper output) => _output = output;

    private static int C(string text)
    {
        Assert.True(TrainerCards.TryParse(text, out var card), text);
        return card;
    }

    private static int[] Cards(string text) => text.Split(' ').Select(C).ToArray();

    private static List<WeightedCombo> Combos(params string[] classes) =>
        classes.SelectMany(TrainerCards.ExpandClass).Select(c => new WeightedCombo(c.A, c.B, 1)).ToList();

    /// <summary>
    /// Juego de river clasico: fuera de posicion tiene la mejor mano o aire; en posicion, manos que
    /// solo ganan a un farol. Con una sola apuesta de bote, el equilibrio conocido es: apostar todas
    /// las manos buenas, farolear la mitad de esa cantidad (1 farol por cada 2 de valor) y que quien
    /// recibe la apuesta pague el 50%.
    /// </summary>
    [Fact]
    public void En_el_juego_polar_de_river_da_el_equilibrio_conocido()
    {
        var board = Cards("Ks 8d 4c 2h 2s");
        var nuts = Combos("KK");                     // trio de K (3 combos con la K de la mesa)
        var air = Combos("76s", "65s", "T9s");       // carta alta: pierden contra todo
        var catchers = Combos("88");                 // trio de 8: gana al aire, pierde contra KK
        var settings = new GtoSettings { BetSizes = new[] { 1.0 }, MaxBets = 1, AllowAllIn = false, MaxIterations = 3000, Budget = TimeSpan.FromSeconds(20), TrackAllAverages = true };

        var oop = nuts.Concat(air).ToList();
        var solver = new PostflopGtoSolver(board, 10, 100, oop, catchers, null, settings);
        var kk = TrainerCards.ExpandClass("KK").First(c => c.A != C("Ks") && c.B != C("Ks"));
        var value = solver.Solve(kk.A, kk.B);
        Assert.Equal(GtoKind.Bet, value.Best.Action.Kind);
        Assert.True(value.Choices.Single(c => c.Action.Kind == GtoKind.Bet).Frequency > 0.95);
        Assert.True(solver.ExploitabilityPercentOfPot() < 0.5, $"explotabilidad {solver.ExploitabilityPercentOfPot():0.###}% del bote");

        // Farol: la mitad de las manos de valor (valor 3 combos sin bloqueos, aire 12 combos).
        var bluffs = air.Select(c => solver).ToList();
        var bluffFrequency = air.Average(c =>
        {
            var s = new PostflopGtoSolver(board, 10, 100, oop, catchers, null, settings);
            return s.Solve(c.A, c.B).Choices.Single(x => x.Action.Kind == GtoKind.Bet).Frequency;
        });
        var valueCombos = TrainerCards.ExpandClass("KK").Count(c => c.A != C("Ks") && c.B != C("Ks"));
        Assert.InRange(bluffFrequency * air.Count, valueCombos * 0.5 - 0.6, valueCombos * 0.5 + 0.6);

        // Quien recibe la apuesta paga la mitad de las veces.
        var facing = new PostflopGtoSolver(board, 10, 100, oop, catchers, new[] { new GtoAction(GtoKind.Bet, 10) }, settings);
        var eights = TrainerCards.ExpandClass("88").First(c => c.A != C("8d") && c.B != C("8d"));
        var call = facing.Solve(eights.A, eights.B).Choices.Single(c => c.Action.Kind == GtoKind.Call).Frequency;
        Assert.InRange(call, 0.42, 0.58);
    }

    [Fact]
    public void Un_river_real_converge_a_un_equilibrio()
    {
        var board = Cards("Ks 9d 4c 7h 2s");
        var oop = HandRange.FromClasses("bb", PreflopRanking.TopPercent(35)).Combos.ToList();
        var ip = HandRange.FromClasses("btn", PreflopRanking.TopPercent(25)).Combos.ToList();
        var settings = new GtoSettings { MaxIterations = 600, Budget = TimeSpan.FromSeconds(30), TrackAllAverages = true };
        var watch = Stopwatch.StartNew();
        var solver = new PostflopGtoSolver(board, 12, 88, oop, ip, null, settings);
        var hero = Cards("Ah Kd");
        var result = solver.Solve(hero[0], hero[1]);
        var exploitability = solver.ExploitabilityPercentOfPot();
        _output.WriteLine($"river: {solver.DecisionNodes} nodos, {result.Iterations} iteraciones en {watch.ElapsedMilliseconds} ms, explotabilidad {exploitability:0.##}% del bote");
        foreach (var c in result.Choices)
            _output.WriteLine($"  {c.Action.Kind} {c.Action.To:0.#}: {c.Frequency:P0} EV {c.Ev:0.##}");
        Assert.True(exploitability < 1.5, $"explotabilidad {exploitability:0.##}% del bote");
        Assert.InRange(result.Choices.Sum(c => c.Frequency), 0.999, 1.001);
    }

    [Fact]
    public void El_turn_con_todas_las_rivers_se_resuelve_en_pocos_segundos()
    {
        var board = Cards("Ks 9d 4c 7h");
        var oop = HandRange.FromClasses("bb", PreflopRanking.TopPercent(30)).Combos.ToList();
        var ip = HandRange.FromClasses("btn", PreflopRanking.TopPercent(22)).Combos.ToList();
        // Vueltas fijas (no tiempo): con las pruebas en paralelo el procesador se reparte.
        var settings = new GtoSettings { MaxIterations = 60, Budget = TimeSpan.FromMinutes(2), TrackAllAverages = true };
        var watch = Stopwatch.StartNew();
        var solver = new PostflopGtoSolver(board, 12, 88, oop, ip, null, settings);
        var build = watch.ElapsedMilliseconds;
        var hero = Cards("Ah Kd");
        var result = solver.Solve(hero[0], hero[1]);
        _output.WriteLine($"turn: {solver.DecisionNodes} nodos, arbol en {build} ms, {result.Iterations} iteraciones en {result.Elapsed.TotalMilliseconds:0} ms");
        foreach (var c in result.Choices)
            _output.WriteLine($"  {c.Action.Kind} {c.Action.To:0.#}: {c.Frequency:P0} EV {c.Ev:0.##}");
        var exploitability = solver.ExploitabilityPercentOfPot();
        _output.WriteLine($"  explotabilidad del turn: {exploitability:0.##}% del bote");
        Assert.True(result.Iterations >= 30);
        Assert.Equal(60, result.Iterations);
        Assert.True(exploitability < 5, $"explotabilidad {exploitability:0.##}% del bote");
    }
}
