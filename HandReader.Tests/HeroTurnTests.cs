using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Turno del heroe: la barra de botones grandes de abajo a la derecha se ve roja.</summary>
public class HeroTurnTests
{
    private const int W = 624, H = 430;

    private static CapturedFrame Dark()
    {
        var pixels = new byte[W * H * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20;
            pixels[i + 1] = 20;
            pixels[i + 2] = 20;
        }
        return new CapturedFrame(W, H, pixels);
    }

    /// <summary>Botones de rojo oscuro en degradado, de ~(160,20,0) arriba a ~(80,0,0) abajo.</summary>
    private static void Button(CapturedFrame frame, int left, int right, int top = 387, int bottom = 427,
        Func<double, (byte R, byte G, byte B)>? color = null)
    {
        color ??= t => ((byte)(160 - 80 * t), (byte)(20 - 20 * t), 0);
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= Math.Min(right, W - 1); x++)
            {
                var (r, g, b) = color((y - top) / (double)(bottom - top));
                var i = (y * W + x) * 4;
                frame.Pixels[i] = b;
                frame.Pixels[i + 1] = g;
                frame.Pixels[i + 2] = r;
            }
    }

    [Fact]
    public void Con_el_tema_de_botones_rojo_oscuro_se_ve_el_turno()
    {
        // 20-11-2026: tapete verde sobre fondo negro; con solo el rojo intenso la barra daba 14% con
        // "Pasar / Apostar" y 34% con "No ir / Igualar / Subir": nunca se veia el turno.
        var twoButtons = Dark();
        Button(twoButtons, 415, 512);
        Button(twoButtons, 515, 627);
        Assert.True(LiveTableReader.IsHeroTurn(twoButtons));

        var threeButtons = Dark();
        Button(threeButtons, 310, 407);
        Button(threeButtons, 415, 512);
        Button(threeButtons, 515, 627);
        Assert.True(LiveTableReader.IsHeroTurn(threeButtons));
    }

    [Fact]
    public void Fuera_de_turno_o_con_dorsos_de_cartas_no_es_turno()
    {
        Assert.False(LiveTableReader.IsHeroTurn(Dark()));

        // El rojo apagado de los dorsos de las cartas no es un boton.
        var backs = Dark();
        Button(backs, 415, 512, color: _ => (156, 67, 66));
        Button(backs, 515, 627, color: _ => (156, 67, 66));
        Assert.False(LiveTableReader.IsHeroTurn(backs));
    }
}
