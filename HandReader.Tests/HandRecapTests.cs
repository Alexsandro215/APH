using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Repaso de la mano anterior que se ve unos segundos encima de la mesa.</summary>
public class HandRecapTests
{
    // Mano real (nombres cambiados), hasta el resumen: subida, dos pagos, showdown y bote.
    private static readonly string[] Hand =
    {
        "PokerStars Hand #262749232264:  Hold'em No Limit (100/200) - 2026/11/18 6:10:00 UTC [2026/11/18 1:10:00 ET]",
        "Table 'Mesa IV' 6-max (Play Money) Seat #3 is the button",
        "Seat 1: Rival1 (36302 in chips) ",
        "Seat 3: Rival3 (9100 in chips) ",
        "Seat 4: Rival4 (18617 in chips) ",
        "Seat 5: Heroe (19100 in chips) ",
        "Rival4: posts small blind 100",
        "Heroe: posts big blind 200",
        "*** HOLE CARDS ***",
        "Dealt to Heroe [8c 3d]",
        "Rival1: raises 400 to 600",
        "Rival3: calls 600",
        "Rival4: folds ",
        "Heroe: folds ",
        "*** FLOP *** [Qd 2s 2h]",
        "Rival1: bets 800",
        "Rival3: calls 800",
        "*** TURN *** [Qd 2s 2h] [7s]",
        "Rival1: checks ",
        "Rival3: checks ",
        "*** RIVER *** [Qd 2s 2h 7s] [5s]",
        "Rival1: checks ",
        "Rival3: checks ",
        "*** SHOW DOWN ***",
        "Rival1: shows [Qc Qh] (a full house, Queens full of Deuces)",
        "Rival3: shows [7d Jd] (two pair, Sevens and Deuces)",
        "Rival1 collected 3100 from pot",
        "*** SUMMARY ***",
    };

    [Fact]
    public void Resume_preflop_board_showdown_y_ganador()
    {
        LocalizationManager.Apply("es");
        var recap = HandRecap.Build(Hand, "Heroe")!;
        Assert.Equal("262749232264", recap.HandId);
        Assert.Equal("Rival1 sube a 3 bb · Rival3 paga · 2 se retiran", recap.Preflop);
        Assert.Equal(5, recap.Board.Count);
        Assert.True(recap.Showdown);

        // Tus cartas primero (no las ensenaste) y luego las del showdown, con su jugada.
        Assert.Equal(new[] { "Tú", "Rival1", "Rival3" }, recap.Shown.Select(s => s.Name));
        Assert.True(recap.Shown[0].IsHero);
        Assert.Equal(HandInsightReader.CategoryName(6), recap.Shown[1].Hand); // full
        var winner = Assert.Single(recap.Winners);
        Assert.Equal("Rival1", winner.Name);
        Assert.Equal(15.5, winner.Bb, 3);
    }

    [Fact]
    public void Sin_showdown_dice_quien_se_llevo_el_bote()
    {
        LocalizationManager.Apply("es");
        var lines = new[]
        {
            "PokerStars Hand #1:  Hold'em No Limit (100/200) - 2026/11/18 6:10:00 UTC",
            "Table 'Mesa' 6-max (Play Money) Seat #1 is the button",
            "Seat 1: Heroe (20000 in chips) ",
            "Seat 2: Rival (20000 in chips) ",
            "*** HOLE CARDS ***",
            "Dealt to Heroe [As Ad]",
            "Heroe: raises 400 to 600",
            "Rival: folds ",
            "Uncalled bet (400) returned to Heroe",
            "Heroe collected 500 from pot",
            "*** SUMMARY ***",
        };
        var recap = HandRecap.Build(lines, "Heroe")!;
        Assert.Equal("Tú subes a 3 bb · 1 se retira", recap.Preflop);
        Assert.Empty(recap.Board);
        Assert.False(recap.Showdown);
        Assert.Equal("Tú", Assert.Single(recap.Winners).Name);
    }
}
