using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Precio de pagar un all-in preflop que te cubre, aunque el OCR lea mal un stack.</summary>
public class TrainerPreflopPriceTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int[] Cards(string text) =>
        text.Split(' ').Select(c => TrainerCards.TryParse(c, out var card) ? card : -1).ToArray();

    private static readonly Dictionary<int, string> Positions = new() { [0] = "CO", [1] = "BTN", [2] = "SB", [3] = "BB", [4] = "LJ", [5] = "HJ" };

    private static List<LivePlate> Plates(double? heroStack, string? heroAction, double? bbStack, string? bbAction) => new()
    {
        new LivePlate(0, "Heroe", heroAction, heroStack, true, false, false),
        new LivePlate(1, "A", "Fold", 100, false, false, false),
        new LivePlate(2, "B", "Fold", 99.5, false, false, false),
        new LivePlate(3, "Rival", bbAction, bbStack, true, false, false),
        new LivePlate(4, "C", "Fold", 100, false, false, false),
        new LivePlate(5, "D", "Fold", 100, false, false, false),
    };

    /// <summary>
    /// La mano de Dejanira: AA abre, la BB hace 3bet, AA 4bet a 38 y la BB va all-in cubriendolo. Al
    /// empezar el OCR leyo el stack de la BB sin la coma (1175 en vez de 117,5): lo puesto por la BB
    /// salia de 1175 bb, el bote "que puedes ganar" 0 y el consejo "NO IR: no llegas al 100%".
    /// </summary>
    [Fact]
    public void Con_AA_contra_un_all_in_que_te_cubre_paga_aunque_un_stack_se_lea_mal()
    {
        var context = new LiveHandContext { HeroKey = "Ah Ac" };
        context.Merge("PREFLOP", Plates(92.5, null, 1175, "Post"), Positions);
        context.Merge("PREFLOP", Plates(54.5, "Raise", null, "AllIn"), Positions);

        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 54.5, "Igualar 54,5 BB"),
        }, 156, Plates(54.5, "Raise", null, "AllIn"), 2);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ah Ac"), Array.Empty<int>(), "CO", turn,
            context, Positions, new Dictionary<int, string>(), _ => null);

        Assert.Equal("Call", decision.Kind);
        Assert.DoesNotContain("100%", decision.Reason);
    }

    /// <summary>
    /// AKs abre a 2,5 y la BB va all-in por 130 bb; al heroe le quedan 86,6. El boton de igualar ya
    /// dice 86,6 (lo que puede pagar), asi que el exceso de 40 bb no se descontaba del bote.
    /// </summary>
    [Fact]
    public void Si_el_rival_te_cubre_su_exceso_no_cuenta_en_el_precio()
    {
        var context = new LiveHandContext { HeroKey = "Ac Kc" };
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 86.6, "Igualar 86,6 BB"),
        }, 132.9, Plates(86.6, "Raise", null, "AllIn"), 2);

        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(Cards("Ac Kc"), Array.Empty<int>(), "CO", turn,
            context, Positions, new Dictionary<int, string>(), _ => null);

        // Pagar 86,6 para ganar ~92 de bote: hacen falta ~48-50%, no el 39% de antes.
        var price = Assert.Single(decision.Details, line => line.StartsWith("Precio real"));
        Assert.Matches(@"necesitas (4[6-9]|50)%", price);
    }
}
