using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Bote mal leido (sin la coma) y subir contra quien ya esta all-in.</summary>
public class TrainerPotReadingTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int[] Cards(string text) =>
        text.Split(' ').Select(c => TrainerCards.TryParse(c, out var card) ? card : -1).ToArray();

    [Fact]
    public void Entre_lecturas_del_bote_gana_la_que_no_perdio_la_coma()
    {
        Assert.Equal(87.9, LiveTableReader.PickPot(new[] { 879, 87.9, 87.9 }));
        Assert.Equal(87.9, LiveTableReader.PickPot(new[] { 87.9, 879 }));
        Assert.Equal(12.5, LiveTableReader.PickPot(new[] { 12.5, 13 }));
    }

    private static List<LivePlate> Plates(string? calvaireAction) => new()
    {
        new LivePlate(0, "Heroe", calvaireAction is null ? "Check" : null, 193.1, true, false, false),
        new LivePlate(1, "Brownson76", "Check", 108, true, false, false),
        new LivePlate(2, "jeebeee79", "Check", 170.3, true, false, false),
        new LivePlate(3, "BIGMONDAY", null, 100, false, true, false),
        new LivePlate(4, "YO MERO JUAN03", "Check", 99, true, false, false),
        new LivePlate(5, "calvaire", calvaireAction, calvaireAction is null ? 82.9 : null, true, false, false),
    };

    /// <summary>
    /// La mano de Stroobantia: Q-T sin nada en 6-8-2 y calvaire va all-in por 82,9 bb en un bote de 5.
    /// El lector leyo el bote como 879 y el consejo fue "ALL-IN: gana +112 bb". Con el bote de 5 que
    /// ya se vio en esta calle, 879 es imposible: se toma 87,9 y lo correcto es no ir.
    /// </summary>
    [Fact]
    public void Con_carta_alta_contra_un_all_in_enorme_no_va_aunque_el_bote_se_lea_sin_coma()
    {
        var positions = new Dictionary<int, string> { [0] = "HJ", [1] = "SB", [2] = "BB", [4] = "LJ", [5] = "BTN" };
        var context = new LiveHandContext { HeroKey = "Td Qc" };
        var advisor = new TrainerLiveDecisionAdvisor(Charts);
        var board = Cards("6s 8h 2s");

        // Turno del heroe en el flop con todos pasando: bote de 5 bb.
        var check = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 5, Plates(null), 2);
        advisor.Decide(Cards("Td Qc"), board, "HJ", check, context, positions, new Dictionary<int, string>(), _ => null);

        // calvaire va all-in por 82,9 y el bote se lee 879 (sin la coma).
        var facing = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 82.95, "Igualar 82,95 BB"),
            new LiveActionOption(LiveActionKind.Raise, 193.1, "Subir a 193,1 BB"),
        }, 879, Plates("AllIn"), 3);
        var decision = advisor.Decide(Cards("Td Qc"), board, "HJ", facing, context, positions, new Dictionary<int, string>(), _ => null);

        Assert.Equal("Fold", decision.Kind);
        Assert.Contains(decision.Details, line => line.StartsWith("Bote leído 879"));
    }

    [Fact]
    public void Quien_ya_esta_all_in_no_se_retira_ante_una_subida()
    {
        var hero = Cards("Td Qc");
        var board = Cards("6s 8h 2s");
        var dead = hero.Concat(board).ToHashSet();
        var any = PreflopRanking.TopPercent(60)
            .SelectMany(c => HandRange.FromClasses("", new[] { c }).Combos)
            .Where(c => !dead.Contains(c.A) && !dead.Contains(c.B))
            .ToList();

        var options = TrainerSolver.Postflop(hero, board, new[] { new SolverOpponent(any, null, 82.9, AllIn: true) },
            87.9, 82.9, 0, 193.1, 82.9, canRaise: true);

        foreach (var raise in options.Where(o => o.Kind == "Raise"))
            Assert.Equal(0, raise.FoldPct ?? 0, 3);
        // Con ~20-35% contra un rango de all-in y pagando casi el bote, no ir es lo que mas gana.
        Assert.Equal("Fold", options[0].Kind);
    }
}
