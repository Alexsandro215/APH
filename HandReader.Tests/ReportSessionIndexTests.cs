using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// Lista de informes PDF de la pagina Sesiones: la misma sesion generada varias veces se ve una sola
/// vez (la copia mas reciente) y las horas del historial (UTC) se pasan a la hora local.
/// </summary>
public class ReportSessionIndexTests
{
    private static ReportSessionRecord Report(string file, DateTime created, DateTime? start, string result, int hands = 709, string hero = "UwU_Fantasy_UwU") =>
        new(0, file, created, start, start?.AddMinutes(116), 8, TimeSpan.FromMinutes(116), hero, result, hands);

    [Fact]
    public void La_misma_sesion_generada_varias_veces_sale_una_vez_con_la_copia_mas_reciente()
    {
        var start = new DateTime(2026, 11, 20, 13, 37, 7);
        var records = new[]
        {
            Report("a.pdf", new DateTime(2026, 11, 20, 9, 39, 0), start, "+134,897 fichas / +674.5 bb"),
            Report("b.pdf", new DateTime(2026, 11, 20, 11, 13, 0), start, "+134,897 fichas / +674.5 bb"),
            Report("c.pdf", new DateTime(2026, 11, 20, 10, 18, 0), start, "+134,897 fichas / +674.5 bb"),
            Report("d.pdf", new DateTime(2026, 11, 20, 12, 10, 0), new DateTime(2026, 11, 19, 12, 57, 51), "-$7.55 / -377.5 bb", 188, "_Alextsuu_"),
        };

        var list = ReportSessionIndexService.WithoutRepeats(records);

        Assert.Equal(2, list.Count);
        Assert.Equal("b.pdf", list[0].Record.PdfPath);
        Assert.Equal(3, list[0].Copies);
        Assert.Equal("d.pdf", list[1].Record.PdfPath);
        Assert.Equal(1, list[1].Copies);
    }

    [Fact]
    public void Con_otro_resultado_o_sin_hora_de_inicio_no_se_juntan()
    {
        var start = new DateTime(2026, 11, 20, 13, 37, 7);
        var records = new[]
        {
            Report("a.pdf", new DateTime(2026, 11, 20, 9, 0, 0), start, "+10 bb"),
            Report("b.pdf", new DateTime(2026, 11, 20, 9, 5, 0), start, "+12 bb"),
            Report("c.pdf", new DateTime(2026, 11, 20, 9, 6, 0), null, "-"),
            Report("e.pdf", new DateTime(2026, 11, 20, 9, 7, 0), null, "-"),
        };

        Assert.Equal(4, ReportSessionIndexService.WithoutRepeats(records).Count);
    }

    [Fact]
    public void La_hora_del_historial_se_pasa_a_hora_local()
    {
        var utc = new DateTime(2026, 11, 20, 13, 37, 7, DateTimeKind.Unspecified);
        var expected = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(expected, ReportSessionIndexService.ToLocal(utc));

        var local = new DateTime(2026, 11, 20, 7, 37, 7, DateTimeKind.Local);
        Assert.Equal(local, ReportSessionIndexService.ToLocal(local));
    }
}
