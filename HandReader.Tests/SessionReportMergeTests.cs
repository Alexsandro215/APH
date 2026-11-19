using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Las stats del informe de sesion al juntar mesas: cada una sobre sus propias oportunidades. Antes
/// se promediaban los porcentajes por manos y una mesa sin ninguna c-bet afrontada contaba como
/// "0% de fold": el informe de la noche del 18-11 daba FvCB 35% cuando eran 16 de 26 (62%).
/// </summary>
public class SessionReportMergeTests
{
    private static RealTimePlayerReportRow Row(int hands, int faced, int folds, int cbetOpp, int cbetMade) =>
        new("Heroe", hands, 0, 0, 0, 0, 0, cbetOpp > 0 ? 100.0 * cbetMade / cbetOpp : 0, faced > 0 ? 100.0 * folds / faced : 0, 0, 0, 0)
        {
            Counts = new RealTimeStatCounts(hands, 0, 0, 0, 0, 0, 0, cbetMade, cbetOpp, folds, faced, 0, 0, 0, 0)
        };

    [Fact]
    public void Una_mesa_sin_oportunidades_no_hunde_el_porcentaje()
    {
        var merged = RealTimeSessionReportService.MergePlayerRows(new[]
        {
            Row(hands: 246, faced: 1, folds: 0, cbetOpp: 0, cbetMade: 0),
            Row(hands: 82, faced: 0, folds: 0, cbetOpp: 4, cbetMade: 3),
            Row(hands: 74, faced: 4, folds: 4, cbetOpp: 2, cbetMade: 1),
        });

        Assert.Equal(402, merged.Hands);
        Assert.Equal(80.0, merged.FoldVsCBet, 1);   // 4 de 5
        Assert.Equal(66.7, merged.CBet, 1);         // 4 de 6
    }

    /// <summary>
    /// Informe al terminar Tiempo real: solo el ultimo tramo seguido. Letaba IV jugada de 23:14 a 02:07 y
    /// otra vez desde 02:55: el informe de las 03:49 empezaba a las 23:14.
    /// </summary>
    [Fact]
    public void El_informe_de_tiempo_real_solo_toma_el_ultimo_tramo()
    {
        var t = new[]
        {
            new DateTime(2026, 11, 19, 5, 14, 0), new DateTime(2026, 11, 19, 6, 0, 0), new DateTime(2026, 11, 19, 8, 7, 0),
            new DateTime(2026, 11, 19, 8, 55, 11), new DateTime(2026, 11, 19, 9, 20, 0), new DateTime(2026, 11, 19, 9, 48, 12),
        };
        var (start, end) = RealTimeSessionReportService.LastStretch(t, TimeSpan.FromMinutes(30));
        Assert.Equal(new DateTime(2026, 11, 19, 8, 55, 11), start);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 48, 12), end);
    }

    /// <summary>Con 5 c-bets afrontadas el FvCB no se juzga (salia "a mejorar" y como siguiente enfoque).</summary>
    [Fact]
    public void Con_pocas_ocasiones_la_stat_no_se_juzga()
    {
        var input = new HeroStatInput(496, 24.6, 16.9, 5.9, 2.7, 67.8, 37.3, 20, 35.8, 51.6, 39.1)
        {
            Opportunities = new Dictionary<string, int> { ["FVCBF"] = 5, ["CBF"] = 40 }
        };
        var feedback = HeroStatCoach.Evaluate(input, playMoney: true);
        Assert.DoesNotContain(feedback, item => item.Key == "FVCBF");
        Assert.Contains(feedback, item => item.Key == "CBF");

        var noCounts = HeroStatCoach.Evaluate(input with { Opportunities = null }, playMoney: true);
        Assert.Contains(noCounts, item => item.Key == "FVCBF");
    }
}
