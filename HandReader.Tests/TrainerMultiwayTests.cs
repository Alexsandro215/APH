using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Botes con varios rivales: con que manos entrar pagando detras de limpers y cuando apostar por valor.</summary>
public class TrainerMultiwayTests
{
    private static readonly AphPreflopProvider Charts = PreflopLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Data"));

    [Theory]
    [InlineData("22", true)]
    [InlineData("44", true)]
    [InlineData("A5s", true)]
    [InlineData("J8s", true)]
    [InlineData("T9s", true)]
    [InlineData("65s", true)]
    [InlineData("54s", true)]
    [InlineData("QJo", true)]
    [InlineData("JTo", true)]
    [InlineData("A2o", false)]
    [InlineData("A5o", false)]
    [InlineData("A7o", false)]
    [InlineData("K7o", false)]
    [InlineData("Q9o", false)]
    [InlineData("72s", false)]
    [InlineData("T9o", false)]
    public void Detras_de_limpers_entran_parejas_suited_y_figuras_no_ases_flojos_offsuit(string combo, bool playable)
    {
        Assert.Equal(playable, TrainerDecisionRules.PlayableMultiway(combo, PreflopRanking.TopPercentOf(combo)));
    }

    [Fact]
    public void Con_varios_rivales_el_valor_se_mide_contra_la_parte_justa()
    {
        var none = new HandInsight("Pareja", "", 0, 1, 1);

        // Mano a mano no cambia: 46% sin proyecto es pasar.
        Assert.Equal("Check", TrainerDecisionRules.WithInitiative(46, none, "FLOP", false, 1));
        Assert.Equal(55, TrainerDecisionRules.ValueEquity("FLOP", 1));

        // Top pair con 46% contra cuatro rivales a la vez (parte justa 20%) es valor claro.
        Assert.Equal("Bet (valor)", TrainerDecisionRules.WithInitiative(46, none, "FLOP", false, 4));
        // Un full en el river con 50% contra cuatro, tambien.
        Assert.Equal("Bet (valor)", TrainerDecisionRules.WithInitiative(50, none, "RIVER", false, 4));
        // Con la parte justa (33% contra dos) no hay valor.
        Assert.Equal("Check", TrainerDecisionRules.WithInitiative(33, none, "FLOP", false, 2));
    }

    [Theory]
    [InlineData("Ad 5c", "Fold")]
    [InlineData("Kh 7c", "Fold")]
    [InlineData("6s 5s", "Call")]
    [InlineData("2c 2d", "Call")]
    [InlineData("Qh Jc", "Call")]
    public void En_vivo_detras_de_dos_limpers_no_entra_con_ases_flojos_offsuit(string hero, string expected)
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 1, "Igualar 1 BB"),
            new LiveActionOption(LiveActionKind.Raise, 4, "Subir a 4 BB"),
        }, 3.5, new List<LivePlate>
        {
            new(0, "Heroe", null, 100, true, false, false),
            new(1, "A", "Fold", 100, false, false, false),
            new(2, "B", "Call", 99, true, false, false),
            new(3, "C", "Call", 99, true, false, false),
            new(4, "D", null, 99.5, true, false, false),
            new(5, "E", null, 99, true, false, false),
        }, 3);

        var positions = new Dictionary<int, string> { [0] = "BTN", [1] = "LJ", [2] = "HJ", [3] = "CO", [4] = "SB", [5] = "BB" };
        var cards = hero.Split(' ').Select(text => TrainerCards.TryParse(text, out var card) ? card : -1).ToArray();
        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(cards, Array.Empty<int>(), "BTN", turn,
            new LiveHandContext { HeroKey = hero }, positions, new Dictionary<int, string>(), _ => null);

        Assert.Equal(expected, decision.Kind);
        if (expected == "Fold")
            Assert.Contains("sin mismo palo", decision.Reason);
    }

    [Fact]
    public void Una_subida_enorme_no_se_juega_con_la_tabla_de_un_open()
    {
        Assert.False(TrainerDecisionRules.IsOversizedRaise(1, 3));
        Assert.False(TrainerDecisionRules.IsOversizedRaise(1, 6));   // open grande sobre limpers
        Assert.True(TrainerDecisionRules.IsOversizedRaise(1, 33.3));
        Assert.False(TrainerDecisionRules.IsOversizedRaise(2, 11));  // 3bet normal
        Assert.True(TrainerDecisionRules.IsOversizedRaise(2, 40));
    }

    /// <summary>
    /// La mano de Iolanda V: tres limps, all-in de 33 bb del boton, la ciega pequena paga y el heroe
    /// tiene 33 en la ciega grande. Antes salia "IGUALAR: la tabla APH paga con 33 en BB contra BTN".
    /// </summary>
    [Theory]
    [InlineData("AllIn")]  // la placa dice All-in
    [InlineData("Raise")]  // el all-in no se leyo: solo "Subir"
    [InlineData(null)]     // la placa sin accion legible
    public void En_ciega_grande_con_33_contra_all_in_y_un_pagador_no_paga(string? shoverAction)
    {
        var turn = new LiveTurnState(new[]
        {
            new LiveActionOption(LiveActionKind.Fold, null, "No ir"),
            new LiveActionOption(LiveActionKind.Call, 32.3, "Igualar 32,3 BB"),
            new LiveActionOption(LiveActionKind.Raise, 65.7, "Subir a 65,7 BB"),
        }, 70.7, new List<LivePlate>
        {
            new(0, "UwU", null, 168.5, true, false, false),
            new(1, "tavcr223", "Call", 69, true, false, false),
            new(2, "montes100", "Call", 123.4, true, false, false),
            new(3, "Negociadoor", "Call", 121.5, true, false, false),
            new(4, "pereiraksvf", shoverAction, null, true, false, false),
            new(5, "Yopon", "Call", 2.5, true, false, false),
        }, 3);

        var positions = new Dictionary<int, string> { [0] = "BB", [1] = "LJ", [2] = "HJ", [3] = "CO", [4] = "BTN", [5] = "SB" };
        var cards = new[] { "3h", "3c" }.Select(text => TrainerCards.TryParse(text, out var card) ? card : -1).ToArray();
        var decision = new TrainerLiveDecisionAdvisor(Charts).Decide(cards, Array.Empty<int>(), "BB", turn,
            new LiveHandContext { HeroKey = "3h 3c" }, positions, new Dictionary<int, string>(), _ => null);

        Assert.Equal("Fold", decision.Kind);
        Assert.DoesNotContain("La tabla APH paga", decision.Reason);
    }
}
