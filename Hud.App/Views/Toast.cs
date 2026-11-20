using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hud.App.Services;

namespace Hud.App.Views
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Avisos que aparecen abajo a la derecha de la ventana principal y se van solos, en lugar de una
    /// ventana que hay que cerrar. Solo para avisos informativos: las preguntas y los errores graves
    /// siguen en una ventana de dialogo. Un clic en el aviso lo cierra.
    /// </summary>
    public static class Toast
    {
        private const int MaxVisible = 4;
        private static Panel? _host;

        /// <summary>La ventana principal registra el panel donde se apilan los avisos.</summary>
        public static void Register(Panel host) => _host = host;

        /// <summary>Muestra un aviso. Devuelve false si no hay ventana principal (no se muestra nada).</summary>
        public static bool Show(string message, ToastKind kind = ToastKind.Info)
        {
            if (string.IsNullOrWhiteSpace(message) || _host is null || !_host.IsLoaded)
                return false;

            var toast = Build(message, kind);
            while (_host.Children.Count >= MaxVisible)
                _host.Children.RemoveAt(0);
            _host.Children.Add(toast);

            UiMotion.FadeIn(toast, 220);
            UiMotion.Slide(toast, 36, 0, 320, 0);

            var seconds = Math.Clamp(3 + message.Length / 28.0, 3.5, 9);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(toast);
            };
            toast.MouseLeftButtonUp += (_, _) =>
            {
                timer.Stop();
                Dismiss(toast);
            };
            // Mientras el raton esta encima, no se va.
            toast.MouseEnter += (_, _) => timer.Stop();
            toast.MouseLeave += (_, _) => timer.Start();
            timer.Start();
            return true;
        }

        private static void Dismiss(FrameworkElement toast)
        {
            if (toast.Parent is not Panel host)
                return;

            UiMotion.FadeOut(toast, 200, () => host.Children.Remove(toast));
        }

        private static FrameworkElement Build(string message, ToastKind kind)
        {
            var (glyph, brushKey) = kind switch
            {
                ToastKind.Success => ("", "Brush.Positive"),
                ToastKind.Warning => ("", "Brush.Warning"),
                ToastKind.Error => ("", "Brush.Negative"),
                _ => ("", "Brush.Accent")
            };

            var stripe = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0) };
            stripe.SetResourceReference(Border.BackgroundProperty, brushKey);
            DockPanel.SetDock(stripe, Dock.Left);

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            DockPanel.SetDock(icon, Dock.Left);

            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

            var content = new DockPanel { LastChildFill = true };
            content.Children.Add(stripe);
            content.Children.Add(icon);
            content.Children.Add(text);

            var toast = new Border
            {
                Child = content,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 10, 16, 10),
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = 380,
                MinWidth = 240,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.45,
                    Color = Colors.Black
                }
            };
            toast.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            toast.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            AutomationProperties.SetName(toast, message);
            AutomationProperties.SetLiveSetting(toast, AutomationLiveSetting.Polite);
            return toast;
        }
    }
}
