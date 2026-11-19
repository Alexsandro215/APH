using Hud.App.Services.Trainer;
using Xunit;

namespace HandReader.Tests;

/// <summary>Posicion en vivo con quien juega ahora en la mesa, no solo con el historial de la mano anterior.</summary>
public class LivePositionTests
{
    /// <summary>
    /// Mintaka IV: en la mano anterior el boton era el asiento 5 y jugaban 1, 2, 4, 5 y 6. En esta
    /// cartier478 (asiento 2) se quedo ausente: el boton pasa al 6, la SB es el 1 y el heroe (4) la BB.
    /// Con los asientos de la mano anterior salia "HJ".
    /// </summary>
    [Fact]
    public void Un_jugador_que_se_ausenta_recorre_las_posiciones()
    {
        var live = TrainerSession.ComputeLivePositions(5, 4, new[] { 1, 4, 5, 6 }, 1, preflop: true, canCheck: false, toCall: 2)!.Value;
        Assert.Equal("BB", live.Hero);

        var stale = TrainerSession.ComputeLivePositions(5, 4, new[] { 1, 2, 4, 5, 6 }, 1, preflop: true, canCheck: false, toCall: 2)!.Value;
        Assert.Equal("HJ", stale.Hero);
    }

    [Fact]
    public void Si_el_historial_llega_tarde_el_boton_avanza_una_vez_por_mano()
    {
        // Seis jugadores, boton en el 1, heroe en el 4: la mano siguiente el heroe es BB (boton 2);
        // si ya se repartio otra mano mas, es SB (boton 3).
        Assert.Equal("BB", TrainerSession.ComputeLivePositions(1, 4, new[] { 1, 2, 3, 4, 5, 6 }, 1, false, false, null)!.Value.Hero);
        Assert.Equal("SB", TrainerSession.ComputeLivePositions(1, 4, new[] { 1, 2, 3, 4, 5, 6 }, 2, false, false, null)!.Value.Hero);
    }

    [Fact]
    public void Preflop_poder_pasar_es_la_BB_y_pagar_media_ciega_la_SB()
    {
        Assert.Equal("BB", TrainerSession.ComputeLivePositions(1, 4, new[] { 1, 2, 3, 4, 5, 6 }, 3, true, canCheck: true, toCall: null)!.Value.Hero);
        Assert.Equal("SB", TrainerSession.ComputeLivePositions(1, 4, new[] { 1, 2, 3, 4, 5, 6 }, 1, true, canCheck: false, toCall: 0.5)!.Value.Hero);
    }

    /// <summary>
    /// Oceax V: tres ausentes (sus placas solo mostraban el stack). Juegan 2 (heroe), 3 y 6; el boton
    /// pasa del 2 al 3, la SB es el 6 y el heroe la BB. Contando las seis placas salia "CO".
    /// </summary>
    [Fact]
    public void Con_tres_ausentes_juegan_tres_y_el_heroe_es_la_BB()
    {
        Assert.Equal("BB", TrainerSession.ComputeLivePositions(2, 2, new[] { 3, 6 }, 1, true, false, 12)!.Value.Hero);
        Assert.Equal("CO", TrainerSession.ComputeLivePositions(2, 2, new[] { 1, 3, 4, 5, 6 }, 1, true, false, 12)!.Value.Hero);
    }
}
