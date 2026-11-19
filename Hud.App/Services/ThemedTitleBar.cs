using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Hud.App.Services
{
    /// <summary>
    /// Barra de titulo de Windows con los colores del tema en todas las ventanas de APH.
    /// Antes la barra salia blanca sobre una app oscura. En Windows 11 se pinta con el color del
    /// panel del tema; en Windows 10 al menos se pone en modo oscuro. Si el sistema no lo admite,
    /// no pasa nada: la ventana queda como estaba.
    /// </summary>
    public static class ThemedTitleBar
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            // El estilo implicito de Window del tema no se aplica a las ventanas derivadas
            // (MainWindow, GainAnalysisWindow...): el texto sin color propio salia negro sobre
            // fondo oscuro (por ejemplo, los titulos de "Lectura" en Ganancia). Este estilo por
            // defecto les da el color de texto del tema a todas.
            var windowStyle = new Style(typeof(Window));
            windowStyle.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, new DynamicResourceExtension("Brush.Text")));
            windowStyle.Seal();
            FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(windowStyle));

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                    Apply(window);
            }));

            ThemePaletteManager.PaletteApplied += (_, _) =>
            {
                foreach (Window window in Application.Current.Windows)
                    Apply(window);
            };
        }

        public static void Apply(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                var dark = 1;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

                SetColor(hwnd, DwmwaCaptionColor, "Col.Panel");
                SetColor(hwnd, DwmwaTextColor, "Col.Text");
                SetColor(hwnd, DwmwaBorderColor, "Col.Border");
            }
            catch (Exception ex)
            {
                AppLog.Warn("No se pudo pintar la barra de titulo", ex);
            }
        }

        private static void SetColor(IntPtr hwnd, int attribute, string resourceKey)
        {
            if (Application.Current?.TryFindResource(resourceKey) is not Color color)
                return;

            // COLORREF: 0x00BBGGRR
            var value = color.R | (color.G << 8) | (color.B << 16);
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
    }
}
