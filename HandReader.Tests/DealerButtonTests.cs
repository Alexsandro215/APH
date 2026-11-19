using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Boton del dealer visto en la mesa: un disco claro con la "D" roja junto a una placa.</summary>
public class DealerButtonTests
{
    private static CapturedFrame Felt(int w = 692, int h = 477)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;      // B
            pixels[i + 1] = 120; // G
            pixels[i + 2] = 40;  // R
        }
        return new CapturedFrame(w, h, pixels);
    }

    private static void Fill(CapturedFrame frame, Func<int, int, bool> inside, byte r, byte g, byte b)
    {
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                if (!inside(x, y)) continue;
                var i = (y * frame.Width + x) * 4;
                frame.Pixels[i] = b;
                frame.Pixels[i + 1] = g;
                frame.Pixels[i + 2] = r;
            }
    }

    [Fact]
    public void Encuentra_el_boton_junto_a_la_placa_de_la_izquierda()
    {
        // Como en Zwetana V: el boton junto a WarFlatt (placa 1).
        var frame = Felt();
        Fill(frame, (x, y) => (x - 143) * (x - 143) + (y - 219) * (y - 219) <= 100, 220, 220, 220);
        Fill(frame, (x, y) => Math.Abs(x - 143) <= 6 && Math.Abs(y - 219) <= 4, 210, 30, 35);

        Assert.Equal(1, LiveTableReader.DealerPlate(frame));
    }

    [Fact]
    public void Un_corazon_en_una_carta_blanca_no_es_el_boton()
    {
        var frame = Felt();
        Fill(frame, (x, y) => Math.Abs(x - 330) <= 18 && Math.Abs(y - 310) <= 25, 245, 245, 245);
        Fill(frame, (x, y) => Math.Abs(x - 330) <= 6 && Math.Abs(y - 310) <= 4, 210, 30, 35);

        Assert.Null(LiveTableReader.DealerPlate(frame));
    }
}
