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
        private readonly IReadOnlyList<GainHand> _hands;
        private IReadOnlyList<GainGroup> _groups = Array.Empty<GainGroup>();
        private string _mode = "Street";

        public GainAnalysisWindow(IEnumerable<MainWindow.TableSessionStats> tables)
        {
            InitializeComponent();
            FitToWorkArea();
            Loaded += GainAnalysisWindow_Loaded;

            _hands = BuildHands(tables).ToList();
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
                _ => "GANANCIA POR CALLE"
            };
            ModeDescriptionText.Text = _mode switch
            {
                "Position" => "Agrupa la ganancia por asiento del heroe para detectar donde imprime bb y donde se fuga valor.",
                "Action" => "Agrupa por la ultima accion importante del heroe en la mano: fold, check, call, bet, raise o all-in.",
                "Bluff" => "Separa agresion sin showdown, agresion con mano debil y manos no agresivas para leer la calidad del bluff.",
                _ => "Agrupa por la ultima calle donde el heroe tomo una decision: preflop, flop, turn o river."
            };

            SetModeButton(StreetModeButton, _mode == "Street");
            SetModeButton(PositionModeButton, _mode == "Position");
            SetModeButton(ActionModeButton, _mode == "Action");
            SetModeButton(BluffModeButton, _mode == "Bluff");
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
            var total = _hands.Sum(hand => hand.NetBb);
            var average = _hands.Count == 0 ? 0 : total / _hands.Count;
            var best = _groups.OrderByDescending(group => group.TotalBb).FirstOrDefault();
            var worst = _groups.OrderBy(group => group.TotalBb).FirstOrDefault();

            TotalText.Text = $"{total:+0.#;-0.#;0} bb";
            TotalText.Foreground = TrendBrush(total);
            AverageText.Text = $"{average:+0.##;-0.##;0} bb/hand";
            AverageText.Foreground = TrendBrush(average);
            BestText.Text = best is null ? "-" : $"{best.Label} {best.TotalBb:+0.#;-0.#;0} bb";
            BestText.Foreground = best is null ? GetThemeBrush("Brush.Text", Brushes.White) : TrendBrush(best.TotalBb);
            WorstText.Text = worst is null ? "-" : $"{worst.Label} {worst.TotalBb:+0.#;-0.#;0} bb";
            WorstText.Foreground = worst is null ? GetThemeBrush("Brush.Text", Brushes.White) : TrendBrush(worst.TotalBb);
            CurveTotalText.Text = $"{total:+0.#;-0.#;0} bb";
            CurveTotalText.Foreground = TrendBrush(total);
        }

        private void UpdateInsightPanel()
        {
            InsightTitleText.Text = _mode switch
            {
                "Position" => "LECTURA POR POSICION",
                "Action" => "QUE HACE CADA ACCION",
                "Bluff" => "LECTURA DE BLUFF",
                _ => "LECTURA POR CALLE"
            };

            InsightBodyText.Text = _mode switch
            {
                "Position" => "Sirve para encontrar si la ganancia viene de robar, defender, jugar ciegas o de posiciones tardias.",
                "Action" => "La accion es la ultima decision fuerte detectada en la mano. Te dice cuanto estas ganando o perdiendo cuando terminas foldeando, pagando, apostando o resubiendo.",
                "Bluff" => "Es una lectura aproximada desde el historial: no reemplaza revision manual, pero ayuda a separar presion rentable de agresion que cuesta bb.",
                _ => "La calle indica donde se decidio la mano para el heroe. Si una calle sale muy negativa, ahi conviene revisar botes grandes y decisiones repetidas."
            };

            InsightList.ItemsSource = _groups
                .OrderByDescending(group => Math.Abs(group.TotalBb))
                .Select(group => new InsightItem(
                    group.Label,
                    $"{group.TotalBb:+0.#;-0.#;0} bb",
                    $"{group.Hands} manos | media {group.AverageBb:+0.##;-0.##;0} bb/mano",
                    BuildGroupExplanation(_mode, group),
                    TrendBrush(group.TotalBb)))
                .ToList();
        }

        private static string BuildGroupExplanation(string mode, GainGroup group)
        {
            var sample = group.Hands < 25
                ? "Muestra chica: tomalo como pista, no como sentencia."
                : "Muestra suficiente para empezar a revisar patrones.";
            var trend = group.TotalBb >= 0
                ? "Hasta ahora aporta ganancia."
                : "Hasta ahora esta drenando bb.";

            var definition = mode switch
            {
                "Position" => PositionExplanation(group.Label),
                "Action" => ActionExplanation(group.Label),
                "Bluff" => BluffExplanation(group.Label),
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
                var total = hands.Sum(hand => hand.NetBb);
                yield return new GainGroup(
                    group.Key,
                    hands.Count,
                    total,
                    hands.Count == 0 ? 0 : total / hands.Count);
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
            var ordered = _hands.OrderBy(hand => hand.PlayedAt).ThenBy(hand => hand.TableName).ThenBy(hand => hand.HandNumber).ToList();
            if (ordered.Count == 0)
            {
                AddCanvasLabel(CurveCanvas, "Sin datos", CurveCanvas.ActualWidth / 2 - 24, CurveCanvas.ActualHeight / 2 - 8, GetThemeBrush("Brush.Text", Brushes.White));
                return;
            }

            var cumulative = new List<double>();
            var running = 0d;
            foreach (var hand in ordered)
            {
                running += hand.NetBb;
                cumulative.Add(running);
            }

            var geometry = BuildPlot(CurveCanvas.ActualWidth, CurveCanvas.ActualHeight, cumulative, out var points, out var zeroY, out var min, out var max);
            DrawGrid(CurveCanvas, geometry.Left, geometry.Top, geometry.Right, geometry.Bottom);
            DrawZeroLine(CurveCanvas, geometry.Left, geometry.Right, zeroY);

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

            for (var i = 0; i < points.Count; i += Math.Max(1, points.Count / 80))
                AddDot(CurveCanvas, points[i].X, points[i].Y, chartColor, $"{ordered[i].TableName}\n{ordered[i].NetBb:+0.#;-0.#;0} bb");

            AddCanvasLabel(CurveCanvas, $"{max:0.#}", 4, geometry.Top - 2, GetThemeBrush("Brush.TextDim", Brushes.White));
            AddCanvasLabel(CurveCanvas, $"{min:0.#}", 4, geometry.Bottom - 12, GetThemeBrush("Brush.TextDim", Brushes.White));
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
            var maxAbs = Math.Max(1, _groups.Max(group => Math.Abs(group.TotalBb)));
            var zeroY = plotTop + (plotBottom - plotTop) / 2;
            var slot = (plotRight - plotLeft) / _groups.Count;
            var barWidth = Math.Max(22, Math.Min(82, slot * 0.58));

            DrawGrid(BarsCanvas, plotLeft, plotTop, plotRight, plotBottom);
            DrawZeroLine(BarsCanvas, plotLeft, plotRight, zeroY);

            for (var i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                var x = plotLeft + slot * i + (slot - barWidth) / 2;
                var barHeight = Math.Abs(group.TotalBb) / maxAbs * ((plotBottom - plotTop) / 2 - 8);
                var y = group.TotalBb >= 0 ? zeroY - barHeight : zeroY;
                var color = group.TotalBb >= 0
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
                    ToolTip = $"{group.Label}\nTotal: {group.TotalBb:+0.#;-0.#;0} bb\nMedia: {group.AverageBb:+0.##;-0.##;0} bb\nManos: {group.Hands}"
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                BarsCanvas.Children.Add(rect);

                AddCanvasLabel(BarsCanvas, $"{group.TotalBb:+0.#;-0.#;0}", x - 8, group.TotalBb >= 0 ? y - 18 : y + barHeight + 3, TrendBrush(group.TotalBb));
                AddCanvasLabel(BarsCanvas, ShortLabel(group.Label), x - 6, plotBottom + 10, GetThemeBrush("Brush.TextDim", Brushes.White));
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

        private static void AddDot(Canvas canvas, double x, double y, Color color, string tip)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
                ToolTip = tip
            };
            Canvas.SetLeft(dot, x - 3.5);
            Canvas.SetTop(dot, y - 3.5);
            canvas.Children.Add(dot);
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
            double NetBb);

        private sealed record GainGroup(string Label, int Hands, double TotalBb, double AverageBb);

        private sealed record InsightItem(
            string Label,
            string Result,
            string Detail,
            string Explanation,
            Brush ResultBrush);

        private sealed record PlotGeometry(double Left, double Top, double Right, double Bottom);
    }
}
