using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>Asientos para el HUD de la mesa: quien esta sentado, donde, y en que lugar de la pantalla.</summary>
public class HudSeatTests
{
    // Mano real de "Terebellum IV" (nombres cambiados): un ausente y un resumen que repite los asientos.
    private static readonly string[] Hand =
    {
        "PokerStars Hand #262749232264:  Hold'em No Limit (100/200) - 2026/11/18 6:10:00 UTC [2026/11/18 1:10:00 ET]",
        "Table 'Mesa IV' 6-max (Play Money) Seat #3 is the button",
        "Seat 2: Ausente2 (20000 in chips) is sitting out",
        "Seat 1: Rival1 (36302 in chips) ",
        "Seat 3: Rival3 (9100 in chips) ",
        "Seat 4: Rival4 (18617 in chips) ",
        "Seat 5: Heroe (19100 in chips) ",
        "Seat 6: Rival6 (10088 in chips) ",
        "Rival4: posts small blind 100",
        "Heroe: posts big blind 200",
        "*** HOLE CARDS ***",
        "Dealt to Heroe [8c 3d]",
        "Rival6: calls 200",
        "Rival1: raises 400 to 600",
        "Rival3: calls 600",
        "Rival4: folds ",
        "Heroe: folds ",
        "Rival6: folds ",
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
        "Rival1 collected 3200 from pot",
        "*** SUMMARY ***",
        "Total pot 3200 | Rake 0",
        "Board [Qd 2s 2h 7s 5s]",
        "Seat 2: Ausente2",
        "Seat 1: Rival1 showed [Qc Qh] and won (3200) with a full house, Queens full of Deuces",
        "Seat 3: Rival3 (button) showed [7d Jd] and lost with two pair, Sevens and Deuces",
        "Seat 4: Rival4 (small blind) folded before Flop",
        "Seat 5: Heroe (big blind) folded before Flop",
        "Seat 6: Rival6 folded before Flop (didn't bet)",
    };

    private static StatsAggregator Parse()
    {
        var agg = new StatsAggregator();
        new PokerStarsParser(agg).FeedLines(Hand.Append("PokerStars Hand #0: fin"), () => { });
        return agg;
    }

    [Fact]
    public void El_resumen_no_duplica_ni_inventa_jugadores()
    {
        var agg = Parse();
        Assert.Equal(new[] { "Rival1", "Ausente2", "Rival3", "Rival4", "Heroe", "Rival6" }, agg.CurrentTableOrder);
        Assert.DoesNotContain(agg.CurrentTableOrder, name => name.Contains("showed") || name.Contains("folded"));
    }

    [Fact]
    public void Cada_jugador_tiene_su_asiento_y_el_ausente_se_marca()
    {
        var agg = Parse();
        Assert.Equal(6, agg.TableSize);
        Assert.Equal(6, agg.CurrentSeats.Count);
        Assert.Equal(5, agg.CurrentSeats.Single(s => s.Name == "Heroe").Seat);
        Assert.True(agg.CurrentSeats.Single(s => s.Name == "Ausente2").SittingOut);
        Assert.False(agg.CurrentSeats.Single(s => s.Name == "Rival1").SittingOut);
        Assert.Equal(1, agg.Players["Rival1"].HandsReceived); // el resumen no cuenta otra mano
    }

    [Theory]
    // El heroe en el asiento 5 queda abajo (lugar 0); despues, en el sentido del reloj, como en
    // PokerStars: el 6 a la izquierda, el 1 arriba a la izquierda, el 2 arriba, el 3 arriba a la
    // derecha y el 4 a la derecha.
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    public void Los_asientos_giran_para_que_el_heroe_quede_abajo(int seat, int place)
    {
        Assert.Equal(place, HudLayout.PlaceOf(seat, heroSeat: 5, tableSize: 6));
    }

    [Fact]
    public void Hay_un_lugar_para_cada_asiento_en_todas_las_mesas()
    {
        foreach (var size in new[] { 2, 6, 9, 10 })
        {
            var centers = HudLayout.DefaultCenters(size);
            Assert.Equal(size, centers.Length);
            Assert.All(centers, c => Assert.InRange(c.X, 0, 1));
            Assert.All(centers, c => Assert.InRange(c.Y, 0, 1));
            Assert.Equal(size, Enumerable.Range(1, size).Select(seat => HudLayout.PlaceOf(seat, 3, size)).Distinct().Count());
        }
    }
}
