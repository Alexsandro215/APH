using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class GainAnalysisWindow : Window
    {
        private readonly IReadOnlyList<MainWindow.TableSessionStats> _tables;
        private readonly IReadOnlyList<GainHand> _hands;
        private IReadOnlyList<GainGroup> _groups = Array.Empty<GainGroup>();
        private string _mode = "Street";

        public GainAnalysisWindow(IEnumerable<MainWindow.TableSessionStats> tables)
        {
            InitializeComponent();
            FitToWorkArea();
            Loaded += GainAnalysisWindow_Loaded;

            _tables = tables
                .Where(table => table.HandsReceived > 0)
                .OrderBy(table => table.LastPlayedAt)
                .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _hands = BuildHands(_tables).ToList();
            SummaryText.Text = $"{_hands.Count} manos analizadas";
            RefreshMode();
            DrawCurve();
        }

        private void GainAnalysisWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GainAnalysisWindow_Loaded;
            FitToWorkArea();
            DrawCurve();
            DrawBars();
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            const double desiredWidth = 1380;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(desiredWidth, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        private void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not string mode)
                return;

            _mode = mode;
            RefreshMode();
        }

        private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawCurve();

        private void BarsCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawBars();

        private void RefreshMode()
        {
            _groups = BuildGroups(_mode).ToList();
            BarsTitleText.Text = _mode switch
            {
                "Position" => "GANANCIA POR POSICION",
                "Action" => "GANANCIA POR ACCION",
                "Bluff" => "GANANCIA POR BLUFF",
                "Table" => "GANANCIA POR MESA",
                "Format" => "GANANCIA POR FORMATO",
                "Blinds" => "GANANCIA POR CIEGAS",
                _ => "GANANCIA POR CALLE"
            };
            ModeDescriptionText.Text = _mode switch
            {
                "Position" => "Agrupa la ganancia por asiento del heroe para detectar donde imprime bb y donde se fuga valor.",
                "Action" => "Agrupa por la ultima accion importante del heroe en la mano: fold, check, call, bet, raise o all-in.",
                "Bluff" => "Separa agresion sin showdown, agresion con mano debil y manos no agresivas para leer la calidad del bluff.",
                "Table" => "Cada barra es una mesa completa. Este corte sirve para ubicar exactamente donde ganaste o perdiste mas fichas.",
                "Format" => "Agrupa por formato detectado: 6-max, 3-max u otros formatos.",
                "Blinds" => "Agrupa por ciegas, por ejemplo 100/200 o 250/500, para detectar donde rinde mejor tu juego.",
                _ => "Agrupa por la ultima calle donde el heroe tomo una decision: preflop, flop, turn o river."
            };

            SetModeButton(StreetModeButton, _mode == "Street");
            SetModeButton(PositionModeButton, _mode == "Position");
            SetModeButton(ActionModeButton, _mode == "Action");
            SetModeButton(BluffModeButton, _mode == "Bluff");
            SetModeButton(TableModeButton, _mode == "Table");
            SetModeButton(FormatModeButton, _mode == "Format");
            SetModeButton(BlindsModeButton, _mode == "Blinds");
            UpdateSummaryCards();
            UpdateInsightPanel();
            DrawBars();
        }

        private void SetModeButton(Button button, bool selected)
        {
            button.Background = selected
                ? GetThemeBrush("Brush.AccentSoft", new SolidColorBrush(Color.FromRgb(35, 55, 72)))
                : GetThemeBrush("Brush.Surface", new SolidColorBrush(Color.FromRgb(17, 26, 36)));
            button.BorderBrush = selected
                ? GetThemeBrush("Brush.Accent", new SolidColorBrush(Color.FromRgb(48, 217, 139)))
                : GetThemeBrush("Brush.Border", new SolidColorBrush(Color.FromRgb(42, 58, 76)));
            button.Foreground = selected
                ? GetThemeBrush("Brush.Text", Brushes.White)
                : GetThemeBrush("Brush.TextDim", new SolidColorBrush(Color.FromRgb(164, 184, 203)));
        }

        private void UpdateSummaryCards()
        {
            var total = _tables.Sum(table => table.NetAmount);
            var average = _hands.Count == 0 ? 0 : total / _hands.Count;
            var best = _groups.OrderByDescending(group => group.TotalAmount).FirstOrDefault();
            var worst = _groups.OrderBy(group => group.TotalAmount).FirstOrDefault();

            TotalText.Text = FormatChips(total);
            TotalText.Foreground = TrendBrush(total);
            AverageText.Text = $"{FormatChips(average)}/mano";
            AverageText.Foreground = TrendBrush(average);
            BestText.Text = best is null ? "-" : $"{best.Label} {FormatChips(best.TotalAmount)}";
            BestText.Foreground = best is null ? GetThemeBrush("Brush.Text", Brushes.White) : TrendBrush(best.TotalAmount);
            WorstText.Text = worst is null ? "-" : $"{worst.Label} {FormatChips(worst.TotalAmount)}";
            WorstText.Foreground = worst is null ? GetThemeBrush("Brush.Text", Brushes.White) : TrendBrush(worst.TotalAmount);
            CurveTotalText.Text = FormatChips(total);
            CurveTotalText.Foreground = TrendBrush(total);
        }

        private void UpdateInsightPanel()
        {
            InsightTitleText.Text = _mode switch
            {
                "Position" => "LECTURA POR POSICION",
                "Action" => "QUE HACE CADA ACCION",
                "Bluff" => "LECTURA DE BLUFF",
                "Table" => "LECTURA POR MESA",
                "Format" => "LECTURA POR FORMATO",
                "Blinds" => "LECTURA POR CIEGAS",
                _ => "LECTURA POR CALLE"
            };

            InsightBodyText.Text = _mode switch
            {
                "Position" => "Sirve para encontrar si la ganancia viene de robar, defender, jugar ciegas o de posiciones tardias.",
                "Action" => "La accion es la ultima decision fuerte detectada en la mano. Te dice cuanto estas ganando o perdiendo cuando terminas foldeando, pagando, apostando o resubiendo.",
                "Bluff" => "Es una lectura aproximada desde el historial: no reemplaza revision manual, pero ayuda a separar presion rentable de agresion que cuesta bb.",
                "Table" => "Este corte muestra mesas concretas. Si una barra domina, esa mesa explica buena parte del resultado.",
                "Format" => "Este corte compara formatos de mesa para encontrar donde se adapta mejor tu juego.",
                "Blinds" => "Este corte compara niveles de ciegas para detectar donde el resultado real acompana mejor.",
                _ => "La calle indica donde se decidio la mano para el heroe. Si una calle sale muy negativa, ahi conviene revisar botes grandes y decisiones repetidas."
            };

            InsightList.ItemsSource = _groups
                .OrderByDescending(group => Math.Abs(group.TotalAmount))
                .Select(group => new InsightItem(
                    group.Label,
                    FormatChips(group.TotalAmount),
                    $"{group.Hands} manos | media {FormatChips(group.AverageAmount)}/mano",
                    BuildGroupExplanation(_mode, group),
                    TrendBrush(group.TotalAmount)))
                .ToList();
        }

        private static string BuildGroupExplanation(string mode, GainGroup group)
        {
            var sample = group.Hands < 25
                ? "Muestra chica: tomalo como pista, no como sentencia."
                : "Muestra suficiente para empezar a revisar patrones.";
            var trend = group.TotalAmount >= 0
                ? "Hasta ahora aporta ganancia."
                : "Hasta ahora esta drenando bb.";

            var definition = mode switch
            {
                "Position" => PositionExplanation(group.Label),
                "Action" => ActionExplanation(group.Label),
                "Bluff" => BluffExplanation(group.Label),
                "Table" => "Mesa especifica: permite revisar donde se concentro la ganancia o perdida real.",
                "Format" => "Formato de mesa detectado desde el historial.",
                "Blinds" => "Nivel de ciegas detectado desde la mesa, como 100/200.",
                _ => StreetExplanation(group.Label)
            };

            return $"{definition} {trend} {sample}";
        }

        private static string StreetExplanation(string value) => value switch
        {
            "Preflop" => "Manos que se resolvieron o tuvieron tu ultima decision antes del flop.",
            "Flop" => "Manos donde tu ultima decision fue en flop; mide c-bets, calls y folds tempranos.",
            "Turn" => "Manos donde seguiste hasta turn; suele revelar segundos barrels y calls caros.",
            "River" => "Manos decididas en river; aqui pesan value bets, bluffs finales y hero calls.",
            _ => "Corte de calle detectado desde el historial."
        };

        private static string PositionExplanation(string value) => value switch
        {
            "BTN" or "BTN/SB" => "Boton: zona de robo y maxima informacion postflop.",
            "SB" => "Small blind: juegas fuera de posicion y suele ser una fuga natural.",
            "BB" => "Big blind: mezcla defensa obligada, botes limpeados y calls amplios.",
            "UTG" => "UTG: rango temprano; deberia ser mas fuerte y estable.",
            "MP" => "MP: posicion media; revisa aperturas y calls contra early.",
            "HJ" => "Hijack: empieza la presion de robo, pero aun hay jugadores por hablar.",
            "CO" => "Cutoff: posicion de robo fuerte antes del boton.",
            _ => "Posicion no identificada de forma confiable en el historial."
        };

        private static string ActionExplanation(string value) => value switch
        {
            "Fold" => "Fold: abandonaste la mano; si pierde demasiado, revisa calls previos o folds tardios.",
            "Check" => "Check: pasaste sin apostar; ayuda a ver si estas controlando bote o cediendo demasiada iniciativa.",
            "Call" => "Call: pagaste una apuesta; si esta negativo, revisa bluff-catches y draws pagados caro.",
            "Bet" => "Bet: apostaste primero en la calle; mide valor, proteccion y bluffs iniciados por ti.",
            "Raise" => "Raise: resubiste una apuesta; mide presion, value fuerte y semi-bluffs.",
            "All-in" => "All-in: pusiste todas las fichas; es el corte de mayor varianza.",
            "Sin accion" => "Sin accion: mano detectada sin accion relevante del heroe.",
            _ => "Accion normalizada desde el historial."
        };

        private static string BluffExplanation(string value) => value switch
        {
            "Bluff ganado" => "Bluff ganado: agresion que cobro el bote sin showdown.",
            "Bluff perdido" => "Bluff perdido: agresion con mano debil o sin combinacion fuerte que termino negativa.",
            "Valor/agresion" => "Valor/agresion: apuestas o raises que no parecen bluff puro por el resultado o combinacion.",
            "No agresivo" => "No agresivo: manos donde tu ultima accion fue fold, check o call.",
            _ => "Clasificacion aproximada de agresion."
        };

        private IEnumerable<GainGroup> BuildGroups(string mode)
        {
            if (mode is "Table" or "Format" or "Blinds")
            {
                foreach (var group in BuildTableGroups(mode))
                    yield return group;
                yield break;
            }

            IEnumerable<IGrouping<string, GainHand>> groups = mode switch
            {
                "Position" => _hands.GroupBy(hand => hand.Position),
                "Action" => _hands.GroupBy(hand => hand.Action),
                "Bluff" => _hands.GroupBy(hand => hand.BluffType),
                _ => _hands.GroupBy(hand => hand.Street)
            };

            var ordered = mode switch
            {
                "Position" => groups.OrderBy(group => PositionOrder(group.Key)),
                "Action" => groups.OrderBy(group => ActionOrder(group.Key)),
                "Bluff" => groups.OrderBy(group => BluffOrder(group.Key)),
                _ => groups.OrderBy(group => StreetOrder(group.Key))
            };

            foreach (var group in ordered)
            {
                var hands = group.ToList();
                var totalAmount = hands.Sum(hand => hand.NetAmount);
                var totalBb = hands.Sum(hand => hand.NetBb);
                yield return new GainGroup(
                    group.Key,
                    group.Key,
                    hands.Count,
                    totalAmount,
                    hands.Count == 0 ? 0 : totalAmount / hands.Count,
                    totalBb,
                    hands.Count == 0 ? 0 : totalBb / hands.Count);
            }
        }

        private IEnumerable<GainGroup> BuildTableGroups(string mode)
        {
            var groups = mode switch
            {
                "Format" => _tables.GroupBy(table => string.IsNullOrWhiteSpace(table.GameFormat) ? "Sin formato" : table.GameFormat),
                "Blinds" => _tables.GroupBy(BlindsLabel),
                _ => _tables.GroupBy(table => table.TableName)
            };

            var ordered = mode switch
            {
                "Table" => groups.OrderBy(group => group.Min(table => table.LastPlayedAt)),
                _ => groups.OrderByDescending(group => Math.Abs(group.Sum(table => table.NetAmount)))
            };

            foreach (var group in ordered)
            {
                var tables = group.ToList();
                var hands = tables.Sum(table => table.HandsReceived);
                var totalAmount = tables.Sum(table => table.NetAmount);
                var totalBb = tables.Sum(table => table.NetBb);
                var axisLabel = mode == "Table"
                    ? tables.OrderBy(table => table.LastPlayedAt).First().LastPlayedAt.ToString("MM-dd", CultureInfo.InvariantCulture)
                    : group.Key;
                yield return new GainGroup(
                    group.Key,
                    axisLabel,
                    hands,
                    totalAmount,
                    hands == 0 ? 0 : totalAmount / hands,
                    totalBb,
                    hands == 0 ? 0 : totalBb / hands);
            }
        }

        private static IEnumerable<GainHand> BuildHands(IEnumerable<MainWindow.TableSessionStats> tables)
        {
            foreach (var table in tables)
            {
                if (string.IsNullOrWhiteSpace(table.SourcePath) || !File.Exists(table.SourcePath) || table.BigBlind <= 0)
                    continue;

                var handNumber = 0;
                foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadLines(table.SourcePath)))
                {
                    handNumber++;
                    if (!PokerStarsHandHistory.TryGetDealtCards(hand, table.HeroName, out _))
                        continue;

                    var netAmount = PokerStarsHandHistory.EstimateNetForPlayer(hand, table.HeroName);
                    var netBb = netAmount / table.BigBlind;
                    var positionMap = PokerStarsHandHistory.BuildPositionMap(hand);
                    var position = positionMap.TryGetValue(PokerStarsHandHistory.NormalizeName(table.HeroName), out var mappedPosition)
                        ? mappedPosition
                        : "?";
                    var action = LastHeroAction(hand, table.HeroName);
                    var street = LastHeroActionStreet(hand, table.HeroName);
                    var summary = PokerStarsHandHistory.ExtractHandSummaryInfo(hand, table.HeroName);
                    var bluffType = ClassifyBluff(action, summary.Result, summary.Combination, netBb);
                    var playedAt = PokerStarsHandHistory.ExtractTimestamp(hand) ?? table.LastPlayedAt;

                    yield return new GainHand(
                        table.TableName,
                        handNumber,
                        playedAt,
                        street,
                        position,
                        action,
                        bluffType,
                        netAmount,
                        table.GameFormat,
                        $"{table.BigBlind:0.##} BB",
                        netBb);
                }
            }
        }

        private static string LastHeroAction(IReadOnlyList<string> hand, string heroName)
        {
            var action = "Sin accion";
            foreach (var line in hand)
            {
                var match = PokerStarsHandHistory.ActorRx.Match(line);
                if (!match.Success || !PokerStarsHandHistory.SamePlayer(match.Groups["actor"].Value, heroName))
                    continue;

                var normalized = PokerStarsHandHistory.NormalizeAction(match.Groups["action"].Value);
                action = normalized switch
                {
                    "folds" => "Fold",
                    "checks" => "Check",
                    "calls" => "Call",
                    "bets" => "Bet",
                    "raises" => "Raise",
                    "all-in" => "All-in",
                    _ => action
                };
            }

            return action;
        }

        private static string LastHeroActionStreet(IReadOnlyList<string> hand, string heroName)
        {
            var street = "Preflop";
            for (var i = 0; i < hand.Count; i++)
            {
                var match = PokerStarsHandHistory.ActorRx.Match(hand[i]);
                if (!match.Success || !PokerStarsHandHistory.SamePlayer(match.Groups["actor"].Value, heroName))
                    continue;

                var normalized = PokerStarsHandHistory.NormalizeAction(match.Groups["action"].Value);
                if (normalized is not ("folds" or "checks" or "calls" or "bets" or "raises" or "all-in"))
                    continue;

                street = StreetAtLine(hand, i);
            }

            return street;
        }

        private static string StreetAtLine(IReadOnlyList<string> hand, int index)
        {
            var flop = PokerStarsHandHistory.FindStreetIndex(hand, "FLOP");
            var turn = PokerStarsHandHistory.FindStreetIndex(hand, "TURN");
            var river = PokerStarsHandHistory.FindStreetIndex(hand, "RIVER");

            if (river >= 0 && index > river) return "River";
            if (turn >= 0 && index > turn) return "Turn";
            if (flop >= 0 && index > flop) return "Flop";
            return "Preflop";
        }

        private static string ClassifyBluff(string action, string result, string combination, double netBb)
        {
            var aggressive = action is "Bet" or "Raise" or "All-in";
            if (!aggressive)
                return "No agresivo";

            var noShowdown = result.Contains("sin ver el Showdown", StringComparison.OrdinalIgnoreCase);
            var weak = string.IsNullOrWhiteSpace(combination) ||
                combination.Contains("Carta alta", StringComparison.OrdinalIgnoreCase);

            if (noShowdown && netBb > 0)
                return "Bluff ganado";
            if (weak && netBb < 0)
                return "Bluff perdido";
            return "Valor/agresion";
        }

        private void DrawCurve()
        {
            if (!IsLoaded || CurveCanvas.ActualWidth <= 1 || CurveCanvas.ActualHeight <= 1)
                return;

            CurveCanvas.Children.Clear();
            var ordered = _tables
                .OrderBy(table => table.LastPlayedAt)
                .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ordered.Count == 0)
            {
                AddCanvasLabel(CurveCanvas, "Sin datos", CurveCanvas.ActualWidth / 2 - 24, CurveCanvas.ActualHeight / 2 - 8, GetThemeBrush("Brush.Text", Brushes.White));
                return;
            }

            var cumulative = new List<double>();
            var running = 0d;
            foreach (var table in ordered)
            {
                running += table.NetAmount;
                cumulative.Add(running);
            }

            var geometry = BuildPlot(CurveCanvas.ActualWidth, CurveCanvas.ActualHeight, cumulative, out var points, out var zeroY, out var min, out var max);
            var average = cumulative.Average();
            var averageY = ValueToY(average, min, max, geometry.Top, geometry.Bottom);
            DrawGrid(CurveCanvas, geometry.Left, geometry.Top, geometry.Right, geometry.Bottom);
            DrawZeroLine(CurveCanvas, geometry.Left, geometry.Right, zeroY);
            DrawAverageLine(CurveCanvas, geometry.Left, geometry.Right, averageY, average);

            var chartColor = running >= 0
                ? GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139))
                : GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));

            var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(48, chartColor.R, chartColor.G, chartColor.B)) };
            area.Points.Add(new Point(points[0].X, zeroY));
            foreach (var point in points)
                area.Points.Add(point);
            area.Points.Add(new Point(points[^1].X, zeroY));
            CurveCanvas.Children.Add(area);

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(chartColor),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var point in points)
                line.Points.Add(point);
            CurveCanvas.Children.Add(line);

            for (var i = 0; i < points.Count; i += Math.Max(1, points.Count / 70))
            {
                var table = ordered[i];
                AddDot(
                    CurveCanvas,
                    points[i].X,
                    points[i].Y,
                    chartColor,
                    BuildGainChartToolTip(table, cumulative[i], chartColor));
            }

            AddCanvasLabel(CurveCanvas, $"{max:0.#} fichas", 4, geometry.Top - 2, GetThemeBrush("Brush.TextDim", Brushes.White));
            AddCanvasLabel(CurveCanvas, $"{min:0.#} fichas", 4, geometry.Bottom - 12, GetThemeBrush("Brush.TextDim", Brushes.White));
            AddXAxisDateLabels(CurveCanvas, ordered, points, geometry.Bottom);
        }

        private void DrawBars()
        {
            if (!IsLoaded || BarsCanvas.ActualWidth <= 1 || BarsCanvas.ActualHeight <= 1)
                return;

            BarsCanvas.Children.Clear();
            if (_groups.Count == 0)
            {
                AddCanvasLabel(BarsCanvas, "Sin datos", BarsCanvas.ActualWidth / 2 - 24, BarsCanvas.ActualHeight / 2 - 8, GetThemeBrush("Brush.Text", Brushes.White));
                return;
            }

            const double padLeft = 46;
            const double padTop = 18;
            const double padRight = 22;
            const double padBottom = 48;
            var width = BarsCanvas.ActualWidth;
            var height = BarsCanvas.ActualHeight;
            var plotLeft = padLeft;
            var plotRight = Math.Max(plotLeft + 1, width - padRight);
            var plotTop = padTop;
            var plotBottom = Math.Max(plotTop + 1, height - padBottom);
            var maxAbs = Math.Max(1, _groups.Max(group => Math.Abs(group.TotalAmount)));
            var zeroY = plotTop + (plotBottom - plotTop) / 2;
            var slot = (plotRight - plotLeft) / _groups.Count;
            var barWidth = Math.Max(22, Math.Min(82, slot * 0.58));

            DrawGrid(BarsCanvas, plotLeft, plotTop, plotRight, plotBottom);
            DrawZeroLine(BarsCanvas, plotLeft, plotRight, zeroY);

            for (var i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                var x = plotLeft + slot * i + (slot - barWidth) / 2;
                var barHeight = Math.Abs(group.TotalAmount) / maxAbs * ((plotBottom - plotTop) / 2 - 8);
                var y = group.TotalAmount >= 0 ? zeroY - barHeight : zeroY;
                var color = group.TotalAmount >= 0
                    ? GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139))
                    : GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));

                var rect = new Border
                {
                    Width = barWidth,
                    Height = Math.Max(2, barHeight),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(210, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    ToolTip = BuildGainBarToolTip(group, color)
                };
                ToolTipService.SetInitialShowDelay(rect, 0);
                ToolTipService.SetBetweenShowDelay(rect, 0);
                ToolTipService.SetShowDuration(rect, 12000);
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                BarsCanvas.Children.Add(rect);

                AddCanvasLabel(BarsCanvas, FormatCompactChips(group.TotalAmount), x - 8, group.TotalAmount >= 0 ? y - 18 : y + barHeight + 3, TrendBrush(group.TotalAmount));
                AddCanvasLabel(BarsCanvas, ShortLabel(group.AxisLabel), x - 6, plotBottom + 10, GetThemeBrush("Brush.TextDim", Brushes.White));
            }
        }

        private static PlotGeometry BuildPlot(double width, double height, IReadOnlyList<double> values, out List<Point> points, out double zeroY, out double min, out double max)
        {
            const double padLeft = 36;
            const double padTop = 16;
            const double padRight = 16;
            const double padBottom = 24;
            var left = padLeft;
            var top = padTop;
            var right = Math.Max(left + 1, width - padRight);
            var bottom = Math.Max(top + 1, height - padBottom);
            var minValue = Math.Min(0, values.Min());
            var maxValue = Math.Max(0, values.Max());
            if (Math.Abs(maxValue - minValue) < 0.001)
            {
                maxValue += 1;
                minValue -= 1;
            }

            double xAt(int i) => values.Count == 1 ? (left + right) / 2 : left + (right - left) * i / (values.Count - 1);
            double yAt(double value) => bottom - (value - minValue) / (maxValue - minValue) * (bottom - top);

            zeroY = yAt(0);
            points = values.Select((value, index) => new Point(xAt(index), yAt(value))).ToList();
            min = minValue;
            max = maxValue;
            return new PlotGeometry(left, top, right, bottom);
        }

        private static void DrawGrid(Canvas canvas, double left, double top, double right, double bottom)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(58, 43, 61, 82));
            for (var i = 0; i <= 4; i++)
            {
                var y = top + (bottom - top) * i / 4;
                canvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = right,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                });
            }
        }

        private static void DrawZeroLine(Canvas canvas, double left, double right, double y)
        {
            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(130, 158, 173, 188)),
                StrokeThickness = 1
            });
        }

        private static void DrawAverageLine(Canvas canvas, double left, double right, double y, double average)
        {
            var brush = new SolidColorBrush(Color.FromArgb(170, 255, 215, 86));
            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = right,
                Y1 = y,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection { 6, 4 }
            });
            AddCanvasLabel(canvas, $"Media {FormatCompactChips(average)}", right - 112, y - 18, brush);
        }

        private static double ValueToY(double value, double min, double max, double top, double bottom) =>
            bottom - (value - min) / (max - min) * (bottom - top);

        private static void AddXAxisDateLabels(
            Canvas canvas,
            IReadOnlyList<MainWindow.TableSessionStats> tables,
            IReadOnlyList<Point> points,
            double bottom)
        {
            if (tables.Count == 0 || points.Count == 0)
                return;

            var indexes = new SortedSet<int> { 0, tables.Count - 1 };
            if (tables.Count > 2)
            {
                indexes.Add(tables.Count / 3);
                indexes.Add(tables.Count * 2 / 3);
            }

            foreach (var index in indexes)
            {
                var label = tables[index].LastPlayedAt.ToString("MM-dd", CultureInfo.InvariantCulture);
                AddCanvasLabel(canvas, label, points[index].X - 14, bottom + 7, GetThemeBrush("Brush.TextDim", Brushes.White));
            }
        }

        private static void AddDot(Canvas canvas, double x, double y, Color color, object tip)
        {
            const double hitSize = 24;
            var hitArea = new Ellipse
            {
                Width = hitSize,
                Height = hitSize,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip
            };
            ToolTipService.SetInitialShowDelay(hitArea, 0);
            ToolTipService.SetBetweenShowDelay(hitArea, 0);
            ToolTipService.SetShowDuration(hitArea, 12000);
            Canvas.SetLeft(hitArea, x - hitSize / 2);
            Canvas.SetTop(hitArea, y - hitSize / 2);
            canvas.Children.Add(hitArea);

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 3,
                Cursor = Cursors.Hand,
                IsHitTestVisible = false,
                ToolTip = tip
            };
            Canvas.SetLeft(dot, x - 5);
            Canvas.SetTop(dot, y - 5);
            canvas.Children.Add(dot);
        }

        private static FrameworkElement BuildGainBarToolTip(GainGroup group, Color chartColor)
        {
            var isWin = group.TotalAmount >= 0;
            var resultColor = isWin
                ? GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139))
                : GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));
            var textBrush = GetThemeBrush("Brush.Text", Brushes.White);
            var dimBrush = GetThemeBrush("Brush.TextDim", new SolidColorBrush(Color.FromRgb(164, 184, 203)));
            var borderBrush = new SolidColorBrush(resultColor);

            var card = new Border
            {
                MinWidth = 230,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(
                    Color.FromArgb(248, 18, 24, 34),
                    Color.FromArgb(248, 8, 11, 16),
                    new Point(0, 0),
                    new Point(1, 1)),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                    Color = Color.FromRgb(0, 0, 0)
                }
            };

            var root = new StackPanel();
            var titleRow = new DockPanel { LastChildFill = true };
            var icon = new TextBlock
            {
                Text = isWin ? "\u25B2" : "\u25BC",
                Foreground = borderBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(icon, Dock.Left);
            titleRow.Children.Add(icon);
            titleRow.Children.Add(new TextBlock
            {
                Text = group.Label,
                Foreground = textBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190
            });
            root.Children.Add(titleRow);

            root.Children.Add(new TextBlock
            {
                Text = $"{group.Hands} manos",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(21, 2, 0, 10)
            });

            var metrics = new Grid();
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var result = BuildTooltipMetric("Resultado", FormatChips(group.TotalAmount), borderBrush);
            var average = BuildTooltipMetric("Media", $"{FormatChips(group.AverageAmount)}/mano", new SolidColorBrush(chartColor));
            Grid.SetColumn(average, 1);
            metrics.Children.Add(result);
            metrics.Children.Add(average);
            root.Children.Add(metrics);

            root.Children.Add(new TextBlock
            {
                Text = $"BB aprox: {group.TotalBb:+0.#;-0.#;0} bb",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0)
            });

            card.Child = root;
            return card;
        }

        private static FrameworkElement BuildGainChartToolTip(
            MainWindow.TableSessionStats table,
            double cumulativeAmount,
            Color chartColor)
        {
            var isWin = table.NetAmount >= 0;
            var resultColor = isWin
                ? GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139))
                : GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));
            var textBrush = GetThemeBrush("Brush.Text", Brushes.White);
            var dimBrush = GetThemeBrush("Brush.TextDim", new SolidColorBrush(Color.FromRgb(164, 184, 203)));
            var borderBrush = new SolidColorBrush(resultColor);

            var card = new Border
            {
                MinWidth = 240,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(
                    Color.FromArgb(248, 18, 24, 34),
                    Color.FromArgb(248, 8, 11, 16),
                    new Point(0, 0),
                    new Point(1, 1)),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.45,
                    Color = Color.FromRgb(0, 0, 0)
                }
            };

            var root = new StackPanel();
            var titleRow = new DockPanel { LastChildFill = true };
            var icon = new TextBlock
            {
                Text = isWin ? "\u25B2" : "\u25BC",
                Foreground = borderBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(icon, Dock.Left);
            titleRow.Children.Add(icon);
            titleRow.Children.Add(new TextBlock
            {
                Text = table.TableName,
                Foreground = textBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190
            });
            root.Children.Add(titleRow);

            root.Children.Add(new TextBlock
            {
                Text = $"{table.GameFormat} - {table.PlayedDate}",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(21, 2, 0, 10)
            });

            var metrics = new Grid();
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var result = BuildTooltipMetric("Resultado", FormatChips(table.NetAmount), borderBrush);
            var total = BuildTooltipMetric("Acumulado", FormatChips(cumulativeAmount), new SolidColorBrush(chartColor));
            Grid.SetColumn(total, 1);
            metrics.Children.Add(result);
            metrics.Children.Add(total);
            root.Children.Add(metrics);

            root.Children.Add(new TextBlock
            {
                Text = $"{table.HandsReceived} manos | {table.NetBb:+0.#;-0.#;0} bb | {table.NetAmountLabel}",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0)
            });

            card.Child = root;
            return card;
        }

        private static Border BuildTooltipMetric(string label, string value, Brush accentBrush)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Foreground = accentBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            });
            panel.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Black,
                Margin = new Thickness(0, 2, 0, 0)
            });

            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Child = panel
            };
        }

        private static void AddCanvasLabel(Canvas canvas, string text, double left, double top, Brush foreground)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.82
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            canvas.Children.Add(label);
        }

        private static Brush TrendBrush(double value) =>
            value >= 0
                ? GetThemeBrush("Brush.Accent", new SolidColorBrush(Color.FromRgb(48, 217, 139)))
                : GetThemeBrush("Brush.Negative", new SolidColorBrush(Color.FromRgb(240, 93, 108)));

        private static string FormatChips(double value) =>
            $"{value:+0.#;-0.#;0} fichas";

        private static string FormatCompactChips(double value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000)
                return $"{value / 1_000_000:+0.#;-0.#;0}M";
            if (abs >= 1_000)
                return $"{value / 1_000:+0.#;-0.#;0}K";
            return $"{value:+0.#;-0.#;0}";
        }

        private static string BlindsLabel(MainWindow.TableSessionStats table)
        {
            var big = table.BigBlind;
            if (big <= 0)
                return "Sin ciegas";

            var small = big / 2d;
            return $"{FormatBlind(small)}/{FormatBlind(big)}";
        }

        private static string FormatBlind(double value) =>
            Math.Abs(value % 1) < 0.001
                ? value.ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static Color GetThemeColor(string key, Color fallback)
        {
            if (Application.Current?.Resources[key] is SolidColorBrush brush)
                return brush.Color;
            return fallback;
        }

        private static Brush GetThemeBrush(string key, Brush fallback)
        {
            if (Application.Current?.Resources[key] is Brush brush)
                return brush;
            return fallback;
        }

        private static string ShortLabel(string label) =>
            label.Length <= 12 ? label : label[..11] + ".";

        private static int StreetOrder(string value) => value switch
        {
            "Preflop" => 0,
            "Flop" => 1,
            "Turn" => 2,
            "River" => 3,
            _ => 9
        };

        private static int PositionOrder(string value) => value switch
        {
            "BTN" or "BTN/SB" => 0,
            "SB" => 1,
            "BB" => 2,
            "UTG" => 3,
            "MP" => 4,
            "HJ" => 5,
            "CO" => 6,
            _ => 9
        };

        private static int ActionOrder(string value) => value switch
        {
            "Fold" => 0,
            "Check" => 1,
            "Call" => 2,
            "Bet" => 3,
            "Raise" => 4,
            "All-in" => 5,
            _ => 9
        };

        private static int BluffOrder(string value) => value switch
        {
            "Bluff ganado" => 0,
            "Bluff perdido" => 1,
            "Valor/agresion" => 2,
            "No agresivo" => 3,
            _ => 9
        };

        private sealed record GainHand(
            string TableName,
            int HandNumber,
            DateTime PlayedAt,
            string Street,
            string Position,
            string Action,
            string BluffType,
            double NetAmount,
            string GameFormat,
            string StakeLabel,
            double NetBb);

        private sealed record GainGroup(
            string Label,
            string AxisLabel,
            int Hands,
            double TotalAmount,
            double AverageAmount,
            double TotalBb,
            double AverageBb);

        private sealed record InsightItem(
            string Label,
            string Result,
            string Detail,
            string Explanation,
            Brush ResultBrush);

        private sealed record PlotGeometry(double Left, double Top, double Right, double Bottom);
    }
}
