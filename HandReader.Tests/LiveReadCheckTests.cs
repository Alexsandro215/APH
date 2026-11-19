using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Al terminar la mano se compara lo leido en pantalla con el historial.</summary>
public class LiveReadCheckTests
{
    [Fact]
    public void Si_todo_coincide_no_hay_aviso() =>
        Assert.Null(LiveReadCheck.Compare("Kh As", "Qh 7c 3d", "BB", "As Kh", "7c Qh 3d 7d 6h", "BB"));

    [Fact]
    public void La_posicion_mal_leida_se_marca()
    {
        var check = LiveReadCheck.Compare("9s 9h", "", "HJ", "9h 9s", "2h 3c 3h", "BB");
        Assert.Equal("posición leída HJ, era BB", check);
    }

    [Fact]
    public void Una_carta_mal_leida_se_marca()
    {
        var check = LiveReadCheck.Compare("Jh Ac", "7h Kc 5h 3h", "SB", "Jc Ac", "7h Kc 5h 3h", "SB");
        Assert.Equal("cartas leídas Jh Ac, eran Jc Ac", check);
    }

    [Fact]
    public void El_board_de_esa_calle_se_compara_y_el_flop_en_cualquier_orden()
    {
        Assert.Null(LiveReadCheck.Compare("5s 6s", "9s Ad 7c Kh", "CO", "5s 6s", "Ad 7c 9s Kh 6c", "CO"));
        var river = LiveReadCheck.Compare("5s 6s", "Ad 7c 9s Kh 8c", "CO", "5s 6s", "Ad 7c 9s Kh 6c", "CO");
        Assert.Equal("board leído Ad 7c 9s Kh 8c, era Ad 7c 9s Kh 6c", river);
    }

    [Fact]
    public void Sin_lectura_no_se_compara() =>
        Assert.Null(LiveReadCheck.Compare(null, null, null, "As Kh", "Qh 7c 3d", "BB"));
}
