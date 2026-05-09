using System.Windows;
using System.Windows.Media;

namespace Hud.App.Services
{
    public sealed record ThemePalette(
        string Key,
        string Name,
        string Description,
        Color Background,
        Color Panel,
        Color Card,
        Color Border,
        Color Text,
        Color TextDim,
        Color Accent,
        Color Secondary,
        Color Danger,
        Color Gold);

    public static class ThemePaletteManager
    {
        public const string DefaultPaletteKey = "classic";

        public static IReadOnlyList<ThemePalette> Palettes { get; } = new[]
        {
            new ThemePalette(
                "classic",
                "Classic Poker",
                "Mesa tradicional verde con acentos azulados.",
                ColorFrom("#0C1016"), ColorFrom("#0F141C"), ColorFrom("#141B24"), ColorFrom("#223040"),
                ColorFrom("#E7EEF5"), ColorFrom("#A4B8CB"), ColorFrom("#21C07A"), ColorFrom("#1AA1D1"),
                ColorFrom("#E24E5B"), ColorFrom("#E3B341")),
            new ThemePalette(
                "midnight",
                "Midnight Casino",
                "Azules profundos con neón verde.",
                ColorFrom("#070B12"), ColorFrom("#0B1320"), ColorFrom("#111D2B"), ColorFrom("#26384E"),
                ColorFrom("#EDF6FF"), ColorFrom("#9FB6CE"), ColorFrom("#34D399"), ColorFrom("#38BDF8"),
                ColorFrom("#FB7185"), ColorFrom("#FACC15")),
            new ThemePalette(
                "ruby",
                "Ruby Felt",
                "Elegante mesa roja con acentos dorados.",
                ColorFrom("#110D12"), ColorFrom("#181019"), ColorFrom("#211722"), ColorFrom("#3C2A3D"),
                ColorFrom("#FFF3F7"), ColorFrom("#C9AEBB"), ColorFrom("#F43F5E"), ColorFrom("#22C55E"),
                ColorFrom("#FB7185"), ColorFrom("#FBBF24")),
            new ThemePalette(
                "emerald",
                "Emerald Jungle",
                "Verde bosque profundo y oro antiguo.",
                ColorFrom("#0A120D"), ColorFrom("#0E1A13"), ColorFrom("#14261C"), ColorFrom("#2A4D38"),
                ColorFrom("#E8F5E9"), ColorFrom("#A5D6A7"), ColorFrom("#66BB6A"), ColorFrom("#D4AF37"),
                ColorFrom("#EF5350"), ColorFrom("#FFD700")),
            new ThemePalette(
                "amethyst",
                "Amethyst Night",
                "Púrpura real con azul eléctrico.",
                ColorFrom("#0F0C16"), ColorFrom("#140F1F"), ColorFrom("#1D152C"), ColorFrom("#352A4F"),
                ColorFrom("#F3E5F5"), ColorFrom("#B39DDB"), ColorFrom("#9575CD"), ColorFrom("#4FC3F7"),
                ColorFrom("#FF4081"), ColorFrom("#FFD54F")),
            new ThemePalette(
                "arctic",
                "Arctic Frost",
                "Azules helados y contrastes blancos.",
                ColorFrom("#0D141A"), ColorFrom("#121B24"), ColorFrom("#1A2632"), ColorFrom("#2E4156"),
                ColorFrom("#F0F7FF"), ColorFrom("#A9C7E3"), ColorFrom("#64B5F6"), ColorFrom("#B0BEC5"),
                ColorFrom("#FF8A65"), ColorFrom("#FFF176")),
            new ThemePalette(
                "obsidian",
                "Obsidian Gold",
                "Negro puro con acentos de oro premium.",
                ColorFrom("#080808"), ColorFrom("#0F0F0F"), ColorFrom("#171717"), ColorFrom("#2B2B2B"),
                ColorFrom("#FAFAFA"), ColorFrom("#A3A3A3"), ColorFrom("#D4AF37"), ColorFrom("#525252"),
                ColorFrom("#C62828"), ColorFrom("#FFD700")),
            new ThemePalette(
                "cyberpunk",
                "Cyberpunk",
                "Oscuridad futurista con rosa y cian neón.",
                ColorFrom("#0A0510"), ColorFrom("#120A1D"), ColorFrom("#1B0E2B"), ColorFrom("#3A1E5C"),
                ColorFrom("#FCE4EC"), ColorFrom("#F06292"), ColorFrom("#FF00FF"), ColorFrom("#00FFFF"),
                ColorFrom("#FF4081"), ColorFrom("#FFFF00")),
            new ThemePalette(
                "coffee",
                "Coffee Shop",
                "Tonos café cálidos y crema relajante.",
                ColorFrom("#120F0D"), ColorFrom("#1A1613"), ColorFrom("#251F1B"), ColorFrom("#3D332D"),
                ColorFrom("#EFEBE9"), ColorFrom("#BCAAA4"), ColorFrom("#8D6E63"), ColorFrom("#D7CCC8"),
                ColorFrom("#A52A2A"), ColorFrom("#FFCC80")),
            new ThemePalette(
                "pink",
                "Pink Blossom",
                "Ambiente suave en tonos rosa y blanco.",
                ColorFrom("#1A1012"), ColorFrom("#221619"), ColorFrom("#2D1E22"), ColorFrom("#4A3238"),
                ColorFrom("#FFE4E8"), ColorFrom("#D4A5AF"), ColorFrom("#F06292"), ColorFrom("#F8BBD0"),
                ColorFrom("#E91E63"), ColorFrom("#FFB74D")),
            new ThemePalette(
                "winter",
                "Winter Sky",
                "Cielo invernal, azul claro y nieve.",
                ColorFrom("#0F172A"), ColorFrom("#1E293B"), ColorFrom("#334155"), ColorFrom("#475569"),
                ColorFrom("#F1F5F9"), ColorFrom("#94A3B8"), ColorFrom("#38BDF8"), ColorFrom("#BAE6FD"),
                ColorFrom("#FB7185"), ColorFrom("#E2E8F0"))
        };

        public static ThemePalette Get(string? key) =>
            Palettes.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ??
            Palettes.First(p => p.Key == DefaultPaletteKey);

        public static void Apply(string? key)
        {
            var palette = Get(key);
            var resources = Application.Current?.Resources;
            if (resources is null)
                return;

            SetColor(resources, "Col.Bg", palette.Background);
            SetColor(resources, "Col.Panel", palette.Panel);
            SetColor(resources, "Col.Card", palette.Card);
            SetColor(resources, "Col.Border", palette.Border);
            SetColor(resources, "Col.Text", palette.Text);
            SetColor(resources, "Col.TextDim", palette.TextDim);
            SetColor(resources, "Col.Accent", palette.Accent);
            SetColor(resources, "Col.Heart", palette.Danger);
            SetColor(resources, "Col.Diamond", palette.Gold);
            SetColor(resources, "Col.Club", palette.Secondary);

            SetBrush(resources, "Brush.Bg", palette.Background);
            SetBrush(resources, "Brush.Panel", palette.Panel);
            SetBrush(resources, "Brush.Card", palette.Card);
            SetBrush(resources, "Brush.Border", palette.Border);
            SetBrush(resources, "Brush.Text", palette.Text);
            SetBrush(resources, "Brush.TextDim", palette.TextDim);
            SetBrush(resources, "Brush.Accent", palette.Accent);
            SetBrush(resources, "Brush.Surface", Lighten(palette.Panel, 1.35));
            SetBrush(resources, "Brush.SurfaceText", Colors.White);
            SetBrush(resources, "Brush.Header", Darken(palette.Card, 0.82));
            SetBrush(resources, "Brush.HeaderBorder", Lighten(palette.Border, 1.15));
            SetBrush(resources, "Brush.Positive", palette.Accent);
            SetBrush(resources, "Brush.Negative", palette.Danger);
            SetBrush(resources, "Brush.Warning", palette.Gold);
            SetBrush(resources, "BgDark", palette.Panel);
            SetBrush(resources, "Brush.Inset", palette.Panel);
            SetBrush(resources, "PanelDark", palette.Panel);
            SetBrush(resources, "BorderDark", palette.Border);
            SetBrush(resources, "FgLight", palette.Text);
            SetBrush(resources, "FgDim", palette.TextDim);
            SetBrush(resources, "FgAccent", palette.Secondary);
            SetBrush(resources, "BtnBg", palette.Secondary);
            SetBrush(resources, "BtnBgHover", Darken(palette.Secondary, 0.75));
            SetBrush(resources, "Brush.Glow", WithAlpha(palette.Accent, 0x45));
            SetBrush(resources, "Brush.GlowTransparent", WithAlpha(palette.Accent, 0x00));
            SetColor(resources, "Col.Glow", WithAlpha(palette.Accent, 0x45));
            SetColor(resources, "Col.GlowTransparent", WithAlpha(palette.Accent, 0x00));
        }

        private static void SetColor(ResourceDictionary resources, string key, Color color)
        {
            if (resources.Contains(key))
                resources[key] = color;
        }

        private static void SetBrush(ResourceDictionary resources, string key, Color color)
        {
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
                return;
            }

            resources[key] = new SolidColorBrush(color);
        }

        private static Color WithAlpha(Color color, byte alpha) =>
            Color.FromArgb(alpha, color.R, color.G, color.B);

        private static Color Darken(Color color, double factor) =>
            Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

        private static Color Lighten(Color color, double factor) =>
            Color.FromRgb(
                (byte)Math.Min(255, color.R * factor),
                (byte)Math.Min(255, color.G * factor),
                (byte)Math.Min(255, color.B * factor));

        private static Color ColorFrom(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex);
    }
}

