using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class MisTablasWindow : Window
    {
        private static readonly string[] Ranks = { "A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2" };
        private static readonly Regex DealtRx = PokerStarsHandHistory.DealtRx;
        private static readonly Regex ActorRx = PokerStarsHandHistory.ActorRx;
        private static readonly Regex CollectedRx = PokerStarsHandHistory.CollectedRx;
        private static readonly Regex ReturnedRx = PokerStarsHandHistory.ReturnedRx;
        private static readonly Regex RaiseToRx = PokerStarsHandHistory.RaiseToRx;
        private static readonly Regex ActionAmountRx = PokerStarsHandHistory.ActionAmountRx;
        private static readonly Regex BoardCardsRx = new(@"\[(?<cards>[^\]]+)\]", RegexOptions.Compiled);

        private readonly IReadOnlyList<MainWindow.TableSessionStats> _tables;
        private readonly List<ExactHandItem> _allExamples = new();
        private readonly Dictionary<string, HandCellStats> _cells = new(StringComparer.OrdinalIgnoreCase);
        private HandCellStats? _selectedCell;
        private HeroAction? _exactActionFilter;
        private string _exactResultFilter = "ALL";
        private bool _colorByProfit;
        private bool _compareChart = true;
        private string _street = "PREFLOP";
        private readonly Dictionary<string, Border> _selectionMarks = new(StringComparer.Ordinal);

        /// <summary>Con 5 manos o mas la casilla se pinta entera; con menos, mas tenue.</summary>
        private const int FullSample = 5;

        private static readonly HeroAction[] ActionDisplayOrder =
        {
            HeroAction.Fold,
            HeroAction.Check,
            HeroAction.Call,
            HeroAction.Bet,
            HeroAction.Raise,
            HeroAction.ThreeBet,
            HeroAction.FourBetPlus,
            HeroAction.AllIn
        };

        public MisTablasWindow(IEnumerable<MainWindow.TableSessionStats> tables, string summary)
        {
            InitializeComponent();
            FitHeightToScreen();
            Loaded += MisTablasWindow_Loaded;
            _tables = tables.ToList();
            LocalizeControls();
            UpdateStreetButtons();

            // Leer y procesar todos los historiales congelaba la ventana: va en segundo plano.
            BackgroundLoad.Run(this, () => { LoadStats(); return true; }, _ =>
            {
                PopulateFilterOptions();
                RebuildFilteredCells();
                RenderGrid();
            });
        }

        private void MisTablasWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MisTablasWindow_Loaded;
            FitHeightToScreen();
        }

        private void FitHeightToScreen()
        {
            var workArea = SystemParameters.WorkArea;
            const double desiredWidth = 1420;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(desiredWidth, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        /// <summary>Acciones en el idioma de APH y nombres para el control por voz.</summary>
        private void LocalizeControls()
        {
            foreach (var (chip, action) in new[]
                     {
                         (ActionFoldChip, HeroAction.Fold), (ActionCheckChip, HeroAction.Check), (ActionCallChip, HeroAction.Call),
                         (ActionBetChip, HeroAction.Bet), (ActionRaiseChip, HeroAction.Raise), (ActionThreeBetChip, HeroAction.ThreeBet),
                         (ActionFourBetChip, HeroAction.FourBetPlus), (ActionAllInChip, HeroAction.AllIn)
                     })
            {
                chip.Content = LabelForAction(action);
            }

            SetName(SituationFilter, T("Mine.Situation"));
            SetName(PositionFilter, T("Common.Position"));
            SetName(FormatFilter, T("Grid.Format"));
            SetName(MoneyTypeFilter, T("Common.Type"));
            SetName(FromDateFilter, T("Common.From"));
            SetName(ToDateFilter, T("Common.To"));
        }

        private void Street_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string street)
                return;

            _street = street;
            RenderGrid();
        }

        /// <summary>La calle que se esta viendo queda marcada con el color de acento.</summary>
        private void UpdateStreetButtons()
        {
            foreach (var button in new[] { BtnPreflop, BtnFlop, BtnTurn, BtnRiver })
            {
                var active = (string)button.Tag == _street;
                button.SetResourceReference(BackgroundProperty, active ? "Brush.AccentSoft" : "Brush.Card");
                button.SetResourceReference(BorderBrushProperty, active ? "Brush.Accent" : "Brush.Border");
                button.SetResourceReference(ForegroundProperty, active ? "Brush.Accent" : "Brush.Text");
                button.BorderThickness = new Thickness(active ? 2 : 1);
                button.FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }

        private void CompareChartToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Se llama tambien al crear la ventana (IsChecked="True"), antes de tener datos.
            _compareChart = (sender as CheckBox)?.IsChecked == true;
            if (_cells.Count > 0)
                RenderGrid();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_allExamples.Count == 0)
                return;

            RebuildFilteredCells();
            RenderGrid();
        }

        private void ProfitColorToggle_Changed(object sender, RoutedEventArgs e)
        {
            _colorByProfit = ProfitColorToggle.IsChecked == true;
            RenderGrid();
        }

        private void LoadStats()
        {
            AphPreflopProvider? charts = null;
            try
            {
                charts = PreflopLibrary.Charts;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Mis Tablas: no se pudieron cargar las tablas de APH", ex);
            }

            foreach (var table in _tables)
            {
                if (!File.Exists(table.SourcePath))
                    continue;

                var tableHandNumber = 0;
                var cumulativeBb = 0.0;
                foreach (var hand in SplitHands(File.ReadLines(table.SourcePath)))
                {
                    if (!TryGetHeroHandInfo(hand, table.HeroName, out var handCode, out var exactCards))
                        continue;

                    tableHandNumber++;
                    var netAmount = EstimateHeroNetForHand(hand, table.HeroName);
                    var netBb = table.BigBlind > 0 ? netAmount / table.BigBlind : 0;
                    cumulativeBb += netBb;
                    var spot = ExtractBoardSpot(hand);
                    var positions = BuildPositionMap(hand);
                    var position = positions.TryGetValue(table.HeroName, out var heroPosition)
                        ? heroPosition
                        : "?";
                    var handDateTime = ExtractHandDateTime(hand) ?? table.LastPlayedAt;

                    // Cada decision preflop con su situacion (abrir, contra subida...) y lo que dice la tabla de APH.
                    var decisions = ReadPreflopDecisions(hand, table.HeroName, charts);
                    // Pasar gratis nunca es error (ciega grande, o quien ya puso la ciega al entrar a la
                    // mesa): si la tabla no sube con esta mano, lo correcto era pasar.
                    if (decisions.Count > 0 && DetectHeroAction(hand, table.HeroName, "PREFLOP") == HeroAction.Check)
                        decisions[0] = decisions[0] with { HeroChecked = true, ChartCode = decisions[0].ChartCode == "F" ? "C" : decisions[0].ChartCode };
                    var handScenarios = decisions.Select(d => d.Base).ToHashSet(StringComparer.Ordinal);
                    var firstDecision = decisions.FirstOrDefault();

                    ExactHandItem Item(string street, HeroAction action, PreflopDecision? decision, bool isFirst) =>
                        new(street, handCode, table.TableName, table.SourcePath, tableHandNumber, position, table.GameFormat,
                            table.IsCash, table.LastPlayedAt.Date, handDateTime, exactCards, spot, action, netBb, cumulativeBb,
                            decision, isFirst, handScenarios);

                    foreach (var street in new[] { "PREFLOP", "FLOP", "TURN", "RIVER" })
                    {
                        var action = DetectHeroAction(hand, table.HeroName, street);
                        if (action is null)
                            continue;

                        _allExamples.Add(Item(street, action.Value, firstDecision, isFirst: true));
                    }

                    // Las decisiones siguientes del preflop (por ejemplo, contra el 3bet despues de abrir)
                    // solo cuentan al elegir esa situacion: en "Todas" cada mano cuenta una vez.
                    foreach (var later in decisions.Skip(1))
                        _allExamples.Add(Item("PREFLOP", ActionFromDecision(later), later, isFirst: false));
                }
            }
        }

        private void PopulateFilterOptions()
        {
            // Situaciones que hay en las manos, en el orden de las tablas preflop.
            var order = PreflopScenarios.SixMax.ToList();
            ConfigureOptionCombo(SituationFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_allExamples.Where(example => example.Street == "PREFLOP" && example.Decision is not null)
                    .Select(example => example.Decision!.Base)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(scenario => order.IndexOf(scenario) is var index && index >= 0 ? index : 99)
                    .Select(scenario => new LocalizedOption(scenario, PreflopScenarios.Label(scenario))))
                .ToList());
            SituationFilter.SelectedIndex = 0;

            ConfigureOptionCombo(PositionFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_allExamples.Select(example => example.Position)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(PositionSort)
                    .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());
            PositionFilter.SelectedIndex = 0;

            ConfigureOptionCombo(FormatFilter, new[] { LocalizedOption.Key("ALL", "Common.AllMale") }
                .Concat(_allExamples.Select(example => example.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());
            FormatFilter.SelectedIndex = 0;

            ConfigureOptionCombo(MoneyTypeFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.AllMale"),
                LocalizedOption.Raw("Cash"),
                LocalizedOption.Key("CHIPS", "Common.Chips")
            });
            MoneyTypeFilter.SelectedIndex = 0;
        }

        private void RebuildFilteredCells()
        {
            _cells.Clear();
            foreach (var example in FilterExamples(_allExamples))
            {
                var key = CellKey(example.Street, example.HandCode);
                if (!_cells.TryGetValue(key, out var cell))
                {
                    cell = new HandCellStats(example.Street, example.HandCode);
                    _cells[key] = cell;
                }

                cell.Add(example.Action, example.NetBb, example);
            }
        }

        private IEnumerable<ExactHandItem> FilterExamples(IEnumerable<ExactHandItem> examples)
        {
            var situation = SelectedFilter(SituationFilter);
            var position = SelectedFilter(PositionFilter);
            var format = SelectedFilter(FormatFilter);
            var moneyType = SelectedFilter(MoneyTypeFilter);
            var from = FromDateFilter.SelectedDate?.Date;
            var to = ToDateFilter.SelectedDate?.Date;

            return examples.Where(example =>
                // Sin situacion elegida, cada mano cuenta una vez (su primera decision preflop). Con una
                // situacion: en el preflop, las decisiones en esa situacion; despues del flop, las manos
                // que pasaron por ella.
                (situation is null
                    ? example.Street != "PREFLOP" || example.IsFirstDecision
                    : example.Street == "PREFLOP"
                        ? example.Decision?.Base == situation
                        : example.HandScenarios.Contains(situation)) &&
                (position is null || string.Equals(example.Position, position, StringComparison.OrdinalIgnoreCase)) &&
                (format is null || string.Equals(example.GameFormat, format, StringComparison.OrdinalIgnoreCase)) &&
                (moneyType is null ||
                    (moneyType == "Cash" && example.IsCash) ||
                    (moneyType == "CHIPS" && !example.IsCash)) &&
                (from is null || example.PlayedAt >= from.Value) &&
                (to is null || example.PlayedAt <= to.Value));
        }

        private static string? SelectedFilter(ComboBox comboBox)
        {
            var value = comboBox.SelectedValue as string;
            return string.IsNullOrWhiteSpace(value) || value == "ALL"
                ? null
                : value;
        }

        private static void ConfigureOptionCombo(ComboBox comboBox, IEnumerable<LocalizedOption> options)
        {
            comboBox.DisplayMemberPath = nameof(LocalizedOption.Label);
            comboBox.SelectedValuePath = nameof(LocalizedOption.Value);
            comboBox.ItemsSource = options.ToList();
        }

        private static int PositionSort(string position) =>
            position switch
            {
                "BTN" => 0,
                "BTN/SB" => 1,
                "SB" => 2,
                "BB" => 3,
                "UTG" => 4,
                "MP" => 5,
                "HJ" => 6,
                "CO" => 7,
                "?" => 99,
                _ => 50
            };

        private void RenderGrid()
        {
            UpdateStreetButtons();
            BuildLegend();
            ExactHandsGrid.ItemsSource = null;
            _exactActionFilter = null;
            _exactResultFilter = "ALL";
            RefreshExactFilterChips();

            HandsGrid.Children.Clear();
            _selectionMarks.Clear();
            for (var row = 0; row < Ranks.Length; row++)
            {
                for (var col = 0; col < Ranks.Length; col++)
                {
                    var handCode = HandCode(row, col);
                    _cells.TryGetValue(CellKey(_street, handCode), out var stats);
                    HandsGrid.Children.Add(CreateCell(handCode, stats));
                }
            }

            ShowStreetSummary();
        }

        private bool ShowChartMarks => _compareChart && _street == "PREFLOP";

        /// <summary>
        /// Una casilla: el color de lo que mas hiciste (o de la ganancia media), mas tenue si hay pocas
        /// manos; abajo, una barra con la mezcla de acciones; arriba a la izquierda, cuantas manos; y
        /// una esquina si jugaste distinto a la tabla de APH.
        /// </summary>
        private Button CreateCell(string handCode, HandCellStats? stats)
        {
            var root = new Grid { ClipToBounds = true };
            var total = stats?.Total ?? 0;
            Brush ink;

            if (stats is null || total == 0)
            {
                var empty = new Border();
                empty.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
                root.Children.Add(empty);
                ink = Hud.App.Services.ThemeBrushes.TextDim;
            }
            else
            {
                var color = _colorByProfit ? ColorForBb(stats.AverageBb) : ColorForAction(stats.MainAction);
                var strength = SampleStrength(total);
                var under = Hud.App.Services.ThemeBrushes.Color("Col.Panel", Color.FromRgb(17, 24, 32));
                var under2 = new Border();
                under2.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");
                root.Children.Add(under2);
                root.Children.Add(new Border { Background = Frozen(color), Opacity = strength });
                ink = InkFor(Blend(under, color, strength));

                // La mezcla de acciones (siempre con los colores de accion).
                if (stats.Actions.Count > 1 || _colorByProfit)
                {
                    var bar = new Grid();
                    foreach (var action in ActionDisplayOrder.Where(stats.Actions.ContainsKey))
                    {
                        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stats.Actions[action].Count, GridUnitType.Star) });
                        var segment = new Border { Background = Frozen(ColorForAction(action)), Margin = new Thickness(0, 0, 1, 0) };
                        Grid.SetColumn(segment, bar.ColumnDefinitions.Count - 1);
                        bar.Children.Add(segment);
                    }
                    var barBox = new Border
                    {
                        Height = 7,
                        Padding = new Thickness(1, 1, 0, 1),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(4, 0, 4, 3),
                        Child = bar
                    };
                    barBox.SetResourceReference(Border.BackgroundProperty, "Brush.Bg");
                    root.Children.Add(barBox);
                }

                root.Children.Add(new TextBlock
                {
                    Text = total.ToString(CultureInfo.CurrentCulture),
                    Foreground = ink,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 1, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                });

                if (ShowChartMarks && stats.ChartDiff > 0)
                {
                    var corner = new System.Windows.Shapes.Polygon
                    {
                        Points = new PointCollection { new(0, 0), new(14, 0), new(14, 14) },
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        StrokeThickness = 1
                    };
                    corner.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Negative");
                    corner.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Bg");
                    root.Children.Add(corner);
                }
            }

            root.Children.Add(new TextBlock
            {
                Text = handCode,
                Foreground = ink,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            });

            // Marca de la casilla elegida.
            var mark = new Border { BorderThickness = new Thickness(3), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            mark.SetResourceReference(Border.BorderBrushProperty, "Brush.Text");
            root.Children.Add(mark);
            _selectionMarks[handCode] = mark;

            var button = new Button
            {
                Content = root,
                Style = (Style)FindResource("MatrixCellButton"),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Tag = stats ?? new HandCellStats(_street, handCode),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                ToolTip = CellToolTip(handCode, stats)
            };
            // Para el control por voz: "clic AKo".
            SetName(button, handCode);

            button.Click += Cell_Click;
            return button;
        }

        private string CellToolTip(string handCode, HandCellStats? stats)
        {
            if (stats is null || stats.Total == 0)
                return $"{handCode} | {LocalizationManager.Text("Common.NoSampleLower")}";

            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, T("Mine.Tip.Hands"), handCode, HandsText(stats.Total), stats.AverageBb),
                string.Join(" · ", ActionDisplayOrder.Where(stats.Actions.ContainsKey)
                    .Select(action => $"{LabelForAction(action)} {100.0 * stats.Actions[action].Count / stats.Total:0}%"))
            };
            if (stats.Total < FullSample)
                lines.Add(T("Mine.Tip.Few"));
            if (ShowChartMarks && stats.ChartChecked > 0)
                lines.Add(ChartLine(stats));
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>"Tabla de APH: subir (abrir). Jugaste distinto 2 de 3 veces."</summary>
        private static string ChartLine(HandCellStats stats)
        {
            var advice = stats.ChartAdvice.Count == 1 ? stats.ChartAdvice.First() : T("Mine.Tip.ChartDepends");
            return stats.ChartDiff > 0
                ? string.Format(CultureInfo.CurrentCulture, T("Mine.Tip.ChartDiff"), advice, stats.ChartDiff, stats.ChartChecked)
                : string.Format(CultureInfo.CurrentCulture, T("Mine.Tip.ChartSame"), advice, stats.ChartChecked);
        }

        /// <summary>1 mano: 40%; 2: 55%; 3: 70%; 4: 85%; 5 o mas: entera.</summary>
        private static double SampleStrength(int hands) =>
            hands >= FullSample ? 1.0 : 0.4 + 0.15 * (hands - 1);

        // ---------------------------------------------------------------- leyenda

        private void BuildLegend()
        {
            LegendPanel.Children.Clear();
            var actions = _street == "PREFLOP"
                ? new[] { HeroAction.Fold, HeroAction.Check, HeroAction.Call, HeroAction.Raise, HeroAction.AllIn }
                : new[] { HeroAction.Fold, HeroAction.Check, HeroAction.Call, HeroAction.Bet, HeroAction.Raise, HeroAction.AllIn };

            if (_colorByProfit)
            {
                LegendPanel.Children.Add(LegendTitle(T("Mine.Legend.ProfitTitle")));
                var scale = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 6) };
                scale.Children.Add(LegendText(T("Mine.Legend.Loses")));
                foreach (var bb in new[] { -60.0, -40, -20, -5, 0, 5, 20, 40, 60 })
                    scale.Children.Add(new Border { Width = 16, Height = 14, Background = Frozen(ColorForBb(bb)), Margin = new Thickness(1, 0, 0, 0) });
                scale.Children.Add(LegendText(" " + T("Mine.Legend.Wins")));
                LegendPanel.Children.Add(scale);
                LegendPanel.Children.Add(LegendTitle(T("Mine.Legend.BarTitle")));
            }
            else
            {
                LegendPanel.Children.Add(LegendTitle(T("Mine.Legend.ActionTitle")));
            }

            foreach (var action in actions)
                LegendPanel.Children.Add(LegendItem(Swatch(ColorForAction(action)), action == HeroAction.Raise ? T("Mine.Legend.Raise") : LabelForAction(action)));

            if (!_colorByProfit)
                LegendPanel.Children.Add(LegendItem(BarSample(), T("Mine.Legend.Bar")));
            LegendPanel.Children.Add(LegendItem(FaintSample(), string.Format(CultureInfo.CurrentCulture, T("Mine.Legend.Faint"), FullSample)));
            if (ShowChartMarks)
                LegendPanel.Children.Add(LegendItem(CornerSample(), T("Mine.Legend.Chart")));
        }

        private static TextBlock LegendTitle(string text) => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 6)
        };

        private static TextBlock LegendText(string text)
        {
            var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            return block;
        }

        private static FrameworkElement LegendItem(FrameworkElement sample, string text)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6), VerticalAlignment = VerticalAlignment.Center };
            sample.Margin = new Thickness(0, 0, 5, 0);
            sample.VerticalAlignment = VerticalAlignment.Center;
            item.Children.Add(sample);
            item.Children.Add(LegendText(text));
            return item;
        }

        private static Border Swatch(Color color) => new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = Frozen(color) };

        private static FrameworkElement BarSample()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Height = 7 };
            foreach (var action in new[] { HeroAction.Fold, HeroAction.Call, HeroAction.Raise })
                bar.Children.Add(new Border { Width = 8, Background = Frozen(ColorForAction(action)), Margin = new Thickness(0, 0, 1, 0) });
            return bar;
        }

        private static FrameworkElement FaintSample() =>
            new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = Frozen(ColorForAction(HeroAction.Call)), Opacity = 0.45 };

        private static FrameworkElement CornerSample()
        {
            var corner = new System.Windows.Shapes.Polygon { Points = new PointCollection { new(0, 0), new(14, 0), new(14, 14) } };
            corner.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Negative");
            return corner;
        }

        // ---------------------------------------------------------------- resumen y casilla elegida

        /// <summary>Sin casilla elegida: un resumen de la calle con los filtros puestos.</summary>
        private void ShowStreetSummary()
        {
            _selectedCell = null;
            UpdateSelectionMarks();
            ExactHandsPanel.Visibility = Visibility.Collapsed;
            InsightPanel.Children.Clear();
            SelectedHandText.Text = string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Title"), StreetLabel(_street));
            SelectedStreetText.Text = T("Mine.Sum.Hint");

            var cells = _cells.Values.Where(cell => cell.Street == _street && cell.Total > 0).ToList();
            if (cells.Count == 0)
            {
                ActionSummaryList.ItemsSource = null;
                AddInsight(T("Mine.Sum.NoHands"));
                return;
            }

            var all = new HandCellStats(_street, "");
            foreach (var example in cells.SelectMany(cell => cell.Examples))
                all.Add(example.Action, example.NetBb, example);
            ActionSummaryList.ItemsSource = all.SummaryItems(_colorByProfit);

            AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Hands"), HandsText(all.Total), all.AverageBb));
            if (_street == "PREFLOP")
            {
                var played = all.Examples.Count(example => example.Action != HeroAction.Fold);
                AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Played"), 100.0 * played / all.Total));
                if (ShowChartMarks && all.ChartChecked > 0)
                {
                    var same = all.ChartChecked - all.ChartDiff;
                    AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Chart"), same, all.ChartChecked, 100.0 * same / all.ChartChecked), bold: true);
                    var different = cells.Where(cell => cell.ChartDiff > 0)
                        .OrderByDescending(cell => cell.ChartDiff).ThenBy(cell => cell.HandCode, StringComparer.Ordinal)
                        .Take(6).Select(cell => cell.HandCode).ToList();
                    if (different.Count > 0)
                        AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.ChartWorst"), string.Join(", ", different)));
                }
            }

            string Top(IEnumerable<HandCellStats> source) =>
                string.Join(", ", source.Select(cell => $"{cell.HandCode} ({cell.TotalBb:+0.#;-0.#;0} bb)"));
            var losers = cells.Where(cell => cell.TotalBb < -0.05).OrderBy(cell => cell.TotalBb).Take(3).ToList();
            var winners = cells.Where(cell => cell.TotalBb > 0.05).OrderByDescending(cell => cell.TotalBb).Take(3).ToList();
            if (losers.Count > 0)
                AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Losers"), Top(losers)));
            if (winners.Count > 0)
                AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Winners"), Top(winners)));
        }

        private void AddInsight(string text, bool bold = false) =>
            InsightPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });

        private void UpdateSelectionMarks()
        {
            foreach (var (hand, mark) in _selectionMarks)
                mark.Visibility = _selectedCell is not null && _selectedCell.HandCode == hand ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HandCellStats stats)
                return;

            // Tocar otra vez la casilla elegida vuelve al resumen de la calle.
            if (_selectedCell is not null && _selectedCell.HandCode == stats.HandCode)
            {
                ShowStreetSummary();
                return;
            }

            SelectedHandText.Text = stats.HandCode;
            SelectedStreetText.Text = StreetLabel(stats.Street) + " · " + T("Mine.Sum.Back");
            InsightPanel.Children.Clear();
            if (stats.Total > 0)
                AddInsight(string.Format(CultureInfo.CurrentCulture, T("Mine.Sum.Hands"), HandsText(stats.Total), stats.AverageBb));
            if (stats.Total is > 0 and < FullSample)
                AddInsight(T("Mine.Tip.Few"));
            if (ShowChartMarks && stats.ChartChecked > 0)
                AddInsight(ChartLine(stats), bold: true);
            ActionSummaryList.ItemsSource = stats.SummaryItems(_colorByProfit);
            _selectedCell = stats;
            UpdateSelectionMarks();
            _exactActionFilter = null;
            _exactResultFilter = "ALL";
            RefreshExactFilterChips();

            if (stats.Examples.Count == 0)
            {
                ExactHandsPanel.Visibility = Visibility.Collapsed;
                ExactHandsGrid.ItemsSource = null;
                return;
            }

            ExactHandsTitle.Text = $"{stats.HandCode} | {StreetLabel(stats.Street)} | {Hud.App.Services.LocalizationManager.Text("Common.ExactHands")}";
            ApplyExactHandsFilter();
            ExactHandsPanel.Visibility = Visibility.Visible;
        }

        private void ExactActionFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag)
                return;

            _exactActionFilter = tag == "ALL" ? null : ActionFromLabel(tag);
            RefreshExactFilterChips();
            ApplyExactHandsFilter();
        }

        private void ExactResultFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag)
                return;

            _exactResultFilter = tag;
            RefreshExactFilterChips();
            ApplyExactHandsFilter();
        }

        private void ApplyExactHandsFilter()
        {
            if (_selectedCell is null)
            {
                ExactHandsGrid.ItemsSource = null;
                return;
            }

            ExactHandsGrid.ItemsSource = _selectedCell.Examples
                .Where(example => _exactActionFilter is null || example.Action == _exactActionFilter.Value)
                .Where(example => _exactResultFilter switch
                {
                    "WIN" => example.NetBb > 0,
                    "LOSS" => example.NetBb < 0,
                    _ => true
                })
                .OrderBy(example => example.TableName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(example => example.HandNumber)
                .ToList();
        }

        private void RefreshExactFilterChips()
        {
            // Con los colores del tema (antes azul claro fijo): elegido = acento.
            void SetChip(Button button, bool selected)
            {
                button.SetResourceReference(BackgroundProperty, selected ? "Brush.Accent" : "Brush.Inset");
                button.SetResourceReference(ForegroundProperty, selected ? "Brush.Bg" : "Brush.Text");
                button.SetResourceReference(BorderBrushProperty, selected ? "Brush.Accent" : "Brush.Border");
            }

            SetChip(ActionAllChip, _exactActionFilter is null);
            SetChip(ActionFoldChip, _exactActionFilter == HeroAction.Fold);
            SetChip(ActionCheckChip, _exactActionFilter == HeroAction.Check);
            SetChip(ActionCallChip, _exactActionFilter == HeroAction.Call);
            SetChip(ActionBetChip, _exactActionFilter == HeroAction.Bet);
            SetChip(ActionRaiseChip, _exactActionFilter == HeroAction.Raise);
            SetChip(ActionThreeBetChip, _exactActionFilter == HeroAction.ThreeBet);
            SetChip(ActionFourBetChip, _exactActionFilter == HeroAction.FourBetPlus);
            SetChip(ActionAllInChip, _exactActionFilter == HeroAction.AllIn);

            SetChip(ResultAllChip, _exactResultFilter == "ALL");
            SetChip(ResultWinChip, _exactResultFilter == "WIN");
            SetChip(ResultLossChip, _exactResultFilter == "LOSS");
        }

        private void ExactHandsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ExactHandsGrid.SelectedItem is not ExactHandItem item)
                return;

            var table = _tables.FirstOrDefault(t =>
                string.Equals(t.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                return;

            var window = new TableDetailWindow(table, item.HandNumber)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            AppNavigator.Open(window);
        }

        private static IEnumerable<IReadOnlyList<string>> SplitHands(IEnumerable<string> lines)
        {
            return PokerStarsHandHistory.SplitHands(lines);
        }

        private static bool TryGetHeroHandInfo(
            IReadOnlyList<string> hand,
            string heroName,
            out string handCode,
            out string exactCards)
        {
            foreach (var line in hand)
            {
                var match = DealtRx.Match(line);
                if (!match.Success)
                    continue;

                if (!PokerStarsHandHistory.SamePlayer(match.Groups["hero"].Value, heroName))
                    continue;

                var cards = match.Groups["cards"].Value.Trim();
                handCode = NormalizeHand(cards);
                exactCards = FormatCards(cards);
                return true;
            }

            handCode = "";
            exactCards = "";
            return false;
        }

        private static string FormatCards(string cards) =>
            string.Join(" ", cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(FormatCardForDisplay));

        private static string ExtractBoardSpot(IReadOnlyList<string> hand)
        {
            var board = new List<string>();
            foreach (var line in hand)
            {
                if (!line.StartsWith("*** FLOP ***", StringComparison.Ordinal) &&
                    !line.StartsWith("*** TURN ***", StringComparison.Ordinal) &&
                    !line.StartsWith("*** RIVER ***", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in BoardCardsRx.Matches(line))
                {
                    foreach (var rawCard in match.Groups["cards"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var card = FormatCardForDisplay(rawCard);
                        if (!board.Contains(card, StringComparer.Ordinal))
                            board.Add(card);
                    }
                }
            }

            return board.Count == 0 ? "-" : string.Join(" - ", board);
        }

        private static DateTime? ExtractHandDateTime(IReadOnlyList<string> hand)
        {
            return PokerStarsHandHistory.ExtractTimestamp(hand);
        }

        private static Dictionary<string, string> BuildPositionMap(IReadOnlyList<string> hand)
        {
            return PokerStarsHandHistory.BuildPositionMap(hand);
        }

        private static string FormatCardForDisplay(string card)
        {
            if (card.Length < 2)
                return card;

            var rank = card[..^1].ToUpperInvariant();
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

        private static HeroAction? DetectHeroAction(IReadOnlyList<string> hand, string heroName, string street)
        {
            var lines = GetStreetLines(hand, street).ToList();
            var raisesBeforeHero = 0;

            foreach (var line in lines)
            {
                var match = ActorRx.Match(line);
                if (!match.Success)
                    continue;

                var actor = PokerStarsHandHistory.NormalizeName(match.Groups["actor"].Value);
                var actionText = match.Groups["action"].Value.Trim();
                var normalizedAction = PokerStarsHandHistory.NormalizeAction(actionText);
                var isRaise = normalizedAction == "raises";

                if (PokerStarsHandHistory.SamePlayer(actor, heroName))
                {
                    if (actionText.Contains("all-in", StringComparison.OrdinalIgnoreCase))
                        return HeroAction.AllIn;
                    if (normalizedAction == "folds")
                        return HeroAction.Fold;
                    if (normalizedAction == "checks")
                        return HeroAction.Check;
                    if (normalizedAction == "calls")
                        return HeroAction.Call;
                    if (normalizedAction == "bets")
                        return HeroAction.Bet;
                    if (isRaise)
                    {
                        if (street == "PREFLOP")
                            return raisesBeforeHero switch
                            {
                                0 => HeroAction.Raise,
                                1 => HeroAction.ThreeBet,
                                _ => HeroAction.FourBetPlus
                            };

                        return HeroAction.Raise;
                    }
                }

                if (isRaise)
                    raisesBeforeHero++;
            }

            return null;
        }

        private static IEnumerable<string> GetStreetLines(IReadOnlyList<string> hand, string street)
        {
            var start = street == "PREFLOP" ? 0 : FindStreetIndex(hand, street);
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
            for (var i = startAt; i < hand.Count; i++)
            {
                var line = hand[i];
                if (street == "SHOW")
                {
                    var showIndex = PokerStarsHandHistory.FindStreetIndex(hand, street, startAt);
                    if (showIndex >= 0)
                        return showIndex;
                }
                if (line.StartsWith($"*** {street} ***", StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static double EstimateHeroNetForHand(IReadOnlyList<string> hand, string heroName)
        {
            return PokerStarsHandHistory.EstimateNetForPlayer(hand, heroName);
        }

        private static bool TryParseAmount(string raw, out double value)
        {
            if (Hud.App.Services.PokerAmountParser.TryParse(raw, out value))
                return true;

            var clean = raw.Replace("$", "", StringComparison.Ordinal)
                .Replace("US", "", StringComparison.OrdinalIgnoreCase)
                .Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (clean.Contains(',') && clean.Contains('.'))
                clean = clean.Replace(",", "", StringComparison.Ordinal);
            else
                clean = clean.Replace(',', '.');

            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string NormalizeHand(string cards)
        {
            var parts = cards.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "--";

            var first = ParseCard(parts[0]);
            var second = ParseCard(parts[1]);
            if (first.rank == second.rank)
                return $"{first.rank}{second.rank}";

            var firstIndex = Array.IndexOf(Ranks, first.rank);
            var secondIndex = Array.IndexOf(Ranks, second.rank);
            var high = firstIndex < secondIndex ? first : second;
            var low = firstIndex < secondIndex ? second : first;
            return $"{high.rank}{low.rank}{(high.suit == low.suit ? "s" : "o")}";
        }

        private static (string rank, char suit) ParseCard(string raw)
        {
            var card = raw.Trim();
            if (card.Length < 2)
                return (card, '?');

            return (card[..^1].ToUpperInvariant(), char.ToLowerInvariant(card[^1]));
        }

        private static string HandCode(int row, int col)
        {
            if (row == col)
                return $"{Ranks[row]}{Ranks[col]}";
            if (row < col)
                return $"{Ranks[row]}{Ranks[col]}s";
            return $"{Ranks[col]}{Ranks[row]}o";
        }

        private static string CellKey(string street, string handCode) => $"{street}:{handCode}";

        private static string StreetLabel(string street) => street switch
        {
            "PREFLOP" => "PRE-FLOP",
            "FLOP" => "FLOP",
            "TURN" => "TURN",
            "RIVER" => "RIVER",
            _ => street
        };

        /// <summary>Colores de cada accion (con significado: no cambian con el tema, como los de las estadisticas).</summary>
        private static Color ColorForAction(HeroAction action) => action switch
        {
            HeroAction.Fold => Color.FromRgb(96, 105, 101),
            HeroAction.Check => Color.FromRgb(104, 130, 146),
            HeroAction.Call => Color.FromRgb(70, 145, 205),
            HeroAction.Bet => Color.FromRgb(72, 170, 100),
            HeroAction.Raise or HeroAction.ThreeBet or HeroAction.FourBetPlus => Color.FromRgb(206, 160, 52),
            HeroAction.AllIn => Color.FromRgb(212, 86, 98),
            _ => Colors.Transparent
        };

        private static Brush BrushForAction(HeroAction action) => Frozen(ColorForAction(action));

        private static Color ColorForBb(double bb)
        {
            var abs = Math.Abs(bb);
            if (abs < 0.05)
                return Color.FromRgb(96, 105, 101);

            if (bb > 0)
            {
                if (abs < 10)
                    return Color.FromRgb(58, 128, 104);
                if (abs < 30)
                    return Color.FromRgb(64, 158, 98);
                if (abs < 50)
                    return Color.FromRgb(96, 186, 112);
                return Color.FromRgb(206, 170, 60);
            }

            if (abs < 10)
                return Color.FromRgb(150, 92, 98);
            if (abs < 30)
                return Color.FromRgb(186, 82, 92);
            if (abs < 50)
                return Color.FromRgb(214, 92, 104);
            return Color.FromRgb(232, 112, 124);
        }

        private static Brush BrushForBb(double bb) => Frozen(ColorForBb(bb));

        /// <summary>Las acciones en el idioma de APH (antes salian en ingles).</summary>
        private static string LabelForAction(HeroAction action) => action switch
        {
            HeroAction.Fold => T("Mine.Act.Fold"),
            HeroAction.Check => T("Mine.Act.Check"),
            HeroAction.Call => T("Mine.Act.Call"),
            HeroAction.Bet => T("Mine.Act.Bet"),
            HeroAction.Raise => T("Mine.Act.Raise"),
            HeroAction.ThreeBet => "3bet",
            HeroAction.FourBetPlus => "4bet+",
            HeroAction.AllIn => "All-in",
            _ => action.ToString()
        };

        /// <summary>Decisiones preflop del heroe en esta mano, con su situacion y lo que dice la tabla de APH.</summary>
        private static List<PreflopDecision> ReadPreflopDecisions(IReadOnlyList<string> hand, string heroName, AphPreflopProvider? charts)
        {
            var list = new List<PreflopDecision>();
            try
            {
                foreach (var spot in PreflopSpotReader.ReadHand(hand))
                {
                    if (!PokerStarsHandHistory.SamePlayer(spot.Player, heroName))
                        break;
                    string? chart = null;
                    try
                    {
                        chart = charts?.GetCode(spot.Scenario, spot.Hero, spot.Villain, spot.Hand);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Mis Tablas: no se pudo consultar la tabla de APH", ex);
                    }
                    list.Add(new PreflopDecision(spot.Scenario, PreflopScenarios.BaseOf(spot.Scenario), spot.Hero, spot.Villain, spot.Action, chart));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Mis Tablas: no se pudo leer la situacion preflop de una mano", ex);
            }
            return list;
        }

        private static HeroAction ActionFromDecision(PreflopDecision decision) => decision.HeroCode switch
        {
            "F" => HeroAction.Fold,
            "C" => HeroAction.Call,
            _ => decision.Base == PreflopScenarios.VsRaise ? HeroAction.ThreeBet
                : decision.Base is PreflopScenarios.Vs3Bet or PreflopScenarios.Vs4Bet or PreflopScenarios.Vs5Bet ? HeroAction.FourBetPlus
                : HeroAction.Raise
        };

        private static string T(string key) => LocalizationManager.Text(key);

        /// <summary>"1 mano" o "N manos".</summary>
        private static string HandsText(int count) =>
            count == 1 ? T("Mine.OneHand") : string.Format(CultureInfo.CurrentCulture, T("Common.HandCount"), count);

        private static void SetName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Color Blend(Color under, Color over, double amount) => Color.FromRgb(
            (byte)Math.Round(under.R + (over.R - under.R) * amount),
            (byte)Math.Round(under.G + (over.G - under.G) * amount),
            (byte)Math.Round(under.B + (over.B - under.B) * amount));

        /// <summary>Texto negro o blanco, el que mas contraste tenga con el fondo (formula WCAG).</summary>
        private static Brush InkFor(Color background)
        {
            static double Channel(byte value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            var luminance = 0.2126 * Channel(background.R) + 0.7152 * Channel(background.G) + 0.0722 * Channel(background.B);
            var vsBlack = (luminance + 0.05) / 0.05;
            var vsWhite = 1.05 / (luminance + 0.05);
            return vsBlack >= vsWhite ? Frozen(Color.FromRgb(17, 17, 17)) : Brushes.White;
        }

        private static HeroAction? ActionFromLabel(string label) => label switch
        {
            "Fold" => HeroAction.Fold,
            "Check" => HeroAction.Check,
            "Call" => HeroAction.Call,
            "Bet" => HeroAction.Bet,
            "Raise" => HeroAction.Raise,
            "3Bet" => HeroAction.ThreeBet,
            "4Bet+" => HeroAction.FourBetPlus,
            "All-in" => HeroAction.AllIn,
            _ => null
        };

        private enum HeroAction
        {
            Fold,
            Check,
            Call,
            Bet,
            Raise,
            ThreeBet,
            FourBetPlus,
            AllIn
        }

        private sealed class HandCellStats
        {
            public HandCellStats(string street, string handCode)
            {
                Street = street;
                HandCode = handCode;
            }

            public string Street { get; }
            public string HandCode { get; }
            public Dictionary<HeroAction, ActionStats> Actions { get; } = new();
            public List<ExactHandItem> Examples { get; } = new();
            public int Total => Actions.Values.Sum(action => action.Count);
            public double TotalBb => Actions.Values.Sum(action => action.TotalBb);
            public double AverageBb => Total == 0 ? 0 : TotalBb / Total;

            /// <summary>Lo que mas hiciste con esta mano (en empate, la accion mas agresiva).</summary>
            public HeroAction MainAction => Actions
                .OrderByDescending(pair => pair.Value.Count)
                .ThenByDescending(pair => Array.IndexOf(ActionDisplayOrder, pair.Key))
                .First().Key;

            /// <summary>Decisiones preflop que tienen tabla de APH, cuantas fueron distintas y que dice la tabla.</summary>
            public int ChartChecked { get; private set; }
            public int ChartDiff { get; private set; }
            public HashSet<string> ChartAdvice { get; } = new(StringComparer.Ordinal);

            public void Add(HeroAction action, double netBb, ExactHandItem example)
            {
                if (!Actions.TryGetValue(action, out var stats))
                {
                    stats = new ActionStats();
                    Actions[action] = stats;
                }

                stats.Count++;
                stats.TotalBb += netBb;
                Examples.Add(example);

                if (example.Street == "PREFLOP" && example.Decision is { ChartCode: not null } decision)
                {
                    ChartChecked++;
                    if (decision.HeroCode != decision.ChartCode)
                        ChartDiff++;
                    ChartAdvice.Add(decision.ChartAction!.ToLower(CultureInfo.CurrentCulture));
                }
            }

            public IReadOnlyList<ActionSummaryItem> SummaryItems(bool colorByProfit)
            {
                if (Total == 0)
                {
                    return new[]
                    {
                        new ActionSummaryItem(LocalizationManager.Text("Tag.NoSample"), string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Common.HandCount"), 0), Hud.App.Services.ThemeBrushes.NeutralBackground)
                    };
                }

                return ActionDisplayOrder
                    .Where(Actions.ContainsKey)
                    .Select(action =>
                    {
                        var actionStats = Actions[action];
                        var pct = actionStats.Count * 100.0 / Total;
                        var avg = actionStats.TotalBb / actionStats.Count;
                        return new ActionSummaryItem(
                            $"{LabelForAction(action)} {pct:0.#}%",
                            actionStats.Count == 1
                                ? string.Format(CultureInfo.CurrentCulture, T("Mine.OneHandBb"), avg)
                                : string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Common.HandCountBbAverage"), actionStats.Count, avg),
                            colorByProfit ? BrushForBb(avg) : BrushForAction(action));
                    })
                    .ToList();
            }
        }

        private sealed class ActionStats
        {
            public int Count { get; set; }
            public double TotalBb { get; set; }
            public double AverageBb => Count == 0 ? 0 : TotalBb / Count;
        }

        private sealed record ActionSummaryItem(string Label, string Detail, Brush Color);

        /// <summary>
        /// Una decision preflop: situacion (por ejemplo "HU VS RAISE"), su base ("VS RAISE"), posiciones
        /// con los nombres de las tablas, lo que hizo el heroe (R/C/F) y lo que dice la tabla (o null).
        /// </summary>
        private sealed record PreflopDecision(string Scenario, string Base, string Hero, string Villain, string HeroCode, string? ChartCode, bool HeroChecked = false)
        {
            /// <summary>Lo que dice la tabla, con "Pasar (check)" cuando se podia pasar gratis.</summary>
            public string? ChartAction => ChartCode is null ? null
                : HeroChecked && ChartCode == "C" ? T("Preflop.Action.Check")
                : PreflopScenarios.ActionLabel(Scenario, Hero, ChartCode);
        }

        private sealed record ExactHandItem(
            string Street,
            string HandCode,
            string TableName,
            string SourcePath,
            int HandNumber,
            string Position,
            string GameFormat,
            bool IsCash,
            DateTime PlayedAt,
            DateTime HandDateTime,
            string ExactCards,
            string Spot,
            HeroAction Action,
            double NetBb,
            double CumulativeBb,
            PreflopDecision? Decision,
            bool IsFirstDecision,
            IReadOnlySet<string> HandScenarios)
        {
            public string HandDateTimeLabel => HandDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            public string ActionLabel => LabelForAction(Action);

            /// <summary>"Contra subida · BTN": la situacion de esta decision (despues del flop, la del preflop de la mano).</summary>
            public string SituationLabel => Decision is null
                ? "—"
                : PreflopScenarios.Label(Decision.Scenario)
                  + (PreflopScenarios.IsHeadsUp(Decision.Scenario) ? " HU" : "")
                  + (string.IsNullOrEmpty(Decision.Villain) ? "" : " · " + PreflopScenarios.PositionLabel(Decision.Villain, Decision.Scenario));

            /// <summary>"✓ Subir (abrir)" si jugaste como la tabla; "✗ Retirarse" si la tabla decia otra cosa.</summary>
            public string ChartLabel => Street != "PREFLOP" || Decision?.ChartCode is null
                ? "—"
                : (Decision.HeroCode == Decision.ChartCode ? "✓ " : "✗ ") + Decision.ChartAction;
            public string NetBbLabel => $"{NetBb:+0.#;-0.#;0} bb";
            public string CumulativeBbLabel => $"{CumulativeBb:+0.#;-0.#;0} bb";
            public string NetTrendIcon => NetBb >= 0 ? "\u25B2" : "\u25BC";
            public string CumulativeTrendIcon => CumulativeBb >= 0 ? "\u25B2" : "\u25BC";
            public IReadOnlyList<CardChipViewModel> ExactCardChips => CardChipViewModel.FromCards(ExactCards);
            public IReadOnlyList<CardChipViewModel> SpotCardChips => CardChipViewModel.FromCards(Spot);
            public Brush NetTrendBrush => NetBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
            public Brush CumulativeTrendBrush => CumulativeBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));
        }
    }
}

