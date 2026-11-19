using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hud.App.Services;
using Hud.App.Services.Solver;
using S = Hud.App.Services.PreflopScenarios;

namespace Hud.App.Views
{
    /// <summary>
    /// Solver de all-in o retirarse en heads-up: con cuantas manos hacer all-in desde el boton y
    /// con cuantas pagarlo desde la ciega grande, segun las ciegas que queden. Es el equilibrio de
    /// Nash calculado por APH (PushFoldSolver), no una tabla copiada.
    ///
    /// Dos vistas: "con estas ciegas" (que hacer con cada mano con la pila elegida) y "hasta
    /// cuantas ciegas" (la tabla clasica: el numero de cada casilla es la pila mas alta con la que
    /// se juega esa mano). Como las tablas preflop, es una consulta: no lee la mesa.
    /// </summary>
    public partial class PushFoldWindow : Window
    {
        private static readonly double[] QuickStacks = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15, 20, 25 };
        private const double AnteSize = 0.1;

        private readonly PushFoldSolver? _solver = PushFoldSolver.Default;
        private readonly Border[,] _cells = new Border[13, 13];
        private readonly TextBlock[,] _handTexts = new TextBlock[13, 13];
        private readonly TextBlock[,] _valueTexts = new TextBlock[13, 13];

        private bool _button = true;
        private double _stack = 10;
        private double _ante;
        private bool _chartView;
        private bool _building;
        private int _version;
        private PushFoldSolution? _solution;
        private PushFoldChart? _chart;

        public PushFoldWindow()
        {
            InitializeComponent();
            Title = T("PushFold.Title");
            TitleText.Text = T("PushFold.Title");
            SummaryText.Text = T("PushFold.Summary");
            RoleLabel.Text = T("PushFold.Role");
            StackLabel.Text = T("PushFold.Stack");
            ViewLabel.Text = T("PushFold.View");
            AnteLabel.Text = T("PushFold.Ante");
            FindLabel.Text = T("Preflop.FindHand");
            SetName(HandSearchBox, T("Preflop.FindHand"));
            SetName(StackSlider, T("PushFold.Stack"));
            EquilibriumTitle.Text = Format("PushFold.EquilibriumTitle", _stack);
            HelpTitle.Text = T("Preflop.HelpTitle");
            HelpList.Children.Clear();
            foreach (var key in new[] { "PushFold.Help1", "PushFold.Help2", "PushFold.Help3", "PushFold.Help6", "PushFold.Help4", "PushFold.Help5" })
                HelpList.Children.Add(DimText("• " + T(key), new Thickness(0, 6, 0, 0)));

            BuildGrid();
            BuildSelectors();
            StackSlider.Value = _stack;
            StackValueText.Text = StackText(_stack);

            if (_solver is null)
            {
                ChartTitleText.Text = T("PushFold.NoData");
                ChartSubText.Text = PushFoldSolver.LoadError ?? "";
                ChartSubText.ToolTip = ChartSubText.Text;
                foreach (var cell in _cells)
                    PreflopChartsWindow.PaintCell(cell, (TextBlock)((Grid)cell.Child).Children[0], "F");
                return;
            }

            _ = RefreshAsync();
            _ = EnsureChartAsync();
        }

        // ---------------------------------------------------------------- selectores

        private void BuildSelectors()
        {
            _building = true;
            try
            {
                RolePanel.Children.Clear();
                RolePanel.Children.Add(Chip("role", T("PushFold.RoleButton"), T("PushFold.RoleButtonDesc"), "SB", _button));
                RolePanel.Children.Add(Chip("role", T("PushFold.RoleBigBlind"), T("PushFold.RoleBigBlindDesc"), "BB", !_button));

                StackChips.Children.Clear();
                foreach (var stack in QuickStacks)
                    StackChips.Children.Add(Chip("stack", StackText(stack), "", stack.ToString(CultureInfo.InvariantCulture), Math.Abs(stack - _stack) < 0.01));

                ViewPanel.Children.Clear();
                ViewPanel.Children.Add(Chip("view", T("PushFold.ViewStack"), T("PushFold.ViewStackDesc"), "stack", !_chartView));
                ViewPanel.Children.Add(Chip("view", T("PushFold.ViewChart"), T("PushFold.ViewChartDesc"), "chart", _chartView));

                AntePanel.Children.Clear();
                AntePanel.Children.Add(Chip("ante", T("PushFold.NoAnte"), T("PushFold.NoAnteDesc"), "0", _ante == 0));
                AntePanel.Children.Add(Chip("ante", T("PushFold.WithAnte"), T("PushFold.WithAnteDesc"), "ante", _ante > 0));
            }
            finally
            {
                _building = false;
            }
        }

        private RadioButton Chip(string group, string text, string tooltip, string value, bool isChecked)
        {
            var chip = new RadioButton
            {
                Content = text,
                ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip,
                Tag = value,
                GroupName = $"{group}-{GetHashCode()}",
                IsChecked = isChecked,
                Style = (Style)FindResource("Chip")
            };
            // Para el control por voz el nombre es exactamente lo que se lee en la ficha.
            SetName(chip, text);
            System.Windows.Automation.AutomationProperties.SetHelpText(chip, tooltip);
            chip.Checked += (_, _) =>
            {
                if (_building) return;
                switch (group)
                {
                    case "role":
                        _button = value == "SB";
                        break;
                    case "stack":
                        _stack = double.Parse(value, CultureInfo.InvariantCulture);
                        _building = true;
                        StackSlider.Value = _stack;
                        _building = false;
                        StackValueText.Text = StackText(_stack);
                        break;
                    case "view":
                        _chartView = value == "chart";
                        break;
                    case "ante":
                        _ante = value == "ante" ? AnteSize : 0;
                        _chart = null;
                        _ = EnsureChartAsync();
                        break;
                }
                _ = RefreshAsync();
            };
            return chip;
        }

        private void StackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_building || StackValueText is null)
                return;
            _stack = Math.Round(e.NewValue * 2) / 2;
            StackValueText.Text = StackText(_stack);
            // La ficha de esa pila queda marcada; si no hay ficha, ninguna.
            _building = true;
            foreach (var chip in StackChips.Children.OfType<RadioButton>())
                chip.IsChecked = Math.Abs(double.Parse((string)chip.Tag, CultureInfo.InvariantCulture) - _stack) < 0.01;
            _building = false;
            _ = RefreshAsync();
        }

        // ---------------------------------------------------------------- calculo

        /// <summary>Resuelve en segundo plano: al arrastrar la barra no se traba la ventana.</summary>
        private async Task RefreshAsync()
        {
            if (_solver is null)
                return;

            var version = ++_version;
            var stack = _stack;
            var ante = _ante;
            var solution = await Task.Run(() => _solver.Solve(stack, ante));
            if (version != _version)
                return;

            _solution = solution;
            if (_chart is null && _solver.HasChart(ante))
                _chart = _solver.Chart(ante);
            Render();
        }

        /// <summary>La tabla de "hasta cuantas ciegas" resuelve 49 pilas: se calcula aparte.</summary>
        private async Task EnsureChartAsync()
        {
            if (_solver is null)
                return;
            var ante = _ante;
            var chart = await Task.Run(() => _solver.Chart(ante));
            if (ante != _ante)
                return;
            _chart = chart;
            Render();
        }

        // ---------------------------------------------------------------- tabla 13x13

        private void BuildGrid()
        {
            ChartGrid.Children.Clear();
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    var handText = new TextBlock { Text = hand, HorizontalAlignment = HorizontalAlignment.Center };
                    var valueText = new TextBlock
                    {
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 2)
                    };
                    var content = new Grid();
                    content.Children.Add(handText);
                    content.Children.Add(valueText);
                    var cell = new Border
                    {
                        Margin = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1),
                        Child = content,
                        Tag = hand,
                        Cursor = Cursors.Hand
                    };
                    cell.MouseLeftButtonUp += (_, _) => HandSearchBox.Text = hand;
                    _cells[row, col] = cell;
                    _handTexts[row, col] = handText;
                    _valueTexts[row, col] = valueText;
                    ChartGrid.Children.Add(cell);
                }
            }
        }

        private void Render()
        {
            if (_solution is null)
                return;

            var role = _button ? "R" : "C";
            var showChart = _chartView && _chart is not null;
            ChartTitleText.Text = _chartView
                ? T(_button ? "PushFold.ChartTitleUpTo.SB" : "PushFold.ChartTitleUpTo.BB")
                : Format(_button ? "PushFold.ChartTitle.SB" : "PushFold.ChartTitle.BB", _solution.Stack);
            ChartSubText.Text = _chartView && _chart is null
                ? T("PushFold.Computing")
                : _chartView
                    ? T(_button ? "PushFold.ChartSubUpTo.SB" : "PushFold.ChartSubUpTo.BB") + AnteNote()
                    : Format(_button ? "PushFold.ChartSub.SB" : "PushFold.ChartSub.BB", _solution.PushPercent, _solution.CallPercent) + AnteNote();
            ChartTitleText.ToolTip = ChartTitleText.Text;
            ChartSubText.ToolTip = ChartSubText.Text;

            var hasMixed = false;
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var index = row * 13 + col;
                    var hand = AphPreflopProvider.HandAt(row, col);
                    var cell = _cells[row, col];
                    var handText = _handTexts[row, col];
                    var valueText = _valueTexts[row, col];

                    if (_chartView)
                    {
                        handText.FontSize = 11;
                        handText.FontWeight = FontWeights.SemiBold;
                        handText.VerticalAlignment = VerticalAlignment.Top;
                        handText.Margin = new Thickness(0, 2, 0, 0);
                        valueText.Visibility = Visibility.Visible;
                        var upTo = showChart ? (_button ? _chart!.PushUpTo[index] : _chart!.CallUpTo[index]) : 0;
                        PaintUpTo(cell, handText, valueText, role, upTo, showChart);
                        cell.ToolTip = showChart ? UpToSentence(hand, _button, upTo) : null;
                    }
                    else
                    {
                        handText.FontSize = 14;
                        handText.FontWeight = FontWeights.SemiBold;
                        handText.VerticalAlignment = VerticalAlignment.Center;
                        handText.Margin = new Thickness(0);
                        valueText.Visibility = Visibility.Collapsed;
                        var frequency = _button ? _solution.Push[index] : _solution.Call[index];
                        hasMixed |= frequency is > 0 and < 1;
                        PaintFrequency(cell, handText, role, frequency);
                        cell.ToolTip = FrequencySentence(hand, frequency);
                    }
                }
            }

            BuildLegend(role, hasMixed);
            HighlightSearch();
            ShowEquilibrium();
        }

        /// <summary>Con estas ciegas: color lleno si se juega siempre, partido si el equilibrio mezcla.</summary>
        private void PaintFrequency(Border cell, TextBlock label, string role, double frequency)
        {
            if (frequency >= 1 || frequency <= 0)
            {
                PreflopChartsWindow.PaintCell(cell, label, frequency >= 1 ? role : "F");
                return;
            }

            var color = ((SolidColorBrush)(role == "R" ? PreflopChartsWindow.RaiseBrush : PreflopChartsWindow.CallBrush)).Color;
            var inset = (TryFindResource("Brush.Inset") as SolidColorBrush)?.Color ?? Colors.Transparent;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(color, 0));
            brush.GradientStops.Add(new GradientStop(color, frequency));
            brush.GradientStops.Add(new GradientStop(inset, frequency));
            brush.GradientStops.Add(new GradientStop(inset, 1));
            brush.Freeze();
            cell.Background = brush;
            cell.BorderBrush = role == "R" ? PreflopChartsWindow.RaiseBrush : PreflopChartsWindow.CallBrush;
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        }

        /// <summary>
        /// Hasta cuantas ciegas: un solo color, mas intenso cuantas mas ciegas aguanta la mano.
        /// Lo que no se juega ni con 1 ciega queda con el fondo hundido.
        /// </summary>
        private void PaintUpTo(Border cell, TextBlock handText, TextBlock valueText, string role, double upTo, bool ready)
        {
            if (!ready || upTo <= 0)
            {
                PreflopChartsWindow.PaintCell(cell, handText, "F");
                valueText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
                valueText.Text = ready ? "–" : "";
                return;
            }

            var max = PushFoldSolver.MaxChartStack;
            var alpha = 0.22 + 0.78 * Math.Clamp((upTo - PushFoldSolver.MinStack) / (max - PushFoldSolver.MinStack), 0, 1);
            var brush = new SolidColorBrush(WithAlpha(role, alpha));
            brush.Freeze();
            cell.Background = brush;
            cell.BorderBrush = brush;
            if (alpha >= 0.6)
            {
                handText.Foreground = PreflopChartsWindow.InkOnColor;
                valueText.Foreground = PreflopChartsWindow.InkOnColor;
            }
            else
            {
                handText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
                valueText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            }
            valueText.Text = upTo >= max ? $"{max:0}+" : upTo.ToString("0.#", CultureInfo.CurrentCulture);
        }

        private static Color WithAlpha(string role, double alpha)
        {
            var color = ((SolidColorBrush)(role == "R" ? PreflopChartsWindow.RaiseBrush : PreflopChartsWindow.CallBrush)).Color;
            return Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B);
        }

        private void BuildLegend(string role, bool hasMixed)
        {
            LegendHost.Children.Clear();
            if (_chartView)
            {
                var panel = new DockPanel { LastChildFill = true };
                var never = new TextBlock { Text = T("PushFold.LegendNever"), Foreground = ThemeBrushes.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                DockPanel.SetDock(never, Dock.Right);
                panel.Children.Add(never);

                var scale = new Grid { VerticalAlignment = VerticalAlignment.Center };
                scale.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                scale.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var gradient = new LinearGradientBrush(WithAlpha(role, 0.22), WithAlpha(role, 1), 0);
                gradient.Freeze();
                scale.Children.Add(new Border { Height = 12, CornerRadius = new CornerRadius(3), Background = gradient });
                var labels = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                var left = new TextBlock { Text = StackText(PushFoldSolver.MinStack), Foreground = ThemeBrushes.TextDim, FontSize = 11 };
                var right = new TextBlock { Text = $"{PushFoldSolver.MaxChartStack:0}+ bb", Foreground = ThemeBrushes.TextDim, FontSize = 11 };
                DockPanel.SetDock(left, Dock.Left);
                DockPanel.SetDock(right, Dock.Right);
                labels.Children.Add(left);
                labels.Children.Add(right);
                labels.Children.Add(new TextBlock { Text = T(_button ? "PushFold.LegendUpTo.SB" : "PushFold.LegendUpTo.BB"), Foreground = ThemeBrushes.TextDim, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                Grid.SetRow(labels, 1);
                scale.Children.Add(labels);
                panel.Children.Add(scale);
                LegendHost.Children.Add(panel);
                return;
            }

            var share = _button ? _solution!.PushPercent : _solution!.CallPercent;
            var items = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            items.Children.Add(LegendItem(PreflopChartsWindow.Swatch(role), Action(role), Format("PushFold.LegendShare", share)));
            items.Children.Add(LegendItem(PreflopChartsWindow.Swatch("F"), Action("F"), Format("PushFold.LegendShare", 100 - share)));
            if (hasMixed)
            {
                var swatch = PreflopChartsWindow.Swatch(role);
                var inset = (TryFindResource("Brush.Inset") as SolidColorBrush)?.Color ?? Colors.Transparent;
                var color = ((SolidColorBrush)(role == "R" ? PreflopChartsWindow.RaiseBrush : PreflopChartsWindow.CallBrush)).Color;
                var split = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                split.GradientStops.Add(new GradientStop(color, 0.5));
                split.GradientStops.Add(new GradientStop(inset, 0.5));
                split.Freeze();
                swatch.Background = split;
                items.Children.Add(LegendItem(swatch, T("PushFold.LegendMixed"), T("PushFold.LegendMixedDesc")));
            }
            LegendHost.Children.Add(items);
        }

        private static FrameworkElement LegendItem(Border swatch, string name, string detail)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run("  " + detail) { Foreground = ThemeBrushes.TextDim });
            var item = new DockPanel { Margin = new Thickness(0, 0, 22, 4) };
            item.Children.Add(swatch);
            item.Children.Add(text);
            return item;
        }

        // ---------------------------------------------------------------- textos de cada mano

        private string Action(string code) => S.ActionLabel(S.HuAllIn, _button ? "SB" : "BB", code);

        private string FrequencySentence(string hand, double frequency)
        {
            var role = _button ? "R" : "C";
            var gain = _button ? _solution!.PushGainOf(hand) : _solution!.CallGainOf(hand);
            var action = frequency >= 1 ? Action(role) : frequency <= 0 ? Action("F")
                : Format("PushFold.Mixed", Action(role), 100 * frequency);
            return $"{hand} · {action} · {GainSentence(hand, _button, gain)}";
        }

        private static string GainSentence(string hand, bool button, double gain) =>
            Format((button ? "Practice.Why.Push" : "Practice.Why.Call") + (gain >= 0 ? "Wins" : "Loses"), hand, Math.Abs(gain));

        private static string UpToSentence(string hand, bool button, double upTo)
        {
            var prefix = button ? "Practice.Why.PushUpTo" : "Practice.Why.CallUpTo";
            return upTo >= PushFoldSolver.MaxChartStack ? Format(prefix + ".Always", hand, PushFoldSolver.MaxChartStack)
                : upTo <= 0 ? Format(prefix + ".Never", hand)
                : Format(prefix, hand, upTo);
        }

        private string AnteNote() => _ante > 0 ? " · " + T("PushFold.WithAnte").ToLowerInvariant() : "";

        // ---------------------------------------------------------------- buscar una mano

        // Se vuelve a pintar todo (169 casillas, inmediato) y encima se marca la mano buscada.
        private void HandSearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

        private void HighlightSearch()
        {
            if (_solution is null)
                return;

            var hands = AphPreflopProvider.ParseHandClasses(HandSearchBox.Text);
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var cell = _cells[row, col];
                    var match = hands.Count > 0 && hands.Contains((string)cell.Tag);
                    cell.BorderThickness = new Thickness(match ? 3 : 1);
                    if (match)
                        cell.BorderBrush = ThemeBrushes.Text;
                    cell.Opacity = hands.Count > 0 && !match ? 0.45 : 1;
                }
            }

            ShowLookup(string.IsNullOrWhiteSpace(HandSearchBox.Text) ? null : hands);
        }

        private void ShowLookup(IReadOnlyList<string>? hands)
        {
            LookupList.Children.Clear();
            if (_solver is null || _solution is null)
                return;

            if (hands is null || hands.Count == 0)
            {
                LookupTitle.Text = T("PushFold.LookupHeader");
                LookupList.Children.Add(DimText(T(hands is null ? "PushFold.LookupEmpty" : "Preflop.LookupInvalid")));
                return;
            }

            LookupTitle.Text = Format("PushFold.LookupTitle", string.Join(" / ", hands.Take(3)), _solution.Stack);
            foreach (var hand in hands.Take(3))
            {
                if (hands.Count > 1)
                    LookupList.Children.Add(new TextBlock { Text = hand, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 0) });

                var index = PreflopEquityMatrix.IndexOf(hand);
                var detail = _solver.Detail(_solution, hand);
                foreach (var button in new[] { _button, !_button })
                {
                    var role = button ? "R" : "C";
                    var frequency = button ? _solution.Push[index] : _solution.Call[index];
                    var code = frequency >= 0.5 ? role : "F";
                    var label = S.ActionLabel(S.HuAllIn, button ? "SB" : "BB", code);
                    if (frequency is > 0 and < 1)
                        label = Format("PushFold.Mixed", S.ActionLabel(S.HuAllIn, button ? "SB" : "BB", role), 100 * frequency);

                    var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                    line.Children.Add(PreflopChartsWindow.Swatch(code, 12));
                    var action = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
                    DockPanel.SetDock(action, Dock.Right);
                    line.Children.Add(action);
                    line.Children.Add(new TextBlock { Text = T(button ? "PushFold.RoleButtonShort" : "PushFold.RoleBigBlindShort") + (button == _button ? "  ◂" : "") });
                    LookupList.Children.Add(line);

                    var gain = button ? _solution.PushGain[index] : _solution.CallGain[index];
                    LookupList.Children.Add(DimText(GainSentence(hand, button, gain), new Thickness(20, 2, 0, 0)));
                    LookupList.Children.Add(DimText(button
                        ? Format("Practice.Why.PushMath", detail.FoldPercent, detail.EquityWhenCalled)
                        : Format("Practice.Why.CallMath", hand, detail.EquityVsPush, detail.EquityNeeded), new Thickness(20, 2, 0, 0)));
                    if (_chart is not null)
                        LookupList.Children.Add(DimText(UpToSentence(hand, button, button ? _chart.PushUpTo[index] : _chart.CallUpTo[index]), new Thickness(20, 2, 0, 0)));
                }
            }
        }

        private void ShowEquilibrium()
        {
            if (_solution is null)
                return;
            EquilibriumTitle.Text = Format("PushFold.EquilibriumTitle", _solution.Stack) + AnteNote();
            EquilibriumList.Children.Clear();
            EquilibriumList.Children.Add(DimText("• " + Format("PushFold.EqPush", _solution.PushPercent), new Thickness(0, 6, 0, 0)));
            EquilibriumList.Children.Add(DimText("• " + Format("PushFold.EqCall", _solution.CallPercent), new Thickness(0, 6, 0, 0)));
            EquilibriumList.Children.Add(DimText("• " + Format(_solution.ButtonValue >= 0 ? "PushFold.EqValueButton" : "PushFold.EqValueBigBlind",
                Math.Abs(_solution.ButtonValue)), new Thickness(0, 6, 0, 0)));
            EquilibriumList.Children.Add(DimText("• " + Format("PushFold.EqPrecision", Math.Max(_solution.Exploitability, 0.0001)), new Thickness(0, 6, 0, 0)));
        }

        // ---------------------------------------------------------------- utilidades

        private static string StackText(double stack) => stack.ToString("0.#", CultureInfo.CurrentCulture) + " bb";

        private static TextBlock DimText(string text, Thickness? margin = null) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.TextDim,
            Margin = margin ?? new Thickness(0)
        };

        private static void SetName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(key), args);

        private static string T(string key) => LocalizationManager.Text(key);
    }
}
