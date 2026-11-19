using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>Posiciones de cada jugador: los ausentes no cuentan y el boton puede estar muerto.</summary>
public class PositionMapTests
{
    // Mano real (nombres cambiados): dos ausentes en la mesa. El heroe pone la ciega pequena.
    private static readonly string[] SittingOutHand =
    {
        "PokerStars Hand #262268803354:  Hold'em No Limit (100/200) - 2026/10/02 3:30:22 UTC [2026/10/01 23:30:22 ET]",
        "Table 'Mesa V' 6-max (Play Money) Seat #5 is the button",
        "Seat 2: ausente1 (0 in chips) is sitting out",
        "Seat 6: ausente2 (0 in chips) is sitting out",
        "Seat 1: Heroe (17800 in chips) ",
        "Seat 3: Rival3 (15901 in chips) ",
        "Seat 4: Rival4 (13860 in chips) ",
        "Seat 5: Rival5 (42630 in chips) ",
        "Heroe: posts small blind 100",
        "Rival3: posts big blind 200",
        "*** HOLE CARDS ***",
        "Dealt to Heroe [Td 7d]",
        "Rival4: folds ",
        "Rival5: folds ",
        "Heroe: calls 100",
        "Rival3: checks ",
    };

    [Fact]
    public void Los_ausentes_no_corren_las_posiciones()
    {
        var map = PokerStarsHandHistory.BuildPositionMap(SittingOutHand);
        Assert.Equal("SB", map["Heroe"]);
        Assert.Equal("BB", map["Rival3"]);
        Assert.Equal("BTN", map["Rival5"]);
        Assert.False(map.ContainsKey("ausente1"));
        Assert.False(map.ContainsKey("ausente2"));
    }

    [Fact]
    public void Coincide_con_la_lectura_de_situaciones_preflop()
    {
        var spot = Assert.Single(PreflopSpotReader.ReadHand(SittingOutHand));
        Assert.Equal("SB", spot.Hero);
        Assert.Equal(PreflopScenarios.Open, spot.Scenario);
        Assert.Equal("C", spot.Action); // completo la ciega: un limp
    }

    [Fact]
    public void Un_nombre_con_la_palabra_ausente_no_se_descarta()
    {
        Assert.False(PokerStarsHandHistory.IsSittingOut("Seat 3: ElAusente (1500 in chips) "));
        Assert.True(PokerStarsHandHistory.IsSittingOut("Seat 2: x (0 in chips) is sitting out"));
    }

    [Fact]
    public void Con_boton_muerto_la_ciega_pequena_es_el_siguiente_activo()
    {
        var hand = new[]
        {
            "PokerStars Hand #1:  Hold'em No Limit (1/2) - 2026/10/02 3:30:22 UTC",
            "Table 'Mesa' 6-max Seat #2 is the button",
            "Seat 1: A (200 in chips) ",
            "Seat 2: B (0 in chips) is sitting out",
            "Seat 3: C (200 in chips) ",
            "Seat 4: D (200 in chips) ",
            "Seat 5: E (200 in chips) ",
            "*** HOLE CARDS ***",
        };
        var map = PokerStarsHandHistory.BuildPositionMap(hand);
        Assert.Equal("SB", map["C"]);
        Assert.Equal("BB", map["D"]);
        Assert.Equal("BTN", map["A"]);
    }
}
