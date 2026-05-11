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
        public static event EventHandler? PaletteApplied;

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
                "Azul medianoche profundo con platino y neon menta.",
                ColorFrom("#05070A"), ColorFrom("#0A0D14"), ColorFrom("#121822"), ColorFrom("#202D3A"),
                ColorFrom("#E5E4E2"), ColorFrom("#9FB6CE"), ColorFrom("#00FF88"), ColorFrom("#38BDF8"),
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
                "Jade imperial profundo con oro antiguo y bronce.",
                ColorFrom("#050A08"), ColorFrom("#0A1410"), ColorFrom("#121F18"), ColorFrom("#203D30"),
                ColorFrom("#E8F5E9"), ColorFrom("#A5D6A7"), ColorFrom("#50C878"), ColorFrom("#CD7F32"),
                ColorFrom("#EF5350"), ColorFrom("#FFD700")),
            new ThemePalette(
                "amethyst",
                "Amethyst Night",
                "Purpura real con azul electrico.",
                ColorFrom("#0F0C16"), ColorFrom("#140F1F"), ColorFrom("#1D152C"), ColorFrom("#352A4F"),
                ColorFrom("#F3E5F5"), ColorFrom("#B39DDB"), ColorFrom("#9575CD"), ColorFrom("#4FC3F7"),
                ColorFrom("#FF4081"), ColorFrom("#FFD54F")),
            new ThemePalette(
                "arctic",
                "Arctic Frost",
                "Abismo glacial con blanco diamante y azul helado.",
                ColorFrom("#0A1116"), ColorFrom("#121B24"), ColorFrom("#1A2632"), ColorFrom("#2E4156"),
                ColorFrom("#FFFFFF"), ColorFrom("#A9C7E3"), ColorFrom("#00BFFF"), ColorFrom("#B0BEC5"),
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
                "Oscuridad futurista con rosa y cian neon.",
                ColorFrom("#0A0510"), ColorFrom("#120A1D"), ColorFrom("#1B0E2B"), ColorFrom("#3A1E5C"),
                ColorFrom("#FCE4EC"), ColorFrom("#F06292"), ColorFrom("#FF00FF"), ColorFrom("#00FFFF"),
                ColorFrom("#FF4081"), ColorFrom("#FFFF00")),
            new ThemePalette(
                "coffee",
                "Coffee Shop",
                "Espresso premium con crema y oro de caramelo.",
                ColorFrom("#0F0C0A"), ColorFrom("#1A1512"), ColorFrom("#251E1A"), ColorFrom("#3D332D"),
                ColorFrom("#F5F5DC"), ColorFrom("#BCAAA4"), ColorFrom("#D4AF37"), ColorFrom("#8D6E63"),
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
                ColorFrom("#FB7185"), ColorFrom("#E2E8F0")),
            new ThemePalette(
                "magma",
                "Magma Core",
                "Calor volcanico con rojos intensos y naranjas fundidos.",
                ColorFrom("#0D0202"), ColorFrom("#1A0505"), ColorFrom("#2E0B0B"), ColorFrom("#661414"),
                ColorFrom("#FFF5F5"), ColorFrom("#FF9999"), ColorFrom("#FF0000"), ColorFrom("#FF8000"),
                ColorFrom("#FFFF00"), ColorFrom("#FFD700")),
            new ThemePalette(
                "toxic",
                "Toxic Acid",
                "Ambiente radiactivo con verdes acidos y violetas toxicos.",
                ColorFrom("#050805"), ColorFrom("#0A120A"), ColorFrom("#121F12"), ColorFrom("#204020"),
                ColorFrom("#F0FFF0"), ColorFrom("#BFFF00"), ColorFrom("#39FF14"), ColorFrom("#BC13FE"),
                ColorFrom("#FF0055"), ColorFrom("#FFD700")),
            new ThemePalette(
                "mirage",
                "Copper Mirage",
                "Arcilla oscura con turquesas intensos y cobre quemado.",
                ColorFrom("#120D0A"), ColorFrom("#1A1410"), ColorFrom("#261D18"), ColorFrom("#4A3A30"),
                ColorFrom("#FDF5E6"), ColorFrom("#DEB887"), ColorFrom("#00CED1"), ColorFrom("#E97451"),
                ColorFrom("#FF4500"), ColorFrom("#FFD700")),
            new ThemePalette(
                "plasma",
                "Plasma Storm",
                "Tormenta electrica con amarillos fluor y violeta laser.",
                ColorFrom("#050510"), ColorFrom("#0A0A20"), ColorFrom("#101035"), ColorFrom("#202060"),
                ColorFrom("#F0F0FF"), ColorFrom("#A0A0FF"), ColorFrom("#FFFF00"), ColorFrom("#9400D3"),
                ColorFrom("#FF00FF"), ColorFrom("#FFD700")),
            new ThemePalette(
                "fireice",
                "Fire & Ice",
                "Duelo elemental entre rojo fuego y azul glaciar.",
                ColorFrom("#0A0D14"), ColorFrom("#141A26"), ColorFrom("#1C2533"), ColorFrom("#2E3B4E"),
                ColorFrom("#F0F4F8"), ColorFrom("#A0B0C0"), ColorFrom("#FF3333"), ColorFrom("#33CCFF"),
                ColorFrom("#FF0000"), ColorFrom("#0066FF")),
            new ThemePalette(
                "noir",
                "Noir Absolute",
                "Minimalismo extremo en blanco y negro puro.",
                ColorFrom("#050505"), ColorFrom("#0A0A0A"), ColorFrom("#121212"), ColorFrom("#2A2A2A"),
                ColorFrom("#FFFFFF"), ColorFrom("#A0A0A0"), ColorFrom("#FFFFFF"), ColorFrom("#666666"),
                ColorFrom("#FF3333"), ColorFrom("#FFFFFF")),
            new ThemePalette(
                "poison",
                "Poison Ivy",
                "Verdes de jungla profunda con violetas misticos.",
                ColorFrom("#080A08"), ColorFrom("#101410"), ColorFrom("#182218"), ColorFrom("#2A402A"),
                ColorFrom("#F0FFF0"), ColorFrom("#A0C0A0"), ColorFrom("#00FF41"), ColorFrom("#9D00FF"),
                ColorFrom("#FF0055"), ColorFrom("#FFD700")),
            new ThemePalette(
                "stealth",
                "Stealth Volt",
                "Gris militar con acentos de amarillo electrico.",
                ColorFrom("#121417"), ColorFrom("#1A1D21"), ColorFrom("#24282D"), ColorFrom("#383E46"),
                ColorFrom("#E1E4E8"), ColorFrom("#94A3B8"), ColorFrom("#FFFF00"), ColorFrom("#FFD700"),
                ColorFrom("#FF3333"), ColorFrom("#FFD700"))
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
            SetBrush(resources, "Brush.Club", palette.Secondary);
            SetBrush(resources, "Brush.Surface", Lighten(palette.Panel, 1.35));
            SetBrush(resources, "Brush.SurfaceText", Colors.White);
            SetBrush(resources, "Brush.Header", Darken(palette.Card, 0.82));
            SetBrush(resources, "Brush.HeaderBorder", Lighten(palette.Border, 1.15));
            SetBrush(resources, "Brush.CardHover", Lighten(palette.Card, 1.18));
            SetBrush(resources, "Brush.RowHover", Lighten(palette.Panel, 1.28));
            SetBrush(resources, "Brush.RowSelected", WithAlpha(palette.Accent, 0x44));
            SetBrush(resources, "Brush.AccentSoft", WithAlpha(palette.Accent, 0x1E));
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
            PaletteApplied?.Invoke(null, EventArgs.Empty);
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
