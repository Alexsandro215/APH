using Hud.App.Services;
using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Ventana de rendimiento de Tiempo real: lo ganado (con y sin suerte) del ultimo tramo de la sesion
/// y los errores que mas repites agrupados, con como evitarlos.
/// </summary>
[Collection("TrainerReviewLog")]
public class SessionPerformanceTests
{
    [Fact]
    public void Los_errores_se_agrupan_por_lo_que_hiciste_y_lo_que_tocaba()
    {
        var patterns = RealTimeSessionReportService.BuildMistakePatterns(new[]
        {
            ("h1", "Ah Kd", -20.0, "RIVER", "Call 10 bb", "Fold", 0.0),
            ("h2", "Qc Qh", -40.0, "TURN", "Call 12 bb", "Fold", -10.0),   // sin la mala suerte: -30
            ("h3", "7c 2d", -3.0, "PREFLOP", "Call 1 bb", "Fold", 0.0),
            ("h4", "Js Ts", -5.0, "FLOP", "Bet 3 bb", "Check", 0.0),
            ("h5", "9s 9d", -8.0, "PREFLOP", "Raise a 6 bb", "Raise a 9 bb", 0.0),   // solo el tamano: no es un patron
        });

        Assert.Equal(3, patterns.Count);
        var first = patterns[0];
        Assert.Equal("Pagas cuando tocaba no ir (después del flop)", first.Label);
        Assert.Equal(2, first.Count);
        Assert.Equal(-50, first.AdjustedBb, 3);
        Assert.Equal(new[] { "AKo", "QQ" }, first.Cards);
        Assert.Contains("precio", first.Tip);
        // A igual numero de veces, primero el que mas costo.
        Assert.Equal("Apuestas cuando tocaba pasar (después del flop)", patterns[1].Label);
        Assert.Equal("Pagas cuando tocaba no ir (antes del flop)", patterns[2].Label);
    }

    [Theory]
    [InlineData(new string[0], 50, HandQuality.Ungraded)]
    [InlineData(new[] { "Good", "Mistake" }, -25, HandQuality.VeryBad)]      // error caro sin la suerte
    [InlineData(new[] { "Mistake" }, -5, HandQuality.Bad)]
    [InlineData(new[] { "Mistake" }, 40, HandQuality.Bad)]                    // un error que gano sigue siendo malo
    [InlineData(new[] { "Good", "Good" }, 30, HandQuality.Excellent)]         // bote grande bien jugado
    [InlineData(new[] { "Good", "Marginal" }, 30, HandQuality.Good)]          // con una duda no es excelente
    [InlineData(new[] { "Good" }, 3, HandQuality.Good)]
    [InlineData(new[] { "Good" }, -12, HandQuality.Standard)]                 // bien jugada y perdida: estandar
    [InlineData(new[] { "Good" }, 0, HandQuality.Standard)]                   // retirarse a tiempo
    public void Cada_mano_se_califica_por_como_se_jugo_y_lo_que_dejo_sin_suerte(string[] grades, double adjustedBb, HandQuality expected) =>
        Assert.Equal(expected, RealTimeSessionReportService.QualityOf(grades, adjustedBb));

    private static string Hand(string id, string time, string flop) =>
        $"PokerStars Hand #{id}:  Hold'em No Limit (250/500) - {time} UTC [{time} ET]\n" +
        "Table 'Asta V' 6-max (Play Money) Seat #4 is the button\n" +
        "Seat 2: Heroe (50000 in chips) \n" +
        "Seat 3: Ana (40000 in chips) \n" +
        "Seat 4: Beto (50862 in chips) \n" +
        "Seat 5: Ciro (39000 in chips) \n" +
        "Ciro: posts small blind 250\n" +
        "Heroe: posts big blind 500\n" +
        "*** HOLE CARDS ***\n" +
        "Dealt to Heroe [Ah Kd]\n" +
        "Ana: calls 500\n" +
        "Beto: calls 500\n" +
        "Ciro: calls 250\n" +
        "Heroe: checks \n" +
        "*** FLOP *** [9c Kc Qh]\n" +
        flop +
        "*** SUMMARY ***\n" +
        "Total pot 2000 | Rake 100 \n" +
        "Board [9c Kc Qh]\n" +
        "Seat 2: Heroe (big blind) folded on the Flop\n" +
        "Seat 3: Ana folded on the Flop\n" +
        "Seat 4: Beto (button) collected (1900)\n" +
        "Seat 5: Ciro (small blind) folded on the Flop\n\n\n";

    [Fact]
    public void El_resumen_en_vivo_cuenta_las_manos_y_lo_ganado_del_ultimo_tramo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aph-rendimiento-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            // Sin manos corregidas: la prueba no lee el registro de revisiones de quien la ejecuta.
            TrainerReviewLog.UseFolder(Path.Combine(folder, "revisiones"));
            var path = Path.Combine(folder, "HH20261120 Asta V - 250-500 - Dinero ficticio Hold'em Sin límite.txt");
            const string allFold = "Ciro: checks \nHeroe: checks \nAna: checks \nBeto: bets 1000\nCiro: folds \nHeroe: folds \nAna: folds \n" +
                                   "Uncalled bet (1000) returned to Beto\nBeto collected 1900 from pot\n";
            File.WriteAllText(path,
                Hand("262760000001", "2026/11/20 9:00:00", allFold) +
                Hand("262760000002", "2026/11/20 9:01:30", allFold));

            var summary = RealTimeSessionReportService.BuildLiveSummary(new[] { ("Asta V", path, (string?)"Heroe") });

            Assert.NotNull(summary);
            Assert.Equal(2, summary!.Hands);
            Assert.Equal(2, summary.Curve.Count);
            Assert.Equal(-2, summary.NetBb, 2);          // pago la ciega grande en las dos y se retiro
            Assert.Equal(0, summary.LuckBb, 3);          // sin all-in no hay suerte
            Assert.Equal(-100, summary.BbPer100, 1);
            Assert.Equal(0, summary.Decisions);
            Assert.Empty(summary.Patterns);
            // AK con pareja de K en 9-K-Q le gana a casi todo (~4% le gana): mano fuerte, y en las dos paso.
            Assert.Equal(2, summary.StrongSpots);
            Assert.Equal(0, summary.StrongBets);
            Assert.Equal(0, summary.StrongBetPct);
            Assert.Equal(2, summary.HandLines.Count);
            Assert.All(summary.HandLines, hand =>
            {
                Assert.Equal(HandQuality.Ungraded, hand.Quality);   // sin decisiones corregidas
                Assert.Equal("AKo", hand.Cards);
                Assert.Equal(-1, hand.NetBb, 2);
            });
        }
        finally
        {
            TrainerReviewLog.UseFolder(null);
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
