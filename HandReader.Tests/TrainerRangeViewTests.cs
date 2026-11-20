using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Cuadro de la izquierda de la mesa en tu turno: tu % de ganar y la tabla de rango que explica el
/// consejo (preflop tu rango con tu mano marcada; despues del flop, el rango del rival).
/// </summary>
public class TrainerRangeViewTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    private static int[] Cards(string text) => text.Split(' ').Select(card =>
    {
        Assert.True(TrainerCards.TryParse(card, out var parsed), card);
        return parsed;
    }).ToArray();

    private static List<LivePlate> Plates(string? villainAction = null) => new()
    {
        new LivePlate(0, "Heroe", null, 100, true, false, false),
        new LivePlate(1, "A", "Fold", 100, false, false, false),
        new LivePlate(2, "B", "Fold", 100, false, false, false),
        new LivePlate(3, "Rival", villainAction, 100, true, false, false),
        new LivePlate(4, "C", "Fold", 100, false, false, false),
        new LivePlate(5, "D", "Fold", 100, false, false, false),
    };

    private static LiveDecision Decide(string hero, string board, string position, LiveTurnState turn) =>
        new TrainerLiveDecisionAdvisor(Charts) { Rhythm = null }.Decide(
            Cards(hero), board.Length == 0 ? Array.Empty<int>() : Cards(board), position, turn,
            new LiveHandContext { HeroKey = hero }, new Dictionary<int, string> { [0] = position, [3] = "BB" },
            new Dictionary<int, string> { [3] = "Rival" }, _ => null);

    [Fact]
    public void Al_abrir_sale_tu_rango_con_tu_mano_y_tu_porcentaje_contra_cualquiera()
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 1, "Igualar 1 BB"),
            new LiveActionOption(LiveActionKind.Raise, 2.5, "Subir a 2,5 BB"),
        }, 1.5, Plates(), 3);

        var view = Decide("Ah Kd", "", "CO", turn).RangeView;
        Assert.NotNull(view);
        Assert.True(view!.IsHeroRange);
        Assert.Equal("AKo", view.HeroClass);
        Assert.Contains("abrir desde CO", view.Title);
        Assert.True(view.Raise.ContainsKey("AKo"));
        Assert.False(view.Raise.ContainsKey("72o"));
        Assert.Equal(PreflopRanking.EquityVsRandom("AKo"), view.WinPct!.Value, 1);
        Assert.Equal("contra una mano cualquiera", view.WinAgainst);
    }

    [Fact]
    public void Despues_del_flop_sale_el_rango_del_rival_y_tu_porcentaje_contra_el()
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Check, null, "Pasar"),
            new LiveActionOption(LiveActionKind.Bet, 1, "Apostar 1 BB"),
        }, 6, Plates("Check"), 2);

        var decision = Decide("7h 7d", "7s Kc 2d", "BTN", turn);
        var view = decision.RangeView;
        Assert.NotNull(view);
        Assert.False(view!.IsHeroRange);
        Assert.Null(view.HeroClass);
        Assert.Equal("Rango de Rival", view.Title);
        Assert.NotEmpty(view.Raise);
        Assert.All(view.Raise.Values, weight => Assert.InRange(weight, 0, 1));
        // Con un trio, ganas casi siempre contra su rango; el mismo % que dicen los detalles.
        Assert.True(view.WinPct > 80, $"{view.WinPct}");
        Assert.Contains(decision.Details, line => line.StartsWith($"Equity estimada: {view.WinPct!.Value.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("es-ES"))}%"));
    }
}
