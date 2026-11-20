using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hud.App.Services;

namespace Hud.App.Views
{
    public enum TablesPromptAnswer
    {
        Start,
        Later,
        Never
    }

    /// <summary>
    /// Aviso pequeño, siempre encima, cuando APH ve mesas de PokerStars abiertas y Tiempo real esta
    /// cerrado: pregunta si lo arranca. No se lleva el foco (no interrumpe lo que escribes ni el clic en
    /// la mesa) y se coloca en la esquina de una pantalla donde no tape ninguna mesa. Si no se contesta
    /// en un minuto, cuenta como "Ahora no". Nunca arranca nada por su cuenta.
    /// </summary>
    public sealed class TablesDetectedPrompt : Window
    {
        private readonly TextBlock _body;
        private readonly IReadOnlyList<IntPtr> _tables;
        private readonly DispatcherTimer _timeout;
        private bool _answered;

        public event EventHandler<TablesPromptAnswer>? Answered;

        public TablesDetectedPrompt(int tableCount, IReadOnlyList<IntPtr> tableHandles)
        {
            _tables = tableHandles;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = 400;
            Title = LocalizationManager.Text("Rt.Prompt.Title");

            // Encabezado: la pica de APH y el titulo.
            var spade = new Path
            {
                Data = Geometry.Parse(SpadeLoader.SpadeData),
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            spade.SetResourceReference(Shape.FillProperty, "Brush.Bg");
            var logo = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Child = spade, Margin = new Thickness(0, 0, 12, 0) };
            logo.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");

            var title = new TextBlock
            {
                Text = LocalizationManager.Text("Rt.Prompt.Title"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(logo, Dock.Left);
            header.Children.Add(logo);
            header.Children.Add(title);

            _body = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
            _body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            SetCount(tableCount);

            var start = Button("Rt.Prompt.Start", "Btn.Accent", TablesPromptAnswer.Start);
            start.Margin = new Thickness(0, 0, 10, 0);
            var later = Button("Rt.Prompt.Later", "Btn.Primary", TablesPromptAnswer.Later);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(start);
            buttons.Children.Add(later);

            var never = new TextBlock
            {
                Text = LocalizationManager.Text("Rt.Prompt.Never"),
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline
            };
            never.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            var neverButton = new Button { Content = never, Template = PlainTemplate(), HorizontalAlignment = HorizontalAlignment.Left };
            neverButton.Click += (_, _) => Answer(TablesPromptAnswer.Never);
            System.Windows.Automation.AutomationProperties.SetName(neverButton, never.Text);

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_body);
            stack.Children.Add(buttons);
            stack.Children.Add(neverButton);

            var card = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(18, 16, 18, 14),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.5, Color = Colors.Black }
            };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            Content = card;

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                    Answer(TablesPromptAnswer.Later);
            };

            _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _timeout.Tick += (_, _) => Answer(TablesPromptAnswer.Later);
            Loaded += (_, _) =>
            {
                PlaceAwayFromTables();
                UiMotion.FadeIn(card, 220);
                UiMotion.Slide(card, 40, 0, 320, 0);
                System.Media.SystemSounds.Asterisk.Play();
                _timeout.Start();
            };
            Closed += (_, _) => _timeout.Stop();
        }

        /// <summary>Si cambia el numero de mesas mientras el aviso esta abierto.</summary>
        public void SetCount(int tableCount) =>
            _body.Text = tableCount == 1
                ? LocalizationManager.Text("Rt.Prompt.BodyOne")
                : string.Format(LocalizationManager.Text("Rt.Prompt.Body"), tableCount);

        private Button Button(string textKey, string styleKey, TablesPromptAnswer answer)
        {
            var text = LocalizationManager.Text(textKey);
            var button = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8), MinWidth = 120 };
            button.SetResourceReference(StyleProperty, styleKey);
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            button.Click += (_, _) => Answer(answer);
            return button;
        }

        private static ControlTemplate PlainTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            return template;
        }

        private void Answer(TablesPromptAnswer answer)
        {
            if (_answered)
                return;
            _answered = true;
            _timeout.Stop();
            Answered?.Invoke(this, answer);
            if (Content is UIElement card)
                UiMotion.FadeOut(card, 180, Close);
            else
                Close();
        }

        // ------------------------------------------------------------ donde ponerlo

        /// <summary>
        /// Esquina inferior derecha (o superior derecha) de la pantalla donde menos tape las mesas: con
        /// seis u ocho mesas en mosaico, una esquina de la pantalla principal caeria sobre los botones de
        /// accion de la ultima mesa.
        /// </summary>
        private void PlaceAwayFromTables()
        {
            var source = PresentationSource.FromVisual(this);
            var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var toPx = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var size = toPx.Transform(new Vector(ActualWidth, ActualHeight));

            var tables = _tables
                .Select(handle => GetWindowRect(handle, out var r) ? new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)) : Rect.Empty)
                .Where(rect => !rect.IsEmpty)
                .ToList();

            var candidates = new List<(Rect Area, bool Primary)>();
            foreach (var (work, primary) in Monitors())
            {
                candidates.Add((new Rect(work.Right - size.X, work.Bottom - size.Y, size.X, size.Y), primary));
                candidates.Add((new Rect(work.Right - size.X, work.Top, size.X, size.Y), primary));
            }
            if (candidates.Count == 0)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth;
                Top = area.Bottom - ActualHeight;
                return;
            }

            double Covered(Rect area) => tables.Sum(table => Rect.Intersect(area, table) is { IsEmpty: false } hit ? hit.Width * hit.Height : 0);
            var best = candidates
                .OrderBy(c => Covered(c.Area))
                .ThenByDescending(c => c.Primary)
                .First().Area;
            var topLeft = toDip.Transform(new Point(best.Left, best.Top));
            Left = topLeft.X;
            Top = topLeft.Y;
        }

        private static List<(Rect Work, bool Primary)> Monitors()
        {
            var result = new List<(Rect, bool)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var w = info.Work;
                    result.Add((new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), (info.Flags & 1) != 0));
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    }
}
