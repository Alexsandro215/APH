using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Tipo de jugador como lo muestra el HUD de la mesa, y ahora tambien las tablas de jugadores del
/// informe: el mismo nombre y color en los dos sitios.
/// </summary>
public class PlayerTypeTagTests
{
    [Fact]
    public void Con_pocas_manos_es_poca_muestra_y_de_15_a_29_una_sospecha()
    {
        Assert.Equal(LocalizationManager.Text("Hud.FewHands"), PlayerProfileClassifier.Tag(10, 80, 2, 0, 0.3).Label);

        var suspect = PlayerProfileClassifier.Tag(20, 80, 2, 0, 0.3);
        var sure = PlayerProfileClassifier.Tag(120, 80, 2, 0, 0.3);
        Assert.Equal(sure.Label + "?", suspect.Label);
        Assert.Equal(sure.Color, suspect.Color);
        // Hasta 100 manos sigue siendo una sospecha (con 30 el tipo exacto acierta 1 de cada 3).
        Assert.Equal(sure.Label + "?", PlayerProfileClassifier.Tag(60, 80, 2, 0, 0.3).Label);
        Assert.Equal(sure.Label, PlayerProfileClassifier.Tag(100, 80, 2, 0, 0.3).Label);
    }

    // Jugadores reales de la sesion del 20-11 (manos, VPIP, PFR, 3Bet, AF). Antes los cuatro primeros
    // salian "Maniac": bastaba con VPIP alto o con resubir mucho en pocas manos.
    [Theory]
    [InlineData(88, 86.4, 12.5, 6.1, 0.5, "Tag.LoosePassive")]   // raphaelam530: entra en todo y casi no apuesta
    [InlineData(102, 77.5, 15.7, 0, 1.3, "Tag.LoosePassive")]   // lovni24
    [InlineData(43, 69.8, 0, 0, 1.5, "Tag.LoosePassive")]       // dakota51563
    [InlineData(107, 19.6, 13.1, 13.3, 0.7, "Tight")]          // el heroe en Eurypylos VI: 3Bet alto con pocas manos
    [InlineData(90, 71.1, 20, 6.3, 2.7, "Maniac")]             // logos2002: suelto y aprieta
    [InlineData(44, 50, 11.4, 0, 5.3, "Maniac")]               // JWeberPonte: apuesta muchisimo
    [InlineData(55, 34.5, 27.3, 33.3, 0.6, "LAG")]             // el heroe en Eumaios VI
    public void Maniac_es_quien_juega_muchisimo_y_aprieta(int hands, double vpip, double pfr, double threeBet, double af, string expected)
    {
        var label = PlayerProfileClassifier.Classify(hands, vpip, pfr, threeBet, af);
        Assert.Equal(expected.StartsWith("Tag.") ? LocalizationManager.Text(expected) : expected, label);
    }

    [Fact]
    public void Un_jugador_que_entra_en_casi_todo_sin_subir_es_fish_en_verde()
    {
        // Como carloslee349 el 20-11: 125 manos, VPIP 72%, PFR 0,8%, AF 0,2.
        var (label, color) = PlayerProfileClassifier.Tag(125, 72, 0.8, 2.6, 0.2);
        Assert.Equal("Fish", label);
        Assert.Equal(System.Windows.Media.Color.FromRgb(46, 158, 91), color);
    }
}
