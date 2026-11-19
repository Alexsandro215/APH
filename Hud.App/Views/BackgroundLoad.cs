using System.Windows;
using System.Windows.Controls;
using Hud.App.Services;

namespace Hud.App.Views
{
    /// <summary>
    /// Carga pesada de una ventana fuera del hilo de la interfaz.
    /// Antes las ventanas de analisis leian y procesaban todos los historiales en su constructor:
    /// con unas 8,000 manos la pantalla quedaba congelada cerca de 2 segundos, y con 50,000 mas
    /// de 6. Ahora la ventana aparece enseguida con un aviso de "Cargando manos..." y su
    /// contenido se desactiva hasta que los datos estan listos.
    /// </summary>
    public static class BackgroundLoad
    {
        /// <summary>
        /// Ejecuta <paramref name="work"/> en segundo plano y luego <paramref name="apply"/> en el
        /// hilo de la interfaz. Se llama desde el constructor, despues de InitializeComponent.
        /// <paramref name="work"/> no debe tocar controles: solo leer archivos y calcular.
        /// </summary>
        public static void Run<T>(Window window, Func<T> work, Action<T> apply)
        {
            var overlay = Attach(window, out var content);

            _ = RunCoreAsync(window, overlay, content, work, apply);
        }

        private static async Task RunCoreAsync<T>(
            Window window,
            FrameworkElement overlay,
            UIElement? content,
            Func<T> work,
            Action<T> apply)
        {
            try
            {
                var result = await Task.Run(work);
                apply(result);
            }
            catch (Exception ex)
            {
                AppLog.Error($"No se pudieron cargar los datos de {window.GetType().Name}", ex);
                MessageBox.Show(
                    window,
                    string.Format(LocalizationManager.Text("Common.LoadFailed"), ex.Message, AppLog.LogFolder),
                    "APH",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (overlay.Parent is Panel host)
                    host.Children.Remove(overlay);
                if (content is not null)
                    content.IsEnabled = true;
            }
        }

        /// <summary>
        /// Congela un pincel para poder crearlo en segundo plano y usarlo en la interfaz. Un
        /// pincel sin congelar pertenece al hilo que lo creo y la ventana fallaria al dibujarlo.
        /// </summary>
        public static System.Windows.Media.Brush Frozen(System.Windows.Media.Brush brush)
        {
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }

        // Envuelve el contenido de la ventana en un Grid con la capa de "cargando" encima. Se hace
        // antes de mostrar la ventana, asi el arbol visual no se reconstruye despues.
        private static FrameworkElement Attach(Window window, out UIElement? content)
        {
            content = window.Content as UIElement;

            var host = new Grid();
            window.Content = host;
            if (content is not null)
            {
                content.IsEnabled = false;
                host.Children.Add(content);
            }

            var text = new TextBlock
            {
                Text = LocalizationManager.Text("Common.LoadingHands"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

            var bar = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 260,
                Height = 6
            };
            bar.SetResourceReference(Control.ForegroundProperty, "Brush.Accent");
            bar.SetResourceReference(Control.BackgroundProperty, "Brush.Border");
            bar.BorderThickness = new Thickness(0);

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(text);
            panel.Children.Add(bar);

            var overlay = new Border { Child = panel };
            overlay.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
            overlay.Opacity = 0.92;

            host.Children.Add(overlay);
            return overlay;
        }
    }
}
