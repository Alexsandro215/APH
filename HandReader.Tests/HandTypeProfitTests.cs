using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Rentabilidad por tipo de mano: tipos, muestra y suerte en el all-in.</summary>
public class HandTypeProfitTests
{
    [Theory]
    [InlineData("AA", "PairsHigh")]
    [InlineData("88", "PairsMid")]
    [InlineData("22", "PairsLow")]
    [InlineData("AKo", "AkAq")]
    [InlineData("AQs", "AkAq")]
    [InlineData("A5s", "AxSuited")]
    [InlineData("AJo", "AjAto")]
    [InlineData("A7o", "AxOffWeak")]
    [InlineData("KQs", "BroadwaySuited")]
    [InlineData("QTo", "BroadwayOff")]
    [InlineData("65s", "SuitedConnectors")]
    [InlineData("K4s", "SuitedWeak")]
    [InlineData("K9o", "KqOffMid")]
    [InlineData("72o", "OffsuitWeak")]
    public void Cada_combo_cae_en_su_tipo(string combo, string expected) =>
        Assert.Equal(expected, HandTypeProfit.Category(combo));

    [Fact]
    public void El_reporte_quita_la_suerte_y_mide_la_muestra()
    {
        var samples = new List<HandTypeSample>
        {
            new("K9o", "BTN", +30, +40),   // gano 30 pero la suerte le dio 40: sin suerte -10
            new("Q8o", "CO", -5, 0),
            new("K7o", "SB", -2, 0),
        };
        var row = Assert.Single(HandTypeProfit.Build(samples, key => key));

        Assert.Equal("KqOffMid", row.Key);
        Assert.Equal(3, row.Hands);
        Assert.Equal(23, row.NetBb, 3);
        Assert.Equal(-17, row.AdjustedBb, 3);
        Assert.Equal("VeryLow", row.SampleKey);
        Assert.False(row.IsClear); // 3 manos: nada es concluyente
        Assert.Equal((2, -15.0), row.ByGroup["Late"]);
        Assert.Equal((1, -2.0), row.ByGroup["Blinds"]);
    }

    private const string AllInHand = """
        PokerStars Hand #1000000001:  Hold'em No Limit (100/200) - 2026/11/18 22:42:51 UTC [2026/11/18 17:42:51 ET]
        Table 'Prueba' 6-max (Play Money) Seat #1 is the button
        Seat 1: Heroe (20000 in chips)
        Seat 2: Villano (16000 in chips)
        Seat 3: Otro (20000 in chips)
        Villano: posts small blind 100
        Otro: posts big blind 200
        *** HOLE CARDS ***
        Dealt to Heroe [As Qs]
        Heroe: raises 400 to 600
        Villano: raises 15400 to 16000 and is all-in
        Otro: folds
        Heroe: calls 15400
        *** FLOP *** [4h 8d 2s]
        *** TURN *** [4h 8d 2s] [9s]
        *** RIVER *** [4h 8d 2s 9s] [5c]
        *** SHOW DOWN ***
        Villano: shows [Ts 8c] (a pair of Eights)
        Heroe: shows [As Qs] (high card Ace)
        Villano collected 32000 from pot
        *** SUMMARY ***
        Total pot 32200 | Rake 200
        Board [4h 8d 2s 9s 5c]
        """;

    /// <summary>
    /// AQs paga un all-in de 80 bb de T8o: iba ~66% y perdio. Pierde 80 bb, pero le tocaban ~+25: la
    /// mala suerte fue de unas -105 bb.
    /// </summary>
    [Fact]
    public void Un_all_in_perdido_yendo_por_delante_es_mala_suerte()
    {
        var lines = AllInHand.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var hand = TrainerHandParser.Parse(lines, "Heroe");
        Assert.NotNull(hand);

        var luck = HandTypeProfit.AllInLuckBb(hand!);
        Assert.InRange(luck, -110, -100);
    }
}
