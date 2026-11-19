using System.Text.Json;
using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Revisa el archivo de calibracion (Hud.App/Data/population_baselines.json) que generan
/// APH.Calibration y las manos reales. Una calibracion rota pinta mal todo el HUD: por ejemplo,
/// la primera version tenia el W$SD en cero porque la base de datos no anota las ganancias de
/// PokerStars.
/// </summary>
public class BaselineTests
{
    private static readonly string[] HudKeys =
        { "VPIP", "PFR", "THREEBET", "AF", "AFQ", "CBF", "FVCBF", "WTSD", "WSD", "WWSF" };

    private static JsonElement Bucket(string name)
    {
        var json = JsonDocument.Parse(File.ReadAllText(PopulationBaselines.FilePath));
        return json.RootElement.GetProperty("buckets").GetProperty(name);
    }

    [Theory]
    [InlineData(StakeProfile.Low)]
    [InlineData(StakeProfile.Mid)]
    [InlineData(StakeProfile.High)]
    public void Cada_estadistica_del_HUD_tiene_7_cortes_ordenados(StakeProfile stake)
    {
        Assert.True(PopulationBaselines.IsAvailable, $"Falta {PopulationBaselines.FilePath}");
        foreach (var key in HudKeys)
        {
            Assert.True(PopulationBaselines.TryGetCuts(key, stake, out var cuts), $"Sin cortes para {key}");
            Assert.Equal(7, cuts.Length);
            for (var i = 1; i < cuts.Length; i++)
                Assert.True(cuts[i] >= cuts[i - 1], $"{key}: los cortes no van de menor a mayor");
            Assert.True(cuts[^1] > cuts[0], $"{key}: todos los cortes son iguales ({cuts[0]})");
        }
    }

    [Theory]
    [InlineData("Mid")]
    [InlineData("High")]
    public void Los_valores_tipicos_de_la_poblacion_son_creibles(string bucket)
    {
        var median = Bucket(bucket).GetProperty("quantiles");
        double P50(string key) => median.GetProperty(key).GetProperty("p50").GetDouble();

        Assert.InRange(P50("VPIP"), 15, 45);
        Assert.InRange(P50("PFR"), 5, 30);
        Assert.InRange(P50("WTSD"), 20, 40);
        Assert.InRange(P50("WSD"), 40, 60);   // se gana mas o menos la mitad de los showdowns
        Assert.InRange(P50("WWSF"), 35, 55);
        Assert.InRange(P50("CBF"), 45, 85);
    }

    [Fact]
    public void El_percentil_interpola_entre_los_cortes()
    {
        Assert.True(PopulationBaselines.TryGetCuts("VPIP", StakeProfile.Mid, out var cuts));
        Assert.Equal(cuts[3], PopulationBaselines.Percentile("VPIP", 50, -1), precision: 6);
        Assert.Equal(cuts[0], PopulationBaselines.Percentile("VPIP", 1, -1), precision: 6);
        Assert.Equal(cuts[6], PopulationBaselines.Percentile("VPIP", 99, -1), precision: 6);
        Assert.Equal((cuts[3] + cuts[4]) / 2, PopulationBaselines.Percentile("VPIP", 60, -1), precision: 6);
        Assert.Equal(-1, PopulationBaselines.Percentile("NO_EXISTE", 50, -1));
    }
}
