using Hud.App.Services;
using Xunit;

namespace HandReader.Tests;

/// <summary>Enlace de una mesa abierta de PokerStars con su archivo de historial.</summary>
public class TableDetectionTests
{
    [Fact]
    public void La_mesa_se_enlaza_en_cuanto_aparece_su_historial()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aph-detect-" + Guid.NewGuid().ToString("N"));
        var player = Path.Combine(folder, "Heroe");
        Directory.CreateDirectory(player);
        try
        {
            var window = new PokerStarsTableWindow(IntPtr.Zero,
                "Terebellum IV - Hold'em Sin límite 100/200 Dinero ficticio - Sesión iniciada con el nombre de usuario Heroe",
                "Terebellum IV", "Heroe");

            // Recien sentado: PokerStars todavia no creo el archivo (lo crea al terminar la primera mano).
            Assert.Empty(PokerStarsWindowDetector.MatchHandHistories(new[] { window }, folder));

            var file = Path.Combine(player, "HH20261118 Terebellum IV - 100-200 - Dinero ficticio Hold'em Sin límite.txt");
            File.WriteAllText(file, "PokerStars Hand #1: Hold'em No Limit (100/200)");
            File.WriteAllText(Path.Combine(player, "HH20261118 Otra Mesa - 100-200 - Dinero ficticio.txt"), "x");

            var match = Assert.Single(PokerStarsWindowDetector.MatchHandHistories(new[] { window }, folder));
            Assert.Equal(file, match.HandHistoryPath);
            Assert.Equal("Heroe", match.Window.HeroName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
