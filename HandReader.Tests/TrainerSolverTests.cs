using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Solver de entrenamiento de la decision en vivo (solo dinero ficticio): all-in o retirarse con
/// pocas ciegas y, despues del flop, la opcion que mas gana contra el rango estimado.
/// </summary>
public class TrainerSolverTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int C(string text)
    {
        Assert.True(TrainerCards.TryParse(text, out var card), text);
        return card;
    }

    private static int[] Cards(string text) => text.Split(' ').Select(C).ToArray();

    // ------------------------------------------------------------------ all-in o retirarse

    [Fact]
    public void Con_pocas_ciegas_el_boton_va_all_in_como_el_equilibrio()
    {
        Assert.True(TrainerSolver.PushAdvice("AKo", 10)!.Value.Push);
        Assert.False(TrainerSolver.PushAdvice("72o", 10)!.Value.Push);
        Assert.True(TrainerSolver.PushAdvice("K2o", 11)!.Value.Push);   // K2o va hasta 11,5 bb
        Assert.False(TrainerSolver.PushAdvice("K2o", 13)!.Value.Push);
        Assert.Null(TrainerSolver.PushAdvice("AKo", 16));                // con mas de 15 mandan las tablas
    }

    [Fact]
    public void La_ciega_grande_paga_el_all_in_como_el_equilibrio()
    {
        Assert.True(TrainerSolver.CallAdvice("AA", 20)!.Value.Call);
        Assert.False(TrainerSolver.CallAdvice("72o", 10)!.Value.Call);
        Assert.True(TrainerSolver.CallAdvice("22", 15)!.Value.Call);    // 22 paga hasta 15 bb
    }

    [Fact]
    public void El_solver_en_la_mesa_solo_acepta_dinero_ficticio()
    {
        // El de Tiempo real usa el mismo filtro que el Modo Entrenador (y su sesion vuelve a
        // comprobar el titulo y el historial antes de leer nada).
        Assert.True(TrainerSession.IsPlayMoneyTitle(
            "Lysithea V - Hold'em Sin límite 100/200 Dinero ficticio - Sesión iniciada con el nombre de usuario _REN_RP"));
        Assert.False(TrainerSession.IsPlayMoneyTitle(
            "Lysithea V - Hold'em Sin límite $0.01/$0.02 - Sesión iniciada con el nombre de usuario _REN_RP"));
        Assert.False(TrainerSession.IsPlayMoneyTitle(
            "Torneo 123456 Mesa 1 - Hold'em Sin límite Dinero ficticio - Sesión iniciada con el nombre de usuario _REN_RP"));
        Assert.True(new HudOverlaySettings().ShowSolver);
    }

    // ------------------------------------------------------------------ cuanto se retira el rival

    [Fact]
    public void El_rival_se_retira_mas_con_aire_y_ante_apuestas_grandes()
    {
        Assert.True(TrainerSolver.FoldChance(0, 1.0, "FLOP", null) > TrainerSolver.FoldChance(0, 0.33, "FLOP", null));
        Assert.True(TrainerSolver.FoldChance(0, 0.5, "FLOP", null) > TrainerSolver.FoldChance(1, 0.5, "FLOP", null));
        Assert.True(TrainerSolver.FoldChance(1, 0.5, "FLOP", null) > TrainerSolver.FoldChance(2, 0.5, "FLOP", null));
        Assert.Equal(0, TrainerSolver.FoldChance(3, 2.0, "RIVER", null));

        var station = new TrainerVillainProfile("pagador", 200, 55, 6, 2, 0.6, 20, 45, "sesion");
        Assert.True(station.IsCallingStation);
        Assert.True(TrainerSolver.FoldChance(0, 0.5, "FLOP", station) < TrainerSolver.FoldChance(0, 0.5, "FLOP", null));
    }

    // ------------------------------------------------------------------ despues del flop

    private static IReadOnlyList<WeightedCombo> Wide(IReadOnlyList<int> dead) =>
        HandRange.FromClasses("amplio", PreflopRanking.TopPercent(50)).Combos
            .Where(c => !dead.Contains(c.A) && !dead.Contains(c.B))
            .ToList();

    [Fact]
    public void Con_un_trio_y_sin_apuesta_delante_apuesta_por_valor()
    {
        var hero = Cards("7h 7d");
        var board = Cards("7s Kc 2d");
        var range = Wide(hero.Concat(board).ToList());
        var options = TrainerSolver.Postflop(hero, board, new[] { new SolverOpponent(range, null, 0) }, 6, 0, 0, 94, 0, canRaise: true);
        Assert.Equal("Bet", options[0].Kind);
        Assert.True(options[0].Ev > options.Single(o => o.Kind == "Check").Ev);
    }

    [Fact]
    public void Con_aire_en_el_river_contra_una_apuesta_de_bote_se_retira()
    {
        var hero = Cards("6h 5h");
        var board = Cards("As Kd Qc 9s 2d");
        // Quien apuesta el bote en el river: manos hechas (pareja de A o K, o mejor).
        var strong = HandRange.FromClasses("fuerte", new[]
            {
                "AA", "KK", "QQ", "99", "AKs", "AKo", "AQs", "AQo", "KQs", "KQo", "AJs", "AJo", "KJs", "KJo", "A9s", "A9o"
            }).Combos
            .Where(c => !hero.Contains(c.A) && !hero.Contains(c.B) && !board.Contains(c.A) && !board.Contains(c.B))
            .ToList();
        var options = TrainerSolver.Postflop(hero, board, new[] { new SolverOpponent(strong, null, 10) }, 20, 10, 0, 80, 10, canRaise: true);
        Assert.Equal("Fold", options[0].Kind);
        Assert.True(options.Single(o => o.Kind == "Call").Ev < 0);
    }

    // ------------------------------------------------------------------ decision en vivo

    private static LiveDecision DecideLive(string hero, string board, string position, LiveTurnState turn)
    {
        var advisor = new TrainerLiveDecisionAdvisor(Charts);
        var positions = new Dictionary<int, string> { [0] = position, [3] = position == "SB" ? "BB" : "BB" };
        return advisor.Decide(Cards(hero), board.Length == 0 ? Array.Empty<int>() : Cards(board), position, turn,
            new LiveHandContext { HeroKey = hero }, positions, new Dictionary<int, string>(), _ => null);
    }

    private static List<LivePlate> Plates(double heroStack, double villainStack, string? villainAction = null) => new()
    {
        new LivePlate(0, "Heroe", null, heroStack, true, false, false),
        new LivePlate(1, "A", "Fold", 100, false, false, false),
        new LivePlate(2, "B", "Fold", 100, false, false, false),
        new LivePlate(3, "Rival", villainAction, villainStack, true, false, false),
        new LivePlate(4, "C", "Fold", 100, false, false, false),
        new LivePlate(5, "D", "Fold", 100, false, false, false),
    };

    [Fact]
    public void En_vivo_la_ciega_pequena_con_10_bb_va_all_in_o_se_retira_como_el_solver()
    {
        LiveTurnState Turn() => new(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 0.5, "Igualar 0,5 BB"),
            new LiveActionOption(LiveActionKind.Raise, 2, "Subir a 2 BB"),
        }, 1.5, Plates(9.5, 40), 3);

        var push = DecideLive("Ah Kd", "", "SB", Turn());
        Assert.Contains("ALL-IN", push.Action);
        Assert.Contains(push.Details, line => line.Contains("Solver de all-in"));

        var fold = DecideLive("7h 2d", "", "SB", Turn());
        Assert.Equal("Fold", fold.Kind);
    }

    [Theory]
    [InlineData("Ks 9d 4c 7h 2s")] // river
    [InlineData("Ks 9d 4c 7h")]    // turn (con todas las rivers)
    public void En_vivo_en_turn_y_river_contra_un_rival_sale_la_jugada_GTO(string board)
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 12, Plates(80, 80, "Check"), 2);

        var decision = DecideLive("Ah Kd", board, "BTN", turn);
        Assert.True(decision.IsGto);
        Assert.Contains(decision.Details, line => line.Contains("GTO entre dos jugadores"));
        Assert.StartsWith("GTO con AKo", decision.Reason);
    }

    [Fact]
    public void En_vivo_despues_del_flop_recomienda_lo_que_mas_gana_y_lo_explica()
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 6, Plates(90, 90, "Check"), 2);

        var decision = DecideLive("7h 7d", "7s Kc 2d", "BTN", turn);
        Assert.Equal("Bet", decision.Kind);
        Assert.False(decision.IsGto); // sin subida preflop los rangos son muy anchos para el GTO del flop en vivo
        Assert.Contains(decision.Details, line => line.Contains("Solver de entrenamiento"));
        Assert.Contains("APOSTAR", decision.Action);
    }
}
