using System.Globalization;
using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Cifras que suben contando: se lee la cifra del texto y los pasos intermedios se escriben con la
/// misma forma (signo, decimales, separador de miles y lo que va alrededor).
/// </summary>
public class UiMotionTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    [Theory]
    [InlineData("+1,234.5 bb", 1234.5)]
    [InlineData("-3 bb/100", -3)]
    [InlineData("48.8%", 48.8)]
    [InlineData("12345", 12345)]
    [InlineData("0 bb/100", 0)]
    public void Lee_la_primera_cifra_del_texto(string text, double expected)
    {
        Assert.True(CountUpText.TryParse(text, Us, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("Hypatia VI")]
    [InlineData("")]
    [InlineData(null)]
    public void Sin_cifra_no_hay_conteo(string? text)
    {
        Assert.False(CountUpText.TryParse(text, Us, out _));
    }

    [Fact]
    public void Lee_cifras_con_coma_decimal()
    {
        Assert.True(CountUpText.TryParse("1.234,5 bb", Es, out var value));
        Assert.Equal(1234.5, value, 6);
    }

    [Theory]
    [InlineData("+1,234.5 bb", 617.25, "+617.3 bb")]
    [InlineData("+1,234.5 bb", 1500, "+1,500.0 bb")]
    [InlineData("12,345", 950, "950")]
    [InlineData("12345", 950, "950")]
    [InlineData("+12 bb", -3.4, "-3 bb")]
    [InlineData("-34.5 bb", -10.04, "-10.0 bb")]
    [InlineData("-34.5 bb", 0, "0.0 bb")]
    [InlineData("48.8%", 24.44, "24.4%")]
    [InlineData("Hypatia VI", 5, "Hypatia VI")]
    public void Escribe_el_paso_intermedio_con_la_forma_del_final(string template, double value, string expected)
    {
        Assert.Equal(expected, CountUpText.Format(template, value, Us));
    }

    [Fact]
    public void El_ultimo_paso_es_el_texto_final()
    {
        const string final = "+1,234.5 bb";
        Assert.True(CountUpText.TryParse(final, Us, out var target));
        Assert.Equal(final, CountUpText.Format(final, target, Us));
    }

    [Theory]
    [InlineData("Normal", MotionLevel.Normal)]
    [InlineData("Reduced", MotionLevel.Reduced)]
    [InlineData("reducidas", MotionLevel.Reduced)]
    [InlineData("Off", MotionLevel.Off)]
    [InlineData("apagadas", MotionLevel.Off)]
    [InlineData(null, MotionLevel.Normal)]
    [InlineData("cualquiera", MotionLevel.Normal)]
    public void Nivel_de_animaciones_desde_el_ajuste(string? value, MotionLevel expected)
    {
        Assert.Equal(expected, UiMotion.Parse(value));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.3, 1.25)]
    [InlineData(0.5, 0.9)]
    [InlineData(1.12, 1.1)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Tamano_de_interfaz_siempre_permitido(double raw, double expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeUiScale(raw), 6);
    }

    [Fact]
    public void Los_temas_tienen_claves_unicas_y_descripcion()
    {
        var palettes = ThemePaletteManager.Palettes;
        Assert.Equal(palettes.Count, palettes.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(palettes, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.Contains(palettes, p => p.Key == "contrast");
        Assert.Contains(palettes, p => p.Key == "royal");
    }
}
