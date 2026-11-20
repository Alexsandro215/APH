using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hud.App.Services;
using Hud.App.Services.Trainer;

namespace Hud.App.Views
{
    /// <summary>
    /// Tablero de rendimiento de la sesion de Tiempo real, pensado para una pantalla aparte, como los
    /// rastreadores de poker: las cifras (lo ganado, sin la suerte de los all-in, la suerte, bb/100,
    /// decisiones), la curva mano a mano, las ultimas manos calificadas (excelente, buena, estandar,
    /// mala, malisima) con su buena o mala suerte, los errores que mas repites con como evitarlos, los
    /// mas caros, la suerte de cada all-in y tus stats con su semaforo. Se actualiza en cada mano
    /// corregida y cada minuto, sin quitar el foco a las mesas, y recuerda donde y como la dejaste
    /// (otra pantalla, maximizada, siempre encima). Usa el mismo calculo que el informe PDF al terminar
    /// (<see cref="RealTimeSessionReportService.BuildLiveSummary"/>): el ultimo tramo de juego seguido.
    /// </summary>
    public sealed class SessionPerformanceWindow : Window
    {
        private readonly Func<IReadOnlyList<(string TableName, string Path, string? Hero)>> _tables;
        private readonly StackPanel _top = new();
        private readonly Grid _cards = new();
        private readonly List<FrameworkElement> _cardItems = new();
        private readonly TextBlock _subtitle;
        private readonly DispatcherTimer _timer;
        private bool _busy;
        private bool _pending;
        private int _columns;

        private const int RecentHands = 25;
        private const double LuckMarkBb = 5;
        private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

        private static string PlacementPath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APH", "rendimiento-ventana.json");

        private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized, bool Topmost);

        public SessionPerformanceWindow(Func<IReadOnlyList<(string TableName, string Path, string? Hero)>> tables)
        {
            _tables = tables;
            Title = LocalizationManager.Text("Perf.Title");
            MinWidth = 420;
            MinHeight = 320;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SetResourceReference(BackgroundProperty, "Brush.AppShell");
            FontSize = 15;
            RestorePlacement();

            // Cabecera: titulo, de que va la sesion y "siempre encima".
            var title = new TextBlock { Text = LocalizationManager.Text("Perf.Title"), FontSize = 26, FontWeight = FontWeights.Bold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SurfaceText");
            _subtitle = Dim(LocalizationManager.Text("Perf.Loading"));
            _subtitle.FontSize = 14;
            var onTop = new CheckBox
            {
                Content = LocalizationManager.Text("Perf.OnTop"),
                IsChecked = Topmost,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(onTop, LocalizationManager.Text("Perf.OnTop"));
            onTop.SetResourceReference(ForegroundProperty, "Brush.SurfaceText");
            onTop.Checked += (_, _) => Topmost = true;
            onTop.Unchecked += (_, _) => Topmost = false;
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(onTop, Dock.Right);
            header.Children.Add(onTop);
            var titles = new StackPanel();
            titles.Children.Add(title);
            titles.Children.Add(_subtitle);
            header.Children.Add(titles);

            var root = new StackPanel { Margin = new Thickness(24, 18, 24, 24) };
            root.Children.Add(header);
            root.Children.Add(_top);
            root.Children.Add(_cards);
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root
            };
            _cards.SizeChanged += (_, _) => LayoutCards();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _timer.Tick += (_, _) => Refresh();
            Loaded += (_, _) =>
            {
                Refresh();
                _timer.Start();
            };
            Closing += (_, _) => SavePlacement();
            Closed += (_, _) => _timer.Stop();
        }

        // ---------------------------------------------------------------- donde se abre

        private void RestorePlacement()
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(1280, work.Width - 24);
            Height = Math.Min(880, work.Height - 24);
            Left = work.Right - Width - 12;
            Top = work.Top + 12;
            try
            {
                if (!File.Exists(PlacementPath) || JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath)) is not { } saved)
                    return;
                // Solo si ese sitio sigue existiendo (la otra pantalla puede estar apagada).
                var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                var rect = new Rect(saved.Left, saved.Top, Math.Max(MinWidth, saved.Width), Math.Max(MinHeight, saved.Height));
                if (!screen.IntersectsWith(new Rect(rect.Left + 40, rect.Top + 10, Math.Max(1, rect.Width - 80), 40)))
                    return;
                Left = rect.Left;
                Top = rect.Top;
                Width = rect.Width;
                Height = rect.Height;
                Topmost = saved.Topmost;
                if (saved.Maximized)
                    SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Sin sitio guardado: arriba a la derecha de la pantalla principal.
            }
        }

        private void SavePlacement()
        {
            try
            {
                var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlacementPath)!);
                File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    WindowState == WindowState.Maximized, Topmost)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Rendimiento: no se pudo guardar donde estaba la ventana", ex);
            }
        }

        // ---------------------------------------------------------------- calculo

        /// <summary>Recalcula en segundo plano (si ya esta calculando, repite al terminar).</summary>
        public void Refresh()
        {
            if (_busy)
            {
                _pending = true;
                return;
            }

            _busy = true;
            var tables = _tables();
            Task.Run(() =>
            {
                try
                {
                    return RealTimeSessionReportService.BuildLiveSummary(tables);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Rendimiento: no se pudo calcular la sesion", ex);
                    return null;
                }
            }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
            {
                _busy = false;
                Render(task.Result);
                if (_pending)
                {
                    _pending = false;
                    Refresh();
                }
            }));
        }

        // ---------------------------------------------------------------- dibujo

        private void Render(LiveSessionSummary? summary)
        {
            _top.Children.Clear();
            _cardItems.Clear();
            if (summary is null || summary.Hands == 0)
            {
                _subtitle.Text = LocalizationManager.Text("Perf.Empty");
                LayoutCards(force: true);
                return;
            }

            _subtitle.Text = string.Format(Culture, LocalizationManager.Text("Perf.Header"),
                summary.Hands.ToString("N0", Culture), Duration(summary.Duration), summary.Tables) + "  ·  " +
                string.Format(Culture, LocalizationManager.Text("Perf.Updated"), DateTime.Now.ToString("HH:mm:ss", Culture));

            // Cifras grandes.
            var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            tiles.Children.Add(Tile(LocalizationManager.Text("Perf.Net"), Signed(summary.NetBb) + " bb", summary.NetBb));
            tiles.Children.Add(Tile(LocalizationManager.Text("Perf.NoLuck"), Signed(summary.AdjustedBb) + " bb", summary.AdjustedBb));
            tiles.Children.Add(Tile(LocalizationManager.Text("Perf.Luck"), Signed(summary.LuckBb) + " bb", summary.LuckBb));
            tiles.Children.Add(Tile("bb/100", Signed(summary.BbPer100), summary.BbPer100));
            tiles.Children.Add(Tile(LocalizationManager.Text("Perf.Per100NoLuck"), Signed(summary.AdjustedPer100), summary.AdjustedPer100));
            if (summary.Decisions > 0)
            {
                tiles.Children.Add(Tile(LocalizationManager.Text("Perf.NoMistakePct"), summary.GoodPct.ToString("0", Culture) + "%", null));
                if (summary.WithAdvice > 0)
                    tiles.Children.Add(Tile(LocalizationManager.Text("Perf.FollowedPct"), summary.FollowedPct.ToString("0", Culture) + "%", null));
            }
            _top.Children.Add(tiles);
            // La curva se dibuja de izquierda a derecha solo la primera vez; al recalcular cada minuto aparece ya hecha.
            _top.Children.Add(Card(LocalizationManager.Text("Perf.Curve"), Chart(summary.Curve, animate: !_curveShown)));
            _curveShown = true;

            _cardItems.Add(Card(LocalizationManager.Text("Perf.LastHands"), HandsSection(summary)));
            _cardItems.Add(Card(LocalizationManager.Text("Perf.Mistakes"), MistakesSection(summary)));
            _cardItems.Add(Card(LocalizationManager.Text("Perf.LuckAndStats"), LuckAndStatsSection(summary)));
            LayoutCards(force: true);
        }

        /// <summary>Tres columnas en pantalla grande, dos en mediana y una en estrecha.</summary>
        private void LayoutCards(bool force = false)
        {
            var width = _cards.ActualWidth > 0 ? _cards.ActualWidth : ActualWidth - 48;
            var columns = width >= 1250 ? 3 : width >= 820 ? 2 : 1;
            if (!force && columns == _columns)
                return;
            _columns = columns;

            _cards.Children.Clear();
            _cards.ColumnDefinitions.Clear();
            _cards.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 && columns > 1 ? 1.25 : 1, GridUnitType.Star) });
            var rows = columns == 3 ? 1 : columns == 2 ? 2 : _cardItems.Count;
            for (var i = 0; i < rows; i++)
                _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var i = 0; i < _cardItems.Count; i++)
            {
                var card = _cardItems[i];
                int row, column, span = 1;
                if (columns == 3)
                    (row, column) = (0, i);
                else if (columns == 2)
                {
                    // Las manos a la izquierda (alto), errores y suerte a la derecha.
                    (row, column) = i == 0 ? (0, 0) : (i - 1, 1);
                    if (i == 0)
                        span = 2;
                }
                else
                    (row, column) = (i, 0);
                Grid.SetRow(card, row);
                Grid.SetColumn(card, column);
                Grid.SetRowSpan(card, span);
                card.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == columns - 1 ? 0 : 6, 12);
                card.VerticalAlignment = VerticalAlignment.Top;
                _cards.Children.Add(card);
            }
        }

        // ---------------------------------------------------------------- ultimas manos

        private static FrameworkElement HandsSection(LiveSessionSummary summary)
        {
            var panel = new StackPanel();
            var hands = summary.HandLines;

            // Cuantas de cada: excelentes, buenas, estandar, malas, malisimas y sin corregir.
            var counts = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var quality in new[] { HandQuality.Excellent, HandQuality.Good, HandQuality.Standard, HandQuality.Bad, HandQuality.VeryBad, HandQuality.Ungraded })
            {
                var count = hands.Count(hand => hand.Quality == quality);
                if (count > 0 || quality != HandQuality.Ungraded)
                    counts.Children.Add(Chip($"{QualityLabel(quality, plural: true)} {count}", QualityColor(quality), 14, new Thickness(0, 0, 6, 6)));
            }
            panel.Children.Add(counts);

            // Buena y mala suerte (all-in).
            var lucky = hands.Where(hand => hand.LuckBb >= LuckMarkBb).ToList();
            var unlucky = hands.Where(hand => hand.LuckBb <= -LuckMarkBb).ToList();
            var luckRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            luckRow.Children.Add(Chip(string.Format(Culture, LocalizationManager.Text("Perf.GoodLuck"), lucky.Count, Signed(lucky.Sum(hand => hand.LuckBb))),
                LuckGood, 14, new Thickness(0, 0, 6, 6)));
            luckRow.Children.Add(Chip(string.Format(Culture, LocalizationManager.Text("Perf.BadLuck"), unlucky.Count, Signed(unlucky.Sum(hand => hand.LuckBb))),
                LuckBad, 14, new Thickness(0, 0, 6, 6)));
            panel.Children.Add(luckRow);

            // Las ultimas manos con algo que contar, la mas reciente arriba: las retiradas sin bote (estandar
            // de 0 bb) llenaban la lista y tapaban las buenas, las malas y los botes con suerte.
            var notable = hands.Where(hand => hand.Quality is not (HandQuality.Standard or HandQuality.Ungraded) ||
                                              Math.Abs(hand.NetBb) >= 2 || Math.Abs(hand.LuckBb) >= LuckMarkBb).ToList();
            foreach (var hand in Enumerable.Reverse(notable).Take(RecentHands))
                panel.Children.Add(HandRow(hand));
            var hidden = hands.Count - notable.Count;
            if (notable.Count > RecentHands)
                panel.Children.Add(Dim(string.Format(Culture, LocalizationManager.Text("Perf.MoreHands"), notable.Count - RecentHands)));
            if (hidden > 0)
                panel.Children.Add(Dim(string.Format(Culture, LocalizationManager.Text("Perf.HiddenHands"), hidden)));
            return panel;
        }

        private static FrameworkElement HandRow(LiveHand hand)
        {
            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(4, 0, 0, 0),
                BorderBrush = new SolidColorBrush(QualityColor(hand.Quality))
            };
            row.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");

            var line = new DockPanel();
            var result = new TextBlock
            {
                Text = Signed(hand.NetBb) + " bb",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            result.SetResourceReference(TextBlock.ForegroundProperty, hand.NetBb >= 0 ? "Brush.Positive" : "Brush.Negative");
            DockPanel.SetDock(result, Dock.Right);
            line.Children.Add(result);

            var left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var time = hand.At is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", Culture) : "--:--";
            left.Children.Add(Text($"{time}  ", 13, "Brush.TextDim", FontWeights.Normal));
            left.Children.Add(Text($"{hand.Cards} ", 16, "Brush.SurfaceText", FontWeights.Bold));
            left.Children.Add(Text($"{hand.Position} · {hand.Table}  ", 13, "Brush.TextDim", FontWeights.Normal));
            left.Children.Add(Chip(QualityLabel(hand.Quality, plural: false), QualityColor(hand.Quality), 12, new Thickness(0, 0, 4, 0)));
            if (hand.LuckBb >= LuckMarkBb)
                left.Children.Add(Chip(string.Format(Culture, LocalizationManager.Text("Perf.HandLuck"), Signed(hand.LuckBb)), LuckGood, 12, new Thickness(0)));
            else if (hand.LuckBb <= -LuckMarkBb)
                left.Children.Add(Chip(string.Format(Culture, LocalizationManager.Text("Perf.HandBadLuck"), Signed(hand.LuckBb)), LuckBad, 12, new Thickness(0)));
            line.Children.Add(left);

            var body = new StackPanel();
            body.Children.Add(line);
            if (hand.Note.Length > 0)
            {
                var note = Dim(hand.Note);
                note.FontSize = 13;
                note.Margin = new Thickness(0, 2, 0, 0);
                body.Children.Add(note);
            }
            row.Child = body;
            return row;
        }

        // ---------------------------------------------------------------- errores

        private static FrameworkElement MistakesSection(LiveSessionSummary summary)
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(LocalizationManager.Text("Perf.Decisions"), first: true));
            if (summary.Decisions == 0)
            {
                panel.Children.Add(Dim(LocalizationManager.Text("Perf.NoDecisions")));
                return panel;
            }

            panel.Children.Add(Body(string.Format(Culture, LocalizationManager.Text("Perf.DecisionsLine"),
                summary.GoodPct.ToString("0", Culture), summary.Mistakes, summary.Decisions.ToString("N0", Culture))));
            if (summary.WithAdvice > 0)
                panel.Children.Add(Body(string.Format(Culture, LocalizationManager.Text("Perf.FollowedLine"),
                    summary.FollowedPct.ToString("0", Culture), summary.Followed, summary.WithAdvice)));
            if (summary.DecisionVerdict is { } verdict)
                panel.Children.Add(Verdict(verdict.Verdict, verdict.Advice));

            panel.Children.Add(Heading(LocalizationManager.Text("Perf.Repeated")));
            if (summary.Patterns.Count == 0)
                panel.Children.Add(Dim(LocalizationManager.Text("Perf.NoMistakes")));
            foreach (var pattern in summary.Patterns.Take(4))
            {
                panel.Children.Add(Strong(string.Format(Culture, LocalizationManager.Text("Perf.Pattern"), pattern.Count, pattern.Label)));
                panel.Children.Add(Dim(string.Format(Culture, LocalizationManager.Text("Perf.PatternCost"),
                    Signed(pattern.AdjustedBb), string.Join(", ", pattern.Cards))));
                panel.Children.Add(Tip(pattern.Tip));
            }

            var costly = summary.Costliest.Where(item => item.AdjustedBb < 0).Take(3).ToList();
            if (costly.Count > 0)
            {
                panel.Children.Add(Heading(LocalizationManager.Text("Perf.Costly")));
                foreach (var item in costly)
                {
                    var line = string.Format(Culture, LocalizationManager.Text("Perf.CostlyLine"),
                        item.Cards, TrainerRangeEstimator.StreetName(item.Street), ActionWords(item.Chosen), Signed(item.AdjustedBb));
                    if (!string.IsNullOrWhiteSpace(item.Advice))
                        line += string.Format(Culture, LocalizationManager.Text("Perf.CostlyAdvice"), item.Advice);
                    panel.Children.Add(Strong(line));
                    if (FirstSentence(item.Why) is { Length: > 0 } why)
                        panel.Children.Add(Tip(why));
                }
            }
            return panel;
        }

        // ---------------------------------------------------------------- suerte y stats

        private static FrameworkElement LuckAndStatsSection(LiveSessionSummary summary)
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(LocalizationManager.Text("Perf.AllIns"), first: true));
            if (summary.AllIns.Count == 0)
                panel.Children.Add(Dim(LocalizationManager.Text("Perf.NoAllIns")));
            foreach (var allIn in summary.AllIns.Take(5))
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var luck = Chip(string.Format(Culture, LocalizationManager.Text(allIn.LuckBb >= 0 ? "Perf.HandLuck" : "Perf.HandBadLuck"), Signed(allIn.LuckBb)),
                    allIn.LuckBb >= 0 ? LuckGood : LuckBad, 12, new Thickness(6, 0, 0, 0));
                DockPanel.SetDock(luck, Dock.Right);
                row.Children.Add(luck);
                row.Children.Add(Body(string.Format(Culture, LocalizationManager.Text("Perf.AllInLine"),
                    allIn.Cards, allIn.Against, allIn.Street, (allIn.Equity * 100).ToString("0", Culture), Signed(allIn.NetBb))));
                panel.Children.Add(row);
            }

            panel.Children.Add(Heading(LocalizationManager.Text("Perf.Stats")));
            panel.Children.Add(Dim(string.Format(Culture, LocalizationManager.Text("Perf.VpipPfr"),
                summary.Vpip.ToString("0", Culture), summary.Pfr.ToString("0", Culture))));
            if (summary.StrongBetPct is { } strongPct)
                panel.Children.Add(Body(string.Format(Culture, LocalizationManager.Text("Perf.StrongValue"),
                    strongPct.ToString("0", Culture), summary.StrongBets, summary.StrongSpots)));
            if (summary.Feedback.Count == 0)
            {
                panel.Children.Add(Dim(string.Format(Culture, LocalizationManager.Text("Perf.FewHands"), StatScale.MinHands)));
            }
            else
            {
                var notGood = summary.Feedback.Where(item => item.Verdict != StatVerdict.Good).ToList();
                foreach (var item in notGood.Take(6))
                    panel.Children.Add(Verdict(item.Verdict, item.Sentence));
                var good = summary.Feedback.Where(item => item.Verdict == StatVerdict.Good).Select(item => item.Name).ToList();
                if (good.Count > 0)
                    panel.Children.Add(Verdict(StatVerdict.Good, string.Format(Culture, LocalizationManager.Text("Perf.StatsGood"), string.Join(", ", good))));
            }

            if (!string.IsNullOrWhiteSpace(summary.Focus))
            {
                panel.Children.Add(Heading(LocalizationManager.Text("Perf.Focus")));
                var focus = Body(summary.Focus);
                focus.FontWeight = FontWeights.SemiBold;
                panel.Children.Add(focus);
            }
            return panel;
        }

        // ---------------------------------------------------------------- curva

        /// <summary>Curva de la sesion con escala en bb: lo ganado (continua) y sin la suerte de los all-in (discontinua).</summary>
        private bool _curveShown;

        private static FrameworkElement Chart(IReadOnlyList<(double NetBb, double LuckBb)> curve, bool animate)
        {
            const double height = 260;
            const double axis = 64;
            var net = new List<double> { 0 };
            var adjusted = new List<double> { 0 };
            foreach (var (handNet, luck) in curve)
            {
                net.Add(net[^1] + handNet);
                adjusted.Add(adjusted[^1] + handNet - luck);
            }
            var max = Math.Max(1, net.Concat(adjusted).Max());
            var min = Math.Min(-1, net.Concat(adjusted).Min());
            var pad = (max - min) * 0.06;
            max += pad;
            min -= pad;

            var canvas = new Canvas { Height = height, ClipToBounds = true };
            canvas.SizeChanged += (_, _) => Draw();
            void Draw()
            {
                canvas.Children.Clear();
                var width = canvas.ActualWidth;
                if (width <= axis + 10 || net.Count < 2)
                    return;
                double X(int i) => axis + (width - axis - 6) * i / (net.Count - 1);
                double Y(double value) => (max - value) / (max - min) * (height - 22) + 4;

                // Rejilla y escala: el cero y cuatro marcas.
                foreach (var mark in Ticks(min, max))
                {
                    var grid = new Line { X1 = axis, X2 = width, Y1 = Y(mark), Y2 = Y(mark), StrokeThickness = mark == 0 ? 1.4 : 0.6 };
                    if (mark != 0)
                        grid.StrokeDashArray = new DoubleCollection { 2, 4 };
                    grid.SetResourceReference(Shape.StrokeProperty, "Brush.Border");
                    canvas.Children.Add(grid);
                    var label = Text(Signed(mark) + " bb", 12, "Brush.TextDim", FontWeights.Normal);
                    label.Width = axis - 8;
                    label.TextAlignment = TextAlignment.Right;
                    Canvas.SetLeft(label, 0);
                    Canvas.SetTop(label, Y(mark) - 9);
                    canvas.Children.Add(label);
                }
                var hands = Text(string.Format(Culture, LocalizationManager.Text("Perf.ChartHands"), net.Count - 1), 12, "Brush.TextDim", FontWeights.Normal);
                Canvas.SetRight(hands, 4);
                Canvas.SetTop(hands, height - 18);
                canvas.Children.Add(hands);

                var adjustedLine = Curve(adjusted, X, Y, "Brush.TextDim", dashed: true);
                var netLine = Curve(net, X, Y, net[^1] >= 0 ? "Brush.Positive" : "Brush.Negative", dashed: false);
                canvas.Children.Add(adjustedLine);
                canvas.Children.Add(netLine);
                if (animate)
                {
                    animate = false;
                    UiMotion.Reveal(adjustedLine, axis, width, height, 900);
                    UiMotion.Reveal(netLine, axis, width, height, 900);
                }
            }

            var panel = new StackPanel();
            panel.Children.Add(canvas);
            panel.Children.Add(Dim(LocalizationManager.Text("Perf.ChartLegend")));
            return panel;
        }

        /// <summary>El cero y unas marcas redondas (1, 2, 5, 10, 20, 50... bb) entre el minimo y el maximo.</summary>
        private static IEnumerable<double> Ticks(double min, double max)
        {
            var raw = (max - min) / 4;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
            var step = new[] { 1, 2, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw);
            for (var value = Math.Ceiling(min / step) * step; value <= max; value += step)
                yield return Math.Round(value, 6);
        }

        private static Polyline Curve(IReadOnlyList<double> values, Func<int, double> x, Func<double, double> y, string brush, bool dashed)
        {
            var line = new Polyline { StrokeThickness = dashed ? 1.8 : 2.6, StrokeLineJoin = PenLineJoin.Round };
            if (dashed)
                line.StrokeDashArray = new DoubleCollection { 4, 3 };
            line.SetResourceReference(Shape.StrokeProperty, brush);
            for (var i = 0; i < values.Count; i++)
                line.Points.Add(new Point(x(i), y(values[i])));
            return line;
        }

        // ---------------------------------------------------------------- piezas

        private static readonly Color LuckGood = Color.FromRgb(40, 150, 185);
        private static readonly Color LuckBad = Color.FromRgb(140, 90, 190);

        private static Color QualityColor(HandQuality quality) => quality switch
        {
            HandQuality.Excellent => Color.FromRgb(22, 150, 90),
            HandQuality.Good => Color.FromRgb(80, 175, 115),
            HandQuality.Standard => Color.FromRgb(100, 112, 126),
            HandQuality.Bad => Color.FromRgb(214, 140, 30),
            HandQuality.VeryBad => Color.FromRgb(210, 55, 65),
            _ => Color.FromRgb(70, 80, 94)
        };

        private static string QualityLabel(HandQuality quality, bool plural) =>
            LocalizationManager.Text($"Perf.Quality.{quality}{(plural ? ".Plural" : "")}");

        private static FrameworkElement Card(string title, FrameworkElement content)
        {
            var panel = new StackPanel();
            var heading = new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SurfaceText");
            panel.Children.Add(heading);
            panel.Children.Add(content);
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12, 16, 14),
                Margin = new Thickness(0, 0, 0, 12),
                Child = panel
            };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            return card;
        }

        // Ultima cifra de cada casilla: al recalcular, la cifra nueva sube o baja contando desde ella.
        private readonly Dictionary<string, double> _lastTileValue = new(StringComparer.Ordinal);

        private FrameworkElement Tile(string label, string value, double? sign)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(label, 13, "Brush.TextDim", FontWeights.SemiBold));
            var number = Text("", 30, sign is null ? "Brush.SurfaceText" : sign >= 0 ? "Brush.Positive" : "Brush.Negative", FontWeights.Bold);
            panel.Children.Add(number);
            double? previous = _lastTileValue.TryGetValue(label, out var last) ? last : 0;
            UiMotion.CountTo(number, value, previous);
            if (CountUpText.TryParse(value, CultureInfo.CurrentCulture, out var now))
                _lastTileValue[label] = now;
            var tile = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 170,
                Child = panel
            };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            return tile;
        }

        private static Border Chip(string text, Color color, double size, Thickness margin) => new()
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 2),
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = size, FontWeight = FontWeights.SemiBold }
        };

        private static FrameworkElement Verdict(StatVerdict verdict, string text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
            var chip = Chip($"{HeroStatCoach.IconOf(verdict)} {HeroStatCoach.LabelOf(verdict)}", HeroStatCoach.ColorOf(verdict), 12, new Thickness(0, 1, 8, 0));
            chip.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            row.Children.Add(Body(text));
            return row;
        }

        private static TextBlock Text(string text, double size, string brush, FontWeight weight)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return block;
        }

        private static TextBlock Heading(string text, bool first = false)
        {
            var block = Text(text, 16, "Brush.SurfaceText", FontWeights.Bold);
            block.Margin = new Thickness(0, first ? 0 : 14, 0, 4);
            return block;
        }

        private static TextBlock Strong(string text)
        {
            var block = Body(text);
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, 6, 0, 0);
            return block;
        }

        private static TextBlock Body(string text)
        {
            var block = Text(text, 15, "Brush.SurfaceText", FontWeights.Normal);
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private static TextBlock Tip(string text)
        {
            var block = Dim(text);
            block.FontStyle = FontStyles.Italic;
            block.Margin = new Thickness(12, 2, 0, 2);
            return block;
        }

        private static TextBlock Dim(string text)
        {
            var block = Text(text, 13.5, "Brush.TextDim", FontWeights.Normal);
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private static string Signed(double value) => value.ToString("+0.#;-0.#;0", Culture);

        /// <summary>"Call 5.5 bb" -> "igualar 5,5 bb" (la correccion escribe en jerga inglesa).</summary>
        private static string ActionWords(string chosen)
        {
            var parts = chosen.Trim().Split(' ', 2);
            var key = parts[0].ToLowerInvariant() switch
            {
                "fold" => "Hud.Act.Fold",
                "check" => "Hud.Act.Check",
                "call" => "Hud.Act.Call",
                "bet" => "Hud.Act.Bet",
                "raise" => "Hud.Act.Raise",
                _ => null
            };
            var rest = parts.Length > 1 ? " " + parts[1].Replace('.', ',') : "";
            return key is null ? chosen : LocalizationManager.Text(key) + rest;
        }

        private static string Duration(TimeSpan time) =>
            time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(time.TotalMinutes))} min";

        private static string FirstSentence(string text)
        {
            var clean = (text ?? "").Trim();
            var end = clean.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 ? clean[..(end + 1)] : clean;
        }
    }
}
