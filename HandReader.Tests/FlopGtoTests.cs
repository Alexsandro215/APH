using Hud.App.Services.Solver;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Solver GTO del flop (dos calles) y biblioteca del flop.</summary>
public class FlopGtoTests
{
    private static List<WeightedCombo> Range(params string[] classes) =>
        HandRange.FromClasses("prueba", classes).Combos.ToList();

    private static int[] Cards(string text) => TrainerCards.ParseMany(text).ToArray();

    [Fact]
    public void Converge_y_quien_no_tiene_nada_pasa()
    {
        // OOP solo con aire; IP con trios y top pair. OOP no gana nada apostando: pasa.
        var oop = Range("Q3o", "J4o", "T3o", "J3o");
        var ip = Range("KK", "77", "22", "AK");
        var solver = new FlopGtoSolver(Cards("Ks 7d 2c"), 5.5, 97.5, oop, ip, trackTurnAverages: true);
        solver.Solve(150, TimeSpan.FromMinutes(1));
        Assert.True(solver.ExploitabilityPercentOfPot() < 1.0, $"explotabilidad {solver.ExploitabilityPercentOfPot():0.00}%");

        var root = solver.Strategy("");
        var checks = root.Where(f => f is not null).Average(f => f![0]);
        Assert.True(checks > 0.9, $"pasa {checks:P0}");
    }

    [Fact]
    public void La_apuesta_real_del_rival_entra_en_el_arbol()
    {
        var oop = Range("AQs", "KQo", "QJs", "98s", "77", "54s");
        var ip = Range("AA", "KK", "QJo", "T9s", "A5s", "66");
        // OOP pasa e IP apuesta 3 bb (55% del bote, no es ninguno de los tamanos del arbol).
        var history = new[] { new GtoAction(GtoKind.Check, 0), new GtoAction(GtoKind.Bet, 3) };
        var solver = new FlopGtoSolver(Cards("Qh 9h 4c"), 5.5, 97.5, oop, ip, history);
        Assert.Equal(0, solver.TargetActor);

        var hero = Cards("Ad Qd");
        var solution = solver.Solve(hero[0], hero[1], 60, TimeSpan.FromMinutes(1));
        Assert.Equal(0, solution.Actor);
        Assert.Contains(solution.Choices, c => c.Action.Kind == GtoKind.Call && Math.Abs(c.Action.To - 3) < 0.01);
        Assert.Equal(1.0, solution.Choices.Sum(c => c.Frequency), 3);
        // Top pair buen kicker contra una apuesta media: no se retira.
        Assert.True(solution.Choices.Single(c => c.Action.Kind == GtoKind.Fold).Frequency < 0.1);
    }

    // ------------------------------------------------------------------ en vivo

    private static readonly Hud.App.Services.AphPreflopProvider Charts =
        Hud.App.Services.PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    /// <summary>El boton (heroe) abrio, la ciega grande pago y paso en el flop: le toca al heroe.</summary>
    private static (LiveDecision Decision, List<string> Thinking) DecideOnFlop(string hero, string flop)
    {
        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "SB", [2] = "LJ", [3] = "BB", [4] = "HJ", [5] = "CO" };
        var context = new LiveHandContext { HeroKey = hero };
        context.Merge("PREFLOP", new List<LivePlate>
        {
            new(0, "Heroe", "Raise", null, true, false, false),
            new(1, "A", "Fold", null, false, false, false),
            new(2, "B", "Fold", null, false, false, false),
            new(3, "Rival", "Call", null, true, false, false),
            new(4, "C", "Fold", null, false, false, false),
            new(5, "D", "Fold", null, false, false, false),
        }, positions);
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 5.5, new List<LivePlate>
        {
            new(0, "Heroe", null, 97.5, true, false, false),
            new(1, "A", "Fold", 100, false, false, false),
            new(2, "B", "Fold", 100, false, false, false),
            new(3, "Rival", "Check", 97.5, true, false, false),
            new(4, "C", "Fold", 100, false, false, false),
            new(5, "D", "Fold", 100, false, false, false),
        }, 2);
        var thinking = new List<string>();
        var advisor = new TrainerLiveDecisionAdvisor(Charts) { Thinking = street => thinking.Add(street) };
        var decision = advisor.Decide(Cards(hero), Cards(flop), "BTN", turn, context, positions, new Dictionary<int, string>(), _ => null);
        return (decision, thinking);
    }

    [Fact]
    public void En_vivo_en_el_flop_de_un_bote_subido_sale_GTO_resuelto_al_momento_o_de_la_biblioteca()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aph-flop-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Sin biblioteca: se resuelve en vivo (con el aviso de "calculando").
            FlopLibrary.UseFolder(folder);
            var (live, thinking) = DecideOnFlop("Ah Kd", "Qh 9h 4c");
            Assert.True(live.IsGto);
            Assert.Equal(new[] { "FLOP" }, thinking);
            Assert.Contains(live.Details, line => line.Contains("GTO del flop resuelto en vivo"));
            Assert.StartsWith("GTO con AKo", live.Reason);

            // Con ese flop en la biblioteca (con otros palos): al instante, sin calcular.
            var spot = FlopLibrary.Spots.Single(s => s.Opener == "BTN");
            var (oop, ip) = FlopLibrary.RangesFor(spot, new TrainerRangeEstimator(Charts));
            FlopLibrary.BuildOne(spot, FlopLibrary.Canonical(Cards("Qs 9s 4d")).Key, oop, ip, 20, CancellationToken.None);
            var (fromLibrary, none) = DecideOnFlop("Ah Kd", "Qh 9h 4c");
            Assert.True(fromLibrary.IsGto);
            Assert.Empty(none);
            Assert.Contains(fromLibrary.Details, line => line.Contains("biblioteca"));
        }
        finally
        {
            FlopLibrary.UseFolder(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public void Flops_canonicos_por_palos()
    {
        Assert.Equal(1755, FlopLibrary.CanonicalFlops.Count);
        Assert.Equal(FlopLibrary.Canonical(Cards("Ks 7d 2c")).Key, FlopLibrary.Canonical(Cards("2h Kc 7s")).Key);
        Assert.NotEqual(FlopLibrary.Canonical(Cards("Ks 7s 2c")).Key, FlopLibrary.Canonical(Cards("Ks 7d 2c")).Key);
    }

    [Fact]
    public void La_biblioteca_guarda_y_encuentra_el_flop_con_otros_palos()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aph-flop-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            FlopLibrary.UseFolder(folder);
            var spot = FlopLibrary.Spots.Single(s => s.Opener == "BTN");
            var bb = HandRange.FromClasses("bb", new[] { "AQs", "KQo", "QJs", "98s", "77", "54s", "T9o" });
            var btn = HandRange.FromClasses("btn", new[] { "AA", "KK", "QJo", "T9s", "A5s", "66", "AKo" });
            var key = FlopLibrary.Canonical(Cards("Qh 9h 4c")).Key;
            FlopLibrary.BuildOne(spot, key, bb, btn, 40, CancellationToken.None);
            Assert.True(FlopLibrary.Has("BTN", key));
            FlopLibrary.UseFolder(folder);   // se vuelve a leer del archivo
            Assert.Equal(1, FlopLibrary.Count("BTN"));

            // El mismo flop y la misma mano con los palos cambiados (h->s, c->d, d->c, s->h).
            var history = new[] { new GtoAction(GtoKind.Check, 0) };
            var heroA = Cards("Ah Kd");
            var heroB = Cards("As Kc");
            var a = FlopLibrary.Lookup("BTN", Cards("Qh 9h 4c"), heroA[0], heroA[1], history, 5.5, 97.5);
            var b = FlopLibrary.Lookup("BTN", Cards("Qs 9s 4d"), heroB[0], heroB[1], history, 5.5, 97.5);
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(1, b!.Actor);   // tras el check de la ciega grande habla el boton
            Assert.All(a!.Choices.Zip(b.Choices), pair => Assert.Equal(pair.First.Frequency, pair.Second.Frequency, 2));
            Assert.Contains(b.Choices, c => c.Action.Kind == GtoKind.Bet);

            // Una mano que no esta en el rango guardado (Q7o en la ciega grande): sin respuesta de la biblioteca.
            var q7 = Cards("Qd 7c");
            Assert.Null(FlopLibrary.Lookup("BTN", Cards("Qh 9h 4c"), q7[0], q7[1], Array.Empty<GtoAction>(), 5.5, 97.5));

            // Una apuesta del 33% del bote real (otro bote) cae en el tamano pequeno guardado.
            var aq = Cards("Ad Qd");
            var facing = FlopLibrary.Lookup("BTN", Cards("Qh 9h 4c"), aq[0], aq[1],
                new[] { new GtoAction(GtoKind.Check, 0), new GtoAction(GtoKind.Bet, 2.1) }, 6.5, 90);
            Assert.NotNull(facing);
            Assert.Equal(0, facing!.Actor);
            Assert.Contains(facing.Choices, c => c.Action.Kind == GtoKind.Call && Math.Abs(c.Action.To - 2.1) < 0.01);

            // Un tamano que no se parece a ninguno (bote x2) no usa la biblioteca: se resolvera en vivo.
            Assert.Null(FlopLibrary.Lookup("BTN", Cards("Qh 9h 4c"), aq[0], aq[1],
                new[] { new GtoAction(GtoKind.Check, 0), new GtoAction(GtoKind.Bet, 13) }, 6.5, 90));
        }
        finally
        {
            FlopLibrary.UseFolder(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
