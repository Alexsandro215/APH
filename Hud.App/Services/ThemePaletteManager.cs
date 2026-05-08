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
                "Verde poker, panel oscuro y acentos azules.",
                ColorFrom("#0C1016"),
                ColorFrom("#0F141C"),
                ColorFrom("#141B24"),
                ColorFrom("#223040"),
                ColorFrom("#E7EEF5"),
                ColorFrom("#A4B8CB"),
                ColorFrom("#21C07A"),
                ColorFrom("#1AA1D1"),
                ColorFrom("#E24E5B"),
                ColorFrom("#E3B341")),
            new ThemePalette(
                "midnight",
                "Midnight Casino",
                "Azules profundos con verde neon moderado.",
                ColorFrom("#070B12"),
                ColorFrom("#0B1320"),
                ColorFrom("#111D2B"),
                ColorFrom("#26384E"),
                ColorFrom("#EDF6FF"),
                ColorFrom("#9FB6CE"),
                ColorFrom("#34D399"),
                ColorFrom("#38BDF8"),
                ColorFrom("#FB7185"),
                ColorFrom("#FACC15")),
            new ThemePalette(
                "ruby",
                "Ruby Felt",
                "Mesa elegante con acento rojo y contraste calido.",
                ColorFrom("#110D12"),
                ColorFrom("#181019"),
                ColorFrom("#211722"),
                ColorFrom("#3C2A3D"),
                ColorFrom("#FFF3F7"),
                ColorFrom("#C9AEBB"),
                ColorFrom("#F43F5E"),
                ColorFrom("#22C55E"),
                ColorFrom("#FB7185"),
                ColorFrom("#FBBF24")),
            new ThemePalette(
                "slate",
                "Slate Pro",
                "Neutral profesional, limpio y comodo para sesiones largas.",
                ColorFrom("#0B0F14"),
                ColorFrom("#101820"),
                ColorFrom("#16212B"),
                ColorFrom("#314253"),
                ColorFrom("#F1F5F9"),
                ColorFrom("#A9B7C7"),
                ColorFrom("#14B8A6"),
                ColorFrom("#60A5FA"),
                ColorFrom("#F87171"),
                ColorFrom("#EAB308"))
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
            SetBrush(resources, "Brush.Surface", Lighten(palette.Card, 0.88));
            SetBrush(resources, "Brush.SurfaceText", Colors.Black);
            SetBrush(resources, "Brush.Header", Darken(palette.Card, 0.82));
            SetBrush(resources, "Brush.HeaderBorder", Lighten(palette.Border, 1.15));
            SetBrush(resources, "Brush.Positive", palette.Accent);
            SetBrush(resources, "Brush.Negative", palette.Danger);
            SetBrush(resources, "Brush.Warning", palette.Gold);
            SetBrush(resources, "BgDark", Darken(palette.Background, 0.92));
            SetBrush(resources, "PanelDark", palette.Panel);
            SetBrush(resources, "BorderDark", palette.Border);
            SetBrush(resources, "FgLight", palette.Text);
            SetBrush(resources, "FgDim", palette.TextDim);
            SetBrush(resources, "FgAccent", palette.Secondary);
            SetBrush(resources, "BtnBg", palette.Secondary);
            SetBrush(resources, "BtnBgHover", Darken(palette.Secondary, 0.75));
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

