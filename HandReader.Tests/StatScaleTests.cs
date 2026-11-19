using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// La escala unica de colores de las stats: normal es entre los percentiles 30 y 70 de la poblacion,
/// azul por debajo y naranja/rojo por encima, sin invertirse nunca, y gris con menos de 30 manos.
/// </summary>
public class StatScaleTests
{
    [Fact]
    public void La_mediana_de_la_poblacion_es_normal_en_todas_las_stats()
    {
        foreach (var key in StatScale.Keys)
        {
            var cuts = StatScale.Cuts(key, StakeProfile.Low);
            Assert.Equal(7, cuts.Length);
            Assert.Equal(StatLevel.Normal, StatScale.Level(key, cuts[3], 500));
        }
    }

    [Fact]
    public void Los_niveles_siguen_los_percentiles_5_15_30_70_85_95()
    {
        var cuts = StatScale.Cuts("VPIP", StakeProfile.Low);
        Assert.Equal(StatLevel.VeryLow, StatScale.Level("VPIP", cuts[0] - 0.1, 500));
        Assert.Equal(StatLevel.Low, StatScale.Level("VPIP", (cuts[0] + cuts[1]) / 2, 500));
        Assert.Equal(StatLevel.SlightlyLow, StatScale.Level("VPIP", (cuts[1] + cuts[2]) / 2, 500));
        Assert.Equal(StatLevel.Normal, StatScale.Level("VPIP", (cuts[2] + cuts[4]) / 2, 500));
        Assert.Equal(StatLevel.SlightlyHigh, StatScale.Level("VPIP", (cuts[4] + cuts[5]) / 2, 500));
        Assert.Equal(StatLevel.High, StatScale.Level("VPIP", (cuts[5] + cuts[6]) / 2, 500));
        Assert.Equal(StatLevel.VeryHigh, StatScale.Level("VPIP", cuts[6] + 5, 500));
    }

    [Theory]
    [InlineData("FVCBF")]
    [InlineData("WSD")]
    public void FvCB_y_WSD_ya_no_van_al_reves(string key)
    {
        var cuts = StatScale.Cuts(key, StakeProfile.Low);
        Assert.Equal(StatLevel.VeryHigh, StatScale.Level(key, cuts[6] + 5, 500));
        Assert.Equal(StatLevel.VeryLow, StatScale.Level(key, cuts[0] - 5, 500));
    }

    [Fact]
    public void Con_menos_de_30_manos_no_hay_color()
    {
        Assert.Equal(StatLevel.NoSample, StatScale.Level("VPIP", 80, 29));
        Assert.Null(StatScale.ColorOf(StatLevel.NoSample));
        Assert.Equal(StatLevel.VeryHigh, StatScale.Level("VPIP", 80, 30));
    }

    [Fact]
    public void Cada_nivel_tiene_su_color_y_el_texto_se_lee_encima()
    {
        var colors = StatScale.Scale.Select(level => StatScale.ColorOf(level)!.Value).ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
        foreach (var level in StatScale.Scale)
            Assert.True(Contrast(StatScale.ColorOf(level)!.Value, StatScale.InkOf(level)) >= 4.5, level.ToString());
    }

    [Fact]
    public void El_tooltip_dice_el_nivel_y_que_significa()
    {
        var text = StatScale.Describe("VPIP", 80, 200);
        Assert.Contains("VPIP", text);
        Assert.Contains("muy alto", text);
        Assert.Contains("suelto", text);
        Assert.Contains("30", StatScale.Describe("VPIP", 80, 12));
    }

    private static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Luminance(System.Windows.Media.Color c) =>
            0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
