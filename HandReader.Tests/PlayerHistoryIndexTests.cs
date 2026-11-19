using System.Text;
using HandReader.Core.Models;
using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// HUD sobre la mesa: lo que ya jugaste con un rival en tus otras mesas se suma a la mesa en vivo,
/// sin contar dos veces las manos de la propia mesa, y la capsula toma el color de la muestra.
/// </summary>
public class PlayerHistoryIndexTests
{
    private static string[] Hand(long id, string table, string villain, bool villainRaises) => new[]
    {
        $"PokerStars Hand #{id}:  Hold'em No Limit (100/200) - 2026/11/18 6:10:00 UTC [2026/11/18 1:10:00 ET]",
        $"Table '{table}' 6-max (Play Money) Seat #1 is the button",
        $"Seat 1: {villain} (20000 in chips) ",
        "Seat 2: Otro (20000 in chips) ",
        "Seat 3: Heroe (20000 in chips) ",
        "Otro: posts small blind 100",
        "Heroe: posts big blind 200",
        "*** HOLE CARDS ***",
        "Dealt to Heroe [8c 3d]",
        villainRaises ? $"{villain}: raises 400 to 600" : $"{villain}: folds ",
        "Otro: folds ",
        villainRaises ? "Heroe: folds " : "Heroe: checks ",
        villainRaises ? $"Uncalled bet (400) returned to {villain}" : "Otro collected 200 from pot",
        villainRaises ? $"{villain} collected 500 from pot" : "",
        "*** SUMMARY ***",
        "Total pot 500 | Rake 0",
        "",
    };

    private static string WriteTable(string directory, string table, int hands, string villain, bool raises)
    {
        var lines = new List<string>();
        for (var i = 0; i < hands; i++)
            lines.AddRange(Hand(1000 + i, table, villain, raises));
        var path = Path.Combine(directory, $"HH20261118 {table} - No Limit Hold'em.txt");
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
        return path;
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aph-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void Suma_las_otras_mesas_pero_no_la_propia()
    {
        var directory = NewDirectory();
        try
        {
            var here = WriteTable(directory, "Mesa A", 5, "Conocido", raises: false);
            WriteTable(directory, "Mesa B", 40, "Conocido", raises: true);
            WriteTable(directory, "Mesa C", 10, "Conocido", raises: false);

            PlayerHistoryIndex.ScanNow(directory);
            var elsewhere = PlayerHistoryIndex.Elsewhere(here, "Conocido");

            Assert.NotNull(elsewhere);
            Assert.Equal(50, elsewhere!.HandsReceived);   // 40 + 10: las 5 de esta mesa ya las lee la mesa en vivo
            Assert.Equal(40, elsewhere.PfrCount);
            Assert.Null(PlayerHistoryIndex.Elsewhere(here, "Nuevo"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Un_rival_solo_de_esta_mesa_no_tiene_historial()
    {
        var directory = NewDirectory();
        try
        {
            var here = WriteTable(directory, "Mesa A", 5, "SoloAqui", raises: false);
            WriteTable(directory, "Mesa B", 5, "OtroRival", raises: false);

            PlayerHistoryIndex.ScanNow(directory);

            Assert.Null(PlayerHistoryIndex.Elsewhere(here, "SoloAqui"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Una_mesa_que_crece_se_vuelve_a_leer()
    {
        var directory = NewDirectory();
        try
        {
            var here = WriteTable(directory, "Mesa A", 1, "Rival", raises: false);
            var other = WriteTable(directory, "Mesa B", 3, "Rival", raises: false);
            PlayerHistoryIndex.ScanNow(directory);
            Assert.Equal(3, PlayerHistoryIndex.Elsewhere(here, "Rival")!.HandsReceived);

            File.AppendAllLines(other, Hand(5000, "Mesa B", "Rival", true));
            PlayerHistoryIndex.ScanNow(directory);

            Assert.Equal(4, PlayerHistoryIndex.Elsewhere(here, "Rival")!.HandsReceived);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void La_mesa_en_vivo_mas_el_historial_suma_contadores_y_guarda_las_ultimas_manos_de_la_mesa()
    {
        var live = new PlayerStats("Rival");
        live.BumpHand();
        live.BumpHand();
        live.IncVPIP();
        live.PushCell("AsKd");
        var history = new PlayerStats("Rival");
        for (var i = 0; i < 8; i++)
            history.BumpHand();
        history.IncVPIP();
        history.IncPFR();

        var sum = live.CombinedWith(history);

        Assert.Equal(10, sum.HandsReceived);
        Assert.Equal(20, sum.VPIPPct, 3);
        Assert.Equal(10, sum.PFRPct, 3);
        Assert.Equal(new[] { "AsKd" }, sum.LastHands);
        Assert.Equal(2, live.HandsReceived); // la mesa en vivo no cambia
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(166, true)]
    [InlineData(12000, true)]
    public void Hay_color_desde_30_manos_contigo(int hands, bool colored) =>
        Assert.Equal(colored, PlayerHistoryIndex.SampleColor(hands).HasValue);

    [Fact]
    public void Cada_tramo_de_manos_tiene_su_propio_color()
    {
        var colors = new[] { 30, 100, 200, 500, 1000, 5000, 10000 }
            .Select(hands => PlayerHistoryIndex.SampleColor(hands)!.Value)
            .ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
    }
}
