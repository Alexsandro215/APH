using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>Sesiones: juego seguido con pausas de menos de 30 minutos.</summary>
public class SessionSummaryTests
{
    [Fact]
    public void Una_pausa_larga_empieza_otra_sesion_y_la_mas_reciente_va_primero()
    {
        var day = new DateTime(2026, 11, 18);
        var hands = new List<SessionHand>
        {
            new(day.AddHours(10), "Mesa A", "Ah Kd", +20, 0),
            new(day.AddHours(10).AddMinutes(10), "Mesa B", "7c 2d", -5, 0),
            new(day.AddHours(10).AddMinutes(35), "Mesa A", "Qs Qh", +60, +40), // 25 min despues: misma sesion
            new(day.AddHours(14), "Mesa C", "As 4s", -80, -50),               // 3 h despues: otra sesion
        };

        var sessions = SessionSummaryService.Group(hands);

        Assert.Equal(2, sessions.Count);
        var afternoon = sessions[0];
        Assert.Equal(1, afternoon.Hands);
        Assert.Equal(-30, afternoon.AdjustedBb, 3); // perdio 80, 50 por mala suerte

        var morning = sessions[1];
        Assert.Equal(3, morning.Hands);
        Assert.Equal(2, morning.Tables);
        Assert.Equal(75, morning.NetBb, 3);
        Assert.Equal(35, morning.AdjustedBb, 3);
        Assert.Equal("Qs Qh", morning.Best!.Cards);
        Assert.Equal("7c 2d", morning.Worst!.Cards);
        Assert.Equal(TimeSpan.FromMinutes(35), morning.Duration);
    }
}
