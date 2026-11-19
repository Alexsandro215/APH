using System.Windows;
using System.Windows.Media;

namespace Hud.App.Services
{
    /// <summary>
    /// Pinceles tomados del tema activo para el codigo que dibuja a mano (etiquetas, graficas,
    /// filas de acciones). Antes esos lugares usaban azules marino fijos que no cambiaban con la
    /// paleta y chocaban con el tema verde. Devuelve pinceles congelados: se pueden crear en
    /// segundo plano y usarse en la interfaz.
    /// </summary>
    public static class ThemeBrushes
    {
        public static Color Color(string key, Color fallback)
        {
            var app = Application.Current;
            if (app is null)
                return fallback;

            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Color(key, fallback));

            return app.TryFindResource(key) is Color color ? color : fallback;
        }

        public static Brush Brush(string key, Color fallback) => Frozen(Color(key, fallback));

        /// <summary>Un pincel del tema por su clave (por ejemplo "HeroRow"), o el color de respaldo.</summary>
        public static Brush Resource(string key, Color fallback)
        {
            var app = Application.Current;
            if (app is null)
                return Frozen(fallback);
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Resource(key, fallback));
            return app.TryFindResource(key) as Brush ?? Frozen(fallback);
        }

        public static Brush Lighter(string key, Color fallback, double factor) =>
            Frozen(Scale(Color(key, fallback), factor));

        /// <summary>Fondo neutro de etiqueta o fila (tarjeta del tema).</summary>
        public static Brush NeutralBackground => Brush("Col.Card", System.Windows.Media.Color.FromRgb(28, 40, 54));

        /// <summary>Borde neutro un poco mas claro que el borde del tema, para que se distinga.</summary>
        public static Brush NeutralBorder => Lighter("Col.Border", System.Windows.Media.Color.FromRgb(86, 108, 132), 1.35);

        public static Brush Text => Brush("Col.Text", Colors.White);

        public static Brush TextDim => Brush("Col.TextDim", System.Windows.Media.Color.FromRgb(164, 184, 203));

        public static Brush Panel => Brush("Col.Panel", System.Windows.Media.Color.FromRgb(17, 24, 32));

        public static Brush Border => Brush("Col.Border", System.Windows.Media.Color.FromRgb(62, 76, 92));

        private static Color Scale(Color color, double factor) =>
            System.Windows.Media.Color.FromArgb(
                color.A,
                (byte)Math.Min(255, color.R * factor),
                (byte)Math.Min(255, color.G * factor),
                (byte)Math.Min(255, color.B * factor));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
