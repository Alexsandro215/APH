using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class TableDetailWindow : Window
    {
        private readonly TableDetailViewModel _viewModel;
        private readonly List<ChartHitPoint> _chartHitPoints = new();
        private Ellipse? _hoverMarker;
        private Border? _hoverTooltip;
        private TableHandViewModel? _hoveredHand;
        private readonly int? _initialHandNumber;
        private readonly string? _trackedVillainName;

        public TableDetailWindow(MainWindow.TableSessionStats table, int? initialHandNumber = null, string? trackedVillainName = null)
        {
            InitializeComponent();
            Height = Math.Max(760, SystemParameters.WorkArea.Height - 36);
            MaxHeight = SystemParameters.WorkArea.Height;
            _initialHandNumber = initialHandNumber;
            _trackedVillainName = string.IsNullOrWhiteSpace(trackedVillainName) ? null : trackedVillainName.Trim();

            _viewModel = new TableDetailViewModel(table, _initialHandNumber, _trackedVillainName);
            DataContext = _viewModel;
            Title = $"APH - {table.TableName}";

            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TableDetailViewModel.SelectedHand))
                    DrawChart();
            };

            Loaded += (_, _) =>
            {
                if (_initialHandNumber is int handNumber)
                    SelectHand(handNumber);
                DrawChart();
            };
        }

        private void SelectHand(int handNumber)
        {
            var hand = _viewModel.Hands.FirstOrDefault(item => item.Index == handNumber);
            if (hand is null)
                return;

            _viewModel.SelectedHand = hand;
            HandsList.ScrollIntoView(hand);
        }

        private void ProfitChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

        private void DrawChart()
        {
            if (!IsLoaded || ProfitChart.ActualWidth <= 1 || ProfitChart.ActualHeight <= 1)
                return;

            ProfitChart.Children.Clear();
            var points = _viewModel.Hands.Select(hand => hand.CumulativeBb).ToList();
            if (points.Count == 0)
                return;

            const double padLeft = 58;
            const double padRight = 22;
            const double padTop = 24;
            const double padBottom = 42;
            var plotLeft = padLeft;
            var plotRight = ProfitChart.ActualWidth - padRight;
            var plotTop = padTop;
            var plotBottom = ProfitChart.ActualHeight - padBottom;
            var width = Math.Max(1, plotRight - plotLeft);
            var height = Math.Max(1, plotBottom - plotTop);
            var min = Math.Min(0, points.Min());
            var max = Math.Max(0, points.Max());
            if (Math.Abs(max - min) < 0.01)
            {
                max += 1;
                min -= 1;
            }

            double X(int index) => plotLeft + (points.Count == 1 ? width / 2 : width * index / (points.Count - 1));
            double Y(double value) => plotTop + height - ((value - min) / (max - min) * height);

            _chartHitPoints.Clear();
            for (var i = 0; i < _viewModel.Hands.Count; i++)
                _chartHitPoints.Add(new ChartHitPoint(_viewModel.Hands[i], X(i), Y(points[i])));

            AddAxes(min, max, points.Count, plotLeft, plotRight, plotTop, plotBottom, X, Y);

            var zeroY = Y(0);
            ProfitChart.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotRight,
                Y1 = zeroY,
                Y2 = zeroY,
                Stroke = new SolidColorBrush(Color.FromRgb(62, 76, 92)),
                StrokeThickness = 1
            });

            AddProfitSegments(points, X, Y);

            var selected = _viewModel.SelectedHand;
            if (selected is not null)
            {
                var selectedX = X(selected.Index - 1);
                ProfitChart.Children.Add(new Line
                {
                    X1 = selectedX,
                    X2 = selectedX,
                    Y1 = plotTop,
                    Y2 = plotBottom,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Opacity = 0.75
                });

                ProfitChart.Children.Add(new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Margin = new Thickness(selectedX - 5, Y(selected.CumulativeBb) - 5, 0, 0)
                });
            }
        }

        private void ProfitChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var point = e.GetPosition(ProfitChart);
            var hit = FindNearestChartPoint(point, 16);

            if (hit is null)
            {
                ClearHoverVisuals();
                ProfitChart.Cursor = null;
                _hoveredHand = null;
                return;
            }

            _hoveredHand = hit.Hand;
            ProfitChart.Cursor = System.Windows.Input.Cursors.Hand;
            ShowHoverVisuals(hit);
        }

        private void ProfitChart_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ClearHoverVisuals();
            ProfitChart.Cursor = null;
            _hoveredHand = null;
        }

        private void ProfitChart_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var point = e.GetPosition(ProfitChart);
            var hit = FindNearestChartPoint(point, 18);
            if (hit is null)
                return;

            _viewModel.SelectedHand = hit.Hand;
            HandsList.ScrollIntoView(hit.Hand);
            e.Handled = true;
        }

        private ChartHitPoint? FindNearestChartPoint(Point point, double threshold)
        {
            if (_chartHitPoints.Count == 0)
                return null;

            var nearest = _chartHitPoints
                .Select(hit => new
                {
                    Hit = hit,
                    Distance = Math.Sqrt(Math.Pow(hit.X - point.X, 2) + Math.Pow(hit.Y - point.Y, 2))
                })
                .OrderBy(item => item.Distance)
                .First();

            return nearest.Distance <= threshold ? nearest.Hit : null;
        }

        private void ShowHoverVisuals(ChartHitPoint hit)
        {
            ShowHoverMarker(hit);
            ShowHoverTooltip(hit);
        }

        private void ShowHoverMarker(ChartHitPoint hit)
        {
            if (_hoverMarker is not null)
                ProfitChart.Children.Remove(_hoverMarker);

            _hoverMarker = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)),
                Stroke = hit.Hand.IsProfitable
                    ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                    : new SolidColorBrush(Color.FromRgb(226, 78, 91)),
                StrokeThickness = 3,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_hoverMarker, hit.X - 7);
            Canvas.SetTop(_hoverMarker, hit.Y - 7);
            ProfitChart.Children.Add(_hoverMarker);
        }

        private void ShowHoverTooltip(ChartHitPoint hit)
        {
            if (_hoverTooltip is not null)
                ProfitChart.Children.Remove(_hoverTooltip);

            var text = new TextBlock
            {
                Text = hit.Hand.ChartTooltip,
                Foreground = Brushes.Black,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                LineHeight = 16
            };

            _hoverTooltip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(238, 242, 246)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(143, 211, 244)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Child = text,
                IsHitTestVisible = false
            };

            _hoverTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var tooltipWidth = _hoverTooltip.DesiredSize.Width;
            var tooltipHeight = _hoverTooltip.DesiredSize.Height;
            var left = hit.X + 12;
            var top = hit.Y + 12;

            if (left + tooltipWidth > ProfitChart.ActualWidth - 4)
                left = hit.X - tooltipWidth - 12;
            if (top + tooltipHeight > ProfitChart.ActualHeight - 4)
                top = hit.Y - tooltipHeight - 12;

            Canvas.SetLeft(_hoverTooltip, Math.Max(4, left));
            Canvas.SetTop(_hoverTooltip, Math.Max(4, top));
            Panel.SetZIndex(_hoverTooltip, 20);
            ProfitChart.Children.Add(_hoverTooltip);
        }

        private void ClearHoverVisuals()
        {
            ClearHoverMarker();
            ClearHoverTooltip();
        }

        private void ClearHoverMarker()
        {
            if (_hoverMarker is null)
                return;

            ProfitChart.Children.Remove(_hoverMarker);
            _hoverMarker = null;
        }

        private void ClearHoverTooltip()
        {
            if (_hoverTooltip is null)
                return;

            ProfitChart.Children.Remove(_hoverTooltip);
            _hoverTooltip = null;
        }

        private void AddProfitSegments(IReadOnlyList<double> points, Func<int, double> x, Func<double, double> y)
        {
            if (points.Count == 1)
            {
                var point = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = points[0] < 0
                        ? new SolidColorBrush(Color.FromRgb(226, 78, 91))
                        : new SolidColorBrush(Color.FromRgb(33, 192, 122))
                };
                Canvas.SetLeft(point, x(0) - 3.5);
                Canvas.SetTop(point, y(points[0]) - 3.5);
                ProfitChart.Children.Add(point);
                return;
            }

            for (var i = 1; i < points.Count; i++)
            {
                var previous = points[i - 1];
                var current = points[i];
                var previousX = x(i - 1);
                var currentX = x(i);

                if ((previous < 0 && current < 0) || (previous >= 0 && current >= 0))
                {
                    AddProfitLine(previousX, y(previous), currentX, y(current), current < 0);
                    continue;
                }

                var crossingRatio = Math.Abs(previous) / Math.Abs(current - previous);
                var crossX = previousX + (currentX - previousX) * crossingRatio;
                var crossY = y(0);

                AddProfitLine(previousX, y(previous), crossX, crossY, previous < 0);
                AddProfitLine(crossX, crossY, currentX, y(current), current < 0);
            }
        }

        private void AddProfitLine(double x1, double y1, double x2, double y2, bool isBelowStartStack)
        {
            ProfitChart.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(isBelowStartStack ? Color.FromRgb(226, 78, 91) : Color.FromRgb(33, 192, 122)),
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        private void AddAxes(
            double min,
            double max,
            int handCount,
            double plotLeft,
            double plotRight,
            double plotTop,
            double plotBottom,
            Func<int, double> x,
            Func<double, double> y)
        {
            var axisBrush = new SolidColorBrush(Color.FromRgb(77, 94, 112));
            var gridBrush = new SolidColorBrush(Color.FromRgb(38, 50, 64));
            var textBrush = new SolidColorBrush(Color.FromRgb(164, 184, 203));

            ProfitChart.Children.Add(new Line { X1 = plotLeft, X2 = plotLeft, Y1 = plotTop, Y2 = plotBottom, Stroke = axisBrush, StrokeThickness = 1 });
            ProfitChart.Children.Add(new Line { X1 = plotLeft, X2 = plotRight, Y1 = plotBottom, Y2 = plotBottom, Stroke = axisBrush, StrokeThickness = 1 });

            foreach (var tick in BuildTicks(min, max, 5))
            {
                var tickY = y(tick);
                ProfitChart.Children.Add(new Line
                {
                    X1 = plotLeft,
                    X2 = plotRight,
                    Y1 = tickY,
                    Y2 = tickY,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    Opacity = tick == 0 ? 0.95 : 0.55
                });

                AddLabel($"{tick:F0} bb", 6, tickY - 8, textBrush);
            }

            foreach (var index in BuildHandTicks(handCount, 5))
            {
                var tickX = x(index - 1);
                ProfitChart.Children.Add(new Line
                {
                    X1 = tickX,
                    X2 = tickX,
                    Y1 = plotBottom,
                    Y2 = plotBottom + 5,
                    Stroke = axisBrush,
                    StrokeThickness = 1
                });

                AddLabel(index.ToString(CultureInfo.InvariantCulture), tickX - 8, plotBottom + 8, textBrush);
            }

            AddLabel("bb", 8, plotTop - 20, textBrush);
            AddLabel("manos", plotRight - 42, plotBottom + 22, textBrush);
        }

        private void AddLabel(string text, double left, double top, Brush brush)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 11
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            ProfitChart.Children.Add(label);
        }

        private static IEnumerable<double> BuildTicks(double min, double max, int targetCount)
        {
            if (targetCount <= 1)
                yield break;

            var step = (max - min) / (targetCount - 1);
            for (var i = 0; i < targetCount; i++)
                yield return min + step * i;
        }

        private static IEnumerable<int> BuildHandTicks(int handCount, int targetCount)
        {
            if (handCount <= 1)
            {
                yield return 1;
                yield break;
            }

            var seen = new HashSet<int>();
            for (var i = 0; i < targetCount; i++)
            {
                var value = 1 + (int)Math.Round((handCount - 1) * i / (double)(targetCount - 1));
                if (seen.Add(value))
                    yield return value;
            }
        }

        private sealed class TableDetailViewModel : INotifyPropertyChanged
        {
            private TableHandViewModel? _selectedHand;

            public ObservableCollection<TableHandViewModel> Hands { get; }

            public TableDetailViewModel(MainWindow.TableSessionStats table, int? trackedHandNumber = null, string? trackedVillainName = null)
            {
                Table = table;
                Hands = new ObservableCollection<TableHandViewModel>(LoadHands(table, trackedHandNumber, trackedVillainName));
                SelectedHand = Hands.FirstOrDefault();
            }

            public MainWindow.TableSessionStats Table { get; }
            public string TableName => Table.TableName;
            public string GameFormat => Table.GameFormat;
            public string PlayedDate => Table.PlayedDate;
            public string MoneyTypeLabel => Table.IsCash ? "Cash" : "Fichas";
            public int HandsReceived => Table.HandsReceived;
            public double NetBb => Table.NetBb;
            public string NetAmountLabel => Table.NetAmountLabel;
            public bool IsWin => Table.IsWin;
            public bool IsProfitable => Table.IsProfitable;
            public string TrendIcon => Table.TrendIcon;
            public Services.StakeProfile Stake => Table.Stake;
            public double VPIPPct => Table.VPIPPct;
            public double PFRPct => Table.PFRPct;
            public double ThreeBetPct => Table.ThreeBetPct;
            public double AF => Table.AF;
            public double AFqPct => Table.AFqPct;
            public double CBetFlopPct => Table.CBetFlopPct;
            public double FoldVsCBetFlopPct => Table.FoldVsCBetFlopPct;
            public double WTSDPct => Table.WTSDPct;
            public double WSDPct => Table.WSDPct;
            public double WWSFPct => Table.WWSFPct;

            public TableHandViewModel? SelectedHand
            {
                get => _selectedHand;
                set
                {
                    if (_selectedHand == value) return;
                    _selectedHand = value;
                    OnPropertyChanged(nameof(SelectedHand));
                    OnPropertyChanged(nameof(SelectedHandTitle));
                }
            }

            public string SelectedHandTitle =>
                SelectedHand is null
                    ? "Selecciona una mano"
                    : $"{SelectedHand.HandNumberLabel} | {SelectedHand.CardsLabel} | {SelectedHand.NetBbLabel}";

            public string SelectedHandTitleSuffix =>
                SelectedHand is null ? "" : $" | {SelectedHand.NetBbLabel}";

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            private static IReadOnlyList<TableHandViewModel> LoadHands(
                MainWindow.TableSessionStats table,
                int? trackedHandNumber,
                string? trackedVillainName)
            {
                if (!File.Exists(table.SourcePath))
                    return Array.Empty<TableHandViewModel>();

                var hands = SplitHands(File.ReadLines(table.SourcePath)).ToList();
                var result = new List<TableHandViewModel>();
                var cumulative = 0.0;

                foreach (var hand in hands)
                {
                    if (!TryExtractHeroCards(hand, table.HeroName, out var cards))
                        continue;

                    var handIndex = result.Count + 1;
                    var shouldTrackVillain = trackedHandNumber == handIndex && !string.IsNullOrWhiteSpace(trackedVillainName);
                    var positions = BuildPositionMap(hand);
                    var netAmount = EstimateHeroNetForHand(hand, table.HeroName);
                    var netBb = table.BigBlind > 0 ? netAmount / table.BigBlind : 0;
                    cumulative += netBb;

                    result.Add(new TableHandViewModel(
                        handIndex,
                        FormatCards(cards),
                        positions.TryGetValue(table.HeroName, out var heroPosition) ? $"Posicion {heroPosition}" : "Posicion ?",
                        netBb,
                        cumulative,
                        ExtractStreetActions(hand, "PREFLOP", table.HeroName, positions, shouldTrackVillain ? trackedVillainName : null, table.BigBlind),
                        ExtractStreetActions(hand, "FLOP", table.HeroName, positions, shouldTrackVillain ? trackedVillainName : null, table.BigBlind),
                        ExtractStreetActions(hand, "TURN", table.HeroName, positions, shouldTrackVillain ? trackedVillainName : null, table.BigBlind),
                        ExtractStreetActions(hand, "RIVER", table.HeroName, positions, shouldTrackVillain ? trackedVillainName : null, table.BigBlind)));
                }

                return result;
            }

            private static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
            {
                return PokerStarsHandHistory.SplitHands(lines);
            }

            private static bool TryExtractHeroCards(IReadOnlyList<string> hand, string heroName, out string cards)
            {
                if (PokerStarsHandHistory.TryGetDealtCards(hand, heroName, out cards))
                    return true;

                cards = "--";
                return false;
            }

            private static string FormatCards(string cards) =>
                string.Join(" ", cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(FormatCardForDisplay));

            private static string FormatBoardCards(string cards) =>
                FormatCards(cards.Replace("[", "").Replace("]", ""));

            private static string FormatCard(string card)
            {
                if (card.Length < 2)
                    return card;

                var rank = card[..^1];
                return card[^1] switch
                {
                    'h' or 'H' => $"{rank}♥",
                    'd' or 'D' => $"{rank}♦",
                    'c' or 'C' => $"{rank}♣",
                    's' or 'S' => $"{rank}♠",
                    _ => card
                };
            }

            private static string FormatCardForDisplay(string card)
            {
                if (card.Length < 2)
                    return card;

                var rank = card[..^1];
                var suit = char.ToLowerInvariant(card[^1]) switch
                {
                    'h' => "\u2665",
                    'd' => "\u2666",
                    'c' => "\u2663",
                    's' => "\u2660",
                    _ => ""
                };

                return suit.Length == 0 ? card : $"{rank}{suit}";
            }

            private static Dictionary<string, string> BuildPositionMap(IReadOnlyList<string> hand)
            {
                return PokerStarsHandHistory.BuildPositionMap(hand);
            }

            private static string PositionFromOffset(int offset, int playerCount) =>
                playerCount switch
                {
                    <= 2 => offset == 0 ? "BTN/SB" : "BB",
                    3 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", _ => "?" },
                    4 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "CO", _ => "?" },
                    5 => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "CO", _ => "?" },
                    _ => offset switch { 0 => "BTN", 1 => "SB", 2 => "BB", 3 => "UTG", 4 => "HJ", 5 => "CO", _ => "MP" }
                };

            private static IReadOnlyList<StreetActionViewModel> ExtractStreetActions(
                IReadOnlyList<string> hand,
                string street,
                string heroName,
                IReadOnlyDictionary<string, string> positions,
                string? trackedVillainName,
                double bigBlind)
            {
                var lines = GetStreetLines(hand, street)
                    .Where(IsActionOrBoardLine)
                    .Select(line => CreateAction(line, heroName, positions, trackedVillainName, bigBlind))
                    .ToList();

                return lines.Count == 0
                    ? new[] { new StreetActionViewModel("Sin accion registrada.", false, true, false, false) }
                    : lines;
            }

            private static IEnumerable<string> GetStreetLines(IReadOnlyList<string> hand, string street)
            {
                var start = street == "PREFLOP"
                    ? 0
                    : FindStreetIndex(hand, street);

                if (start < 0)
                    return Enumerable.Empty<string>();

                var end = hand.Count;
                foreach (var next in street switch
                {
                    "PREFLOP" => new[] { "FLOP", "TURN", "RIVER", "SHOW" },
                    "FLOP" => new[] { "TURN", "RIVER", "SHOW" },
                    "TURN" => new[] { "RIVER", "SHOW" },
                    _ => new[] { "SHOW" }
                })
                {
                    var idx = FindStreetIndex(hand, next, start + 1);
                    if (idx >= 0)
                    {
                        end = idx;
                        break;
                    }
                }

                return hand.Skip(start).Take(end - start);
            }

            private static int FindStreetIndex(IReadOnlyList<string> hand, string street, int startAt = 0)
            {
                return PokerStarsHandHistory.FindStreetIndex(hand, street, startAt);
            }

            private static bool IsActionOrBoardLine(string line)
            {
                return IsPlayerActionLine(line) ||
                       line.StartsWith("Dealt to ", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("Repartido a ", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("Uncalled bet", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("La apuesta no igualada", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(" collected ", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(" cobra ", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(" se llev", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(" shows ", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(" muestra ", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("*** FLOP ***", StringComparison.Ordinal) ||
                       line.StartsWith("*** TURN ***", StringComparison.Ordinal) ||
                       line.StartsWith("*** RIVER ***", StringComparison.Ordinal);
            }

            private static bool IsPlayerActionLine(string line)
            {
                var match = PokerStarsHandHistory.ActorRx.Match(line);
                if (!match.Success)
                    return false;

                var action = PokerStarsHandHistory.NormalizeAction(match.Groups["action"].Value);
                return action is "posts" or "folds" or "checks" or "calls" or "bets" or "raises" or "all-in" or "mucks" ||
                    match.Groups["action"].Value.Contains("shows", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups["action"].Value.Contains("muestra", StringComparison.OrdinalIgnoreCase);
            }

            private static StreetActionViewModel CreateAction(
                string line,
                string heroName,
                IReadOnlyDictionary<string, string> positions,
                string? trackedVillainName,
                double bigBlind)
            {
                var actor = ExtractActor(line);
                var isHero = PokerStarsHandHistory.SamePlayer(actor, heroName);
                var isTrackedVillain = !string.IsNullOrWhiteSpace(trackedVillainName) &&
                    PokerStarsHandHistory.SamePlayer(actor, trackedVillainName);
                var isSystem = actor.Length == 0;
                var clean = TranslateActionLine(line, actor, heroName, positions, bigBlind);
                var isBoardHeader = line.StartsWith("*** FLOP ***", StringComparison.Ordinal) ||
                                    line.StartsWith("*** TURN ***", StringComparison.Ordinal) ||
                                    line.StartsWith("*** RIVER ***", StringComparison.Ordinal);

                return new StreetActionViewModel(clean, isHero, isSystem, isBoardHeader, isTrackedVillain);
            }

            private static string ExtractActor(string line)
            {
                var dealt = PokerStarsHandHistory.DealtRx.Match(line);
                if (dealt.Success)
                    return PokerStarsHandHistory.NormalizeName(dealt.Groups["hero"].Value);

                var actor = PokerStarsHandHistory.ActorRx.Match(line);
                if (actor.Success)
                    return PokerStarsHandHistory.NormalizeName(actor.Groups["actor"].Value);

                var collected = PokerStarsHandHistory.CollectedRx.Match(line);
                if (collected.Success)
                    return PokerStarsHandHistory.NormalizeName(collected.Groups["name"].Value);

                var shows = PokerStarsHandHistory.ShowCardsRx.Match(line);
                if (shows.Success)
                    return PokerStarsHandHistory.NormalizeName(shows.Groups["name"].Value);

                return "";
            }

            private static string TranslateActionLine(
                string line,
                string actor,
                string heroName,
                IReadOnlyDictionary<string, string> positions,
                double bigBlind)
            {
                if (line.StartsWith("***", StringComparison.Ordinal))
                    return FormatStreetHeader(line);

                var dealt = PokerStarsHandHistory.DealtRx.Match(line);
                if (dealt.Success)
                {
                    var hero = PokerStarsHandHistory.NormalizeName(dealt.Groups["hero"].Value);
                    return $"{ActorLabel(hero, heroName, positions)} recibe {FormatCards(dealt.Groups["cards"].Value)}";
                }

                var show = PokerStarsHandHistory.ShowCardsRx.Match(line);
                if (show.Success)
                    return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(show.Groups["name"].Value), heroName, positions)} muestra {FormatCards(show.Groups["cards"].Value)}";

                var collected = PokerStarsHandHistory.CollectedRx.Match(line);
                if (collected.Success)
                {
                    var amount = collected.Groups["amount"].Success ? collected.Groups["amount"].Value : collected.Groups["amount2"].Value;
                    return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(collected.Groups["name"].Value), heroName, positions)} cobra {FormatAmountAsBb(amount, bigBlind)}";
                }

                var returned = PokerStarsHandHistory.ReturnedRx.Match(line);
                if (returned.Success)
                {
                    var name = returned.Groups["name"].Success ? returned.Groups["name"].Value : returned.Groups["name2"].Value;
                    var amount = returned.Groups["amount"].Success ? returned.Groups["amount"].Value : returned.Groups["amount2"].Value;
                    return $"{ActorLabel(PokerStarsHandHistory.NormalizeName(name), heroName, positions)} recupera apuesta no pagada {FormatAmountAsBb(amount, bigBlind)}";
                }

                if (!string.IsNullOrWhiteSpace(actor))
                    return $"{ActorLabel(actor, heroName, positions)} {TranslatePlayerAction(line[(actor.Length + 1)..].Trim(), bigBlind)}";

                return line.Trim();
            }

            private static string ActorLabel(
                string player,
                string heroName,
                IReadOnlyDictionary<string, string> positions)
            {
                var position = positions.TryGetValue(player, out var value) ? value : "?";
                var prefix = PokerStarsHandHistory.SamePlayer(player, heroName) ? "Heroe " : "";
                return $"{position}-{prefix}{player}:";
            }

            private static string FormatStreetHeader(string line)
            {
                var match = Regex.Match(
                    line,
                    @"^\*\*\*\s+(?<street>FLOP|TURN|RIVER)\s+\*\*\*\s+(?<cards>.+)$",
                    RegexOptions.IgnoreCase);

                if (!match.Success)
                    return line.Replace("*", "").Trim();

                var street = match.Groups["street"].Value.ToUpperInvariant();
                var cards = FormatBoardCards(match.Groups["cards"].Value);
                return $"{street} [{cards}]";
            }

            private static string TranslatePlayerAction(string action, double bigBlind)
            {
                var normalized = action.Trim();
                var raise = Regex.Match(normalized, @"(?:raises|sube)\s+(?<from>\$?[\d,.]+)\s+(?:to|a|hasta)\s+(?<to>\$?[\d,.]+)", RegexOptions.IgnoreCase);
                if (raise.Success)
                    return $"sube {FormatAmountAsBb(raise.Groups["from"].Value, bigBlind)} hasta {FormatAmountAsBb(raise.Groups["to"].Value, bigBlind)}";

                var smallBlind = Regex.Match(normalized, @"^posts small blind\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
                if (smallBlind.Success)
                    return $"pone ciega chica {FormatAmountAsBb(smallBlind.Groups["amount"].Value, bigBlind)}";

                var bigBlindPost = Regex.Match(normalized, @"^posts big blind\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
                if (bigBlindPost.Success)
                    return $"pone ciega grande {FormatAmountAsBb(bigBlindPost.Groups["amount"].Value, bigBlind)}";

                var ante = Regex.Match(normalized, @"^posts the ante\s+\$?(?<amount>[\d,.]+)", RegexOptions.IgnoreCase);
                if (ante.Success)
                    return $"pone ante {FormatAmountAsBb(ante.Groups["amount"].Value, bigBlind)}";

                var call = Regex.Match(normalized, @"^(?:calls|iguala|paga)\s+\$?(?<amount>[\d,.]+)(?<rest>.*)$", RegexOptions.IgnoreCase);
                if (call.Success)
                    return $"paga {FormatAmountAsBb(call.Groups["amount"].Value, bigBlind)}{TranslateAllInText(call.Groups["rest"].Value)}";

                var bet = Regex.Match(normalized, @"^(?:bets|apuesta)\s+\$?(?<amount>[\d,.]+)(?<rest>.*)$", RegexOptions.IgnoreCase);
                if (bet.Success)
                    return $"apuesta {FormatAmountAsBb(bet.Groups["amount"].Value, bigBlind)}{TranslateAllInText(bet.Groups["rest"].Value)}";

                return Regex.Replace(normalized, @"^posts small blind", "pone ciega chica", RegexOptions.IgnoreCase)
                    .Replace("posts big blind", "pone ciega grande", StringComparison.OrdinalIgnoreCase)
                    .Replace("posts the ante", "pone ante", StringComparison.OrdinalIgnoreCase)
                    .Replace("folds", "se retira", StringComparison.OrdinalIgnoreCase)
                    .Replace("checks", "pasa", StringComparison.OrdinalIgnoreCase)
                    .Replace("calls", "paga", StringComparison.OrdinalIgnoreCase)
                    .Replace("bets", "apuesta", StringComparison.OrdinalIgnoreCase)
                    .Replace("and is all-in", "y va all-in", StringComparison.OrdinalIgnoreCase)
                    .Replace("is all-in", "va all-in", StringComparison.OrdinalIgnoreCase)
                    .Replace("mucks", "descarta", StringComparison.OrdinalIgnoreCase);
            }

            private static string TranslateAllInText(string text) =>
                text.Replace("and is all-in", " y va all-in", StringComparison.OrdinalIgnoreCase)
                    .Replace("is all-in", " va all-in", StringComparison.OrdinalIgnoreCase);

            private static string FormatAmountAsBb(string raw, double bigBlind)
            {
                if (bigBlind <= 0 || !TryParseAmount(raw, out var amount))
                    return raw.Trim();

                var bb = amount / bigBlind;
                var formatted = Math.Abs(bb - Math.Round(bb)) < 0.05
                    ? Math.Round(bb).ToString("0", CultureInfo.InvariantCulture)
                    : bb.ToString("0.#", CultureInfo.InvariantCulture);
                return $"{formatted} bb";
            }

            private static string TranslateShowdownText(string text) =>
                text.Replace("three of a kind", "trio", StringComparison.OrdinalIgnoreCase)
                    .Replace("two pair", "doble pareja", StringComparison.OrdinalIgnoreCase)
                    .Replace("one pair", "pareja", StringComparison.OrdinalIgnoreCase)
                    .Replace("high card", "carta alta", StringComparison.OrdinalIgnoreCase);

            private static string TranslatePotText(string text) =>
                text.Replace("from main pot", " del bote principal", StringComparison.OrdinalIgnoreCase)
                    .Replace("from side pot", " del bote secundario", StringComparison.OrdinalIgnoreCase);

            private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
            {
                return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
            }

            private static bool TryParseAmount(string raw, out double value)
            {
                if (Hud.App.Services.PokerAmountParser.TryParse(raw, out value))
                    return true;

                raw = raw.Replace("$", "")
                    .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("\u00A0", "")
                    .Replace(" ", "")
                    .Replace(",", ".");
                return double.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            }
        }

        private sealed record TableHandViewModel(
            int Index,
            string Cards,
            string PositionLabel,
            double NetBb,
            double CumulativeBb,
            IReadOnlyList<StreetActionViewModel> PreflopActions,
            IReadOnlyList<StreetActionViewModel> FlopActions,
            IReadOnlyList<StreetActionViewModel> TurnActions,
            IReadOnlyList<StreetActionViewModel> RiverActions)
        {
            public string HandNumberLabel => $"Mano {Index}";
            public string CardsLabel => Cards;
            public IReadOnlyList<CardChipViewModel> CardChips => CardChipViewModel.FromCards(Cards);
            public string NetBbLabel => $"{(NetBb >= 0 ? "+" : "")}{NetBb:F0} bb";
            public string CumulativeBbLabel => $"{(CumulativeBb >= 0 ? "+" : "")}{CumulativeBb:F0} bb total";
            public string ChartTooltip => $"{HandNumberLabel}\n{CardsLabel}\n{NetBbLabel}\n{CumulativeBbLabel}";
            public bool IsProfitable => NetBb >= 0;
            public bool IsHugeWin => NetBb >= 50;
            public Brush ResultBackground => ResultBrush(NetBb);

            private static Brush ResultBrush(double netBb)
            {
                var abs = Math.Abs(netBb);

                if (abs < 5)
                    return new SolidColorBrush(Color.FromRgb(18, 24, 32));

                if (netBb > 0)
                {
                    if (abs < 25)
                        return new SolidColorBrush(Color.FromRgb(27, 89, 83));
                    if (abs < 50)
                        return new SolidColorBrush(Color.FromRgb(22, 123, 67));
                    return new SolidColorBrush(Color.FromRgb(153, 132, 24));
                }

                if (abs < 25)
                    return new SolidColorBrush(Color.FromRgb(103, 45, 50));
                if (abs < 50)
                    return new SolidColorBrush(Color.FromRgb(170, 44, 48));
                return new SolidColorBrush(Color.FromRgb(90, 18, 28));
            }
        }

        private sealed class StreetActionViewModel
        {
            public StreetActionViewModel(string text, bool isHero, bool isSystem, bool isBoardHeader, bool isTrackedVillain)
            {
                Text = text;
                IsHero = isHero;
                IsSystem = isSystem;
                IsBoardHeader = isBoardHeader;
                IsTrackedVillain = isTrackedVillain;

                var baseForeground = isTrackedVillain
                    ? new SolidColorBrush(Color.FromRgb(255, 132, 146))
                    : isHero
                    ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                    : isSystem
                        ? new SolidColorBrush(Color.FromRgb(143, 211, 244))
                        : Brushes.White;

                Background = isTrackedVillain
                    ? new SolidColorBrush(Color.FromRgb(61, 22, 30))
                    : isHero
                    ? new SolidColorBrush(Color.FromRgb(16, 37, 28))
                    : isSystem
                        ? new SolidColorBrush(Color.FromRgb(16, 28, 41))
                        : new SolidColorBrush(Color.FromRgb(17, 24, 32));

                VisualParts = BuildActionVisualParts(text, baseForeground, isBoardHeader);
            }

            public string Text { get; }
            public bool IsHero { get; }
            public bool IsSystem { get; }
            public bool IsBoardHeader { get; }
            public bool IsTrackedVillain { get; }
            public Brush Background { get; }
            public IReadOnlyList<ActionVisualPartViewModel> VisualParts { get; }
        }

        private sealed record ActionVisualPartViewModel(
            string Text,
            Brush Foreground,
            FontWeight FontWeight,
            IReadOnlyList<CardChipViewModel> CardChips)
        {
            public bool IsCards => CardChips.Count > 0;
            public bool IsText => !IsCards;
        }

        private static IReadOnlyList<ActionVisualPartViewModel> BuildActionVisualParts(string text, Brush baseForeground, bool boardHeader)
        {
            var segments = new List<ActionVisualPartViewModel>();
            var buffer = "";
            var textBrush = boardHeader ? new SolidColorBrush(Color.FromRgb(143, 211, 244)) : baseForeground;

            void Flush()
            {
                if (buffer.Length == 0)
                    return;

                segments.Add(new ActionVisualPartViewModel(
                    buffer,
                    textBrush,
                    FontWeights.SemiBold,
                    Array.Empty<CardChipViewModel>()));
                buffer = "";
            }

            foreach (Match token in Regex.Matches(text, @"\S+|\s+"))
            {
                var value = token.Value;
                if (!CardChipViewModel.LooksLikeCardToken(value))
                {
                    buffer += value;
                    continue;
                }

                Flush();
                segments.Add(new ActionVisualPartViewModel(
                    "",
                    textBrush,
                    FontWeights.Bold,
                    CardChipViewModel.FromCards(value)));
            }

            Flush();
            return segments;
        }

        private sealed record ChartHitPoint(TableHandViewModel Hand, double X, double Y);
    }
}
