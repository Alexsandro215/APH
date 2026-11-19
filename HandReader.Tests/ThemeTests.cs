using System.Text.RegularExpressions;
using Xunit;

namespace HandReader.Tests;

/// <summary>
/// WPF congela los pinceles del tema y, al cambiar de aspecto, APH pone pinceles nuevos. Lo que
/// pide un color del tema con StaticResource se queda con el del tema anterior (fondos verdes con
/// el tema azul, por ejemplo): en las vistas esos colores van siempre con DynamicResource.
/// </summary>
public class ThemeTests
{
    private static readonly string[] ThemeKeys =
    {
        "Col.Bg", "Col.Panel", "Col.Card", "Col.Border", "Col.Text", "Col.TextDim", "Col.Accent",
        "Col.Heart", "Col.Diamond", "Col.Club", "Col.Glow", "Col.GlowTransparent",
        "Brush.Bg", "Brush.Panel", "Brush.Card", "Brush.Border", "Brush.Text", "Brush.TextDim",
        "Brush.Accent", "Brush.Club", "Brush.Surface", "Brush.SurfaceText", "Brush.Header",
        "Brush.HeaderBorder", "Brush.CardHover", "Brush.RowHover", "Brush.RowSelected",
        "Brush.AccentSoft", "Brush.Positive", "Brush.Negative", "Brush.Warning", "Brush.Inset",
        "Brush.Glow", "Brush.GlowTransparent", "Brush.AppShell",
        "BgDark", "PanelDark", "BorderDark", "FgLight", "FgDim", "FgAccent", "BtnBg", "BtnBgHover",
        "HeroRow", "HeroRowSelected", "HeroRowAccent",
    };

    [Fact]
    public void Las_vistas_piden_los_colores_del_tema_con_DynamicResource()
    {
        var root = FindAppFolder();
        var pattern = new Regex(@"StaticResource (" + string.Join("|", ThemeKeys.Select(Regex.Escape)) + @")\}");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/") || normalized.EndsWith("Styles/PokerTheme.xaml"))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(offenders.Count == 0, "Colores del tema con StaticResource:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Cada_aspecto_pone_todos_los_colores_del_tema()
    {
        // Las claves que el tema define en XAML y que el cambio de aspecto debe actualizar.
        var source = File.ReadAllText(Path.Combine(FindAppFolder(), "Services", "ThemePaletteManager.cs"));
        foreach (var key in ThemeKeys.Where(k => k != "Brush.AppShell"))
            Assert.True(source.Contains($"\"{key}\""), $"ThemePaletteManager no actualiza {key}");
    }

    private static string FindAppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Hud.App")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Hud.App");
    }
}
