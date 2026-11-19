using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hud.App.Services;
using S = Hud.App.Services.PreflopScenarios;

namespace Hud.App.Views
{
    /// <summary>
    /// Tablas preflop de referencia: que hacer con cada mano segun la situacion y la posicion
    /// (6-max y heads-up, 100 ciegas grandes), con el tamano de cada subida y una comparacion con
    /// lo que hicieron los jugadores en sus manos reales.
    ///
    /// Es una tabla fija que el jugador consulta eligiendo la situacion a mano: no lee la mesa ni
    /// las cartas, asi que sirve igual que una tabla impresa (las salas no permiten ayuda en tiempo
    /// real con dinero real, pero si tablas de consulta).
    /// </summary>
    public partial class PreflopChartsWindow : Window
    {
        // Colores validados para daltonismo y contraste sobre el fondo del tema (naranja = subir,
        // azul = pagar). Retirarse usa el fondo hundido del tema: lo que no se juega no resalta.
        internal static readonly Brush RaiseBrush = Frozen(Color.FromRgb(0xD9, 0x59, 0x26));
        internal static readonly Brush CallBrush = Frozen(Color.FromRgb(0x39, 0x87, 0xE5));
        internal static readonly Brush InkOnColor = Frozen(Color.FromRgb(0x08, 0x10, 0x0B));

        private static PreflopChartsWindow? _floating;

        private readonly AphPreflopProvider _charts = PreflopLibrary.Charts;
        private readonly IReadOnlyList<(string Scenario, string Hero, string Villain)> _available;
        private readonly IReadOnlyList<string> _handFiles;
        private readonly bool _compact;
        private readonly Border[,] _cells = new Border[13, 13];

        private bool _headsUp;
        private string _scenario = S.Open;
        private string _hero = "CO";
        private string _villain = "";
        private bool _building;
        private List<PreflopSpot>? _spots;
        private string? _player;

        public PreflopChartsWindow() : this(Array.Empty<string>())
        {
        }

        public PreflopChartsWindow(IEnumerable<string> handFiles) : this(compact: false, handFiles.ToList(), null, null, null)
        {
        }

        private PreflopChartsWindow(bool compact, IReadOnlyList<string> handFiles, string? scenario, string? hero, string? villain)
        {
            InitializeComponent();
            _compact = compact;
            _handFiles = handFiles;
            _available = _charts.AvailableCharts();
            if (scenario is not null) { _scenario = scenario; _headsUp = S.IsHeadsUp(scenario); }
            if (hero is not null) _hero = hero;
            if (villain is not null) _villain = villain;

            Title = T("Preflop.Title");
            ApplyTexts();
            BuildGrid();
            if (compact)
                ApplyCompactLayout();

            RebuildSelectors();
            if (_available.Count == 0)
                ChartTitleText.Text = T("Preflop.NoCharts");

            if (!compact)
                _ = LoadSpotsAsync();
        }

        // ---------------------------------------------------------------- textos y modo compacto

        private void ApplyTexts()
        {
            TitleText.Text = T("Preflop.Title");
            SummaryText.Text = T("Preflop.Summary");
            TableLabel.Text = T("Preflop.Table");
            SituationLabel.Text = T("Preflop.Situation");
            HeroLabel.Text = T("Preflop.YourPosition");
            FindLabel.Text = T("Preflop.FindHand");
            FindHint.Text = T("Preflop.FindHandHint");
            FloatButton.Content = T("Preflop.Float");
            FloatButton.ToolTip = T("Preflop.FloatDesc");
            SetName(FloatButton, T("Preflop.Float"));
            SetName(HandSearchBox, T("Preflop.FindHand"));
            TopmostCheck.Content = T("Preflop.AlwaysOnTop");
            HelpTitle.Text = T("Preflop.HelpTitle");
            DraftBadgeText.Text = T("Preflop.Draft");
            DraftBadge.ToolTip = T("Preflop.DraftDesc");
            YouTitle.Text = T("Preflop.YouTitle");

            HelpList.Children.Clear();
            foreach (var key in new[] { "Preflop.Help1", "Preflop.Help2", "Preflop.Help3", "Preflop.Help5", "Preflop.Help4" })
                HelpList.Children.Add(DimText("• " + T(key), new Thickness(0, 6, 0, 0)));

            ShowLookup(null);
        }

        /// <summary>Ventana flotante: solo la tabla, la leyenda y lo minimo para elegir.</summary>
        private void ApplyCompactLayout()
        {
            HeaderCard.Visibility = Visibility.Collapsed;
            SidePanel.Visibility = Visibility.Collapsed;
            SideColumn.Width = new GridLength(0);
            FloatButton.Visibility = Visibility.Collapsed;
            FindHint.Visibility = Visibility.Collapsed;
            TopmostCheck.Visibility = Visibility.Visible;
            RootGrid.Margin = new Thickness(8);
            ControlsCard.Padding = new Thickness(10);
            ControlsCard.Margin = new Thickness(0, 0, 0, 8);
            ToolsColumn.Width = new GridLength(130);
            ToolsPanel.Margin = new Thickness(10, 0, 0, 0);
            ChartTitleText.FontSize = 14;
            SizeText.FontSize = 11;
            LegendPanel.Rows = 3;
            LegendPanel.Columns = 1;
            Topmost = true;
            Width = 560;
            Height = 900;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;
            if (Height > area.Height) Height = area.Height;
            Left = area.Right - Width - 8;
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        // ---------------------------------------------------------------- selectores

        private void RebuildSelectors()
        {
            _building = true;
            try
            {
                TablePanel.Children.Clear();
                TablePanel.Children.Add(Chip("table", T("Preflop.Table6"), T("Preflop.Table6Desc"), "6", !_headsUp));
                TablePanel.Children.Add(Chip("table", T("Preflop.TableHu"), T("Preflop.TableHuDesc"), "HU", _headsUp));

                var scenarios = (_headsUp ? S.HeadsUp : S.SixMax).Where(s => _available.Any(c => c.Scenario == s)).ToList();
                if (!scenarios.Contains(_scenario) && scenarios.Count > 0) _scenario = scenarios[0];

                ScenarioPanel.Children.Clear();
                foreach (var scenario in scenarios)
                    ScenarioPanel.Children.Add(Chip("scenario", S.Label(scenario), S.Description(scenario), scenario, scenario == _scenario));

                var heroes = S.PositionOrder.Where(p => _available.Any(c => c.Scenario == _scenario && c.Hero == p)).ToList();
                if (!heroes.Contains(_hero) && heroes.Count > 0) _hero = heroes.Contains("CO") ? "CO" : heroes[0];

                HeroPanel.Children.Clear();
                foreach (var hero in heroes)
                    HeroPanel.Children.Add(Chip("hero", S.PositionLabel(hero, _scenario), S.PositionHelp(hero, _scenario), hero, hero == _hero));

                var villains = S.PositionOrder.Where(p => _available.Any(c => c.Scenario == _scenario && c.Hero == _hero && c.Villain == p)).ToList();
                VillainLabel.Text = S.VillainQuestion(_scenario);
                VillainPanel.Children.Clear();
                if (villains.Count == 0)
                {
                    // Se oculta pero conserva su espacio (con una ficha invisible de relleno): si
                    // desapareciera, la tabla de abajo creceria y encogeria al cambiar de situacion.
                    _villain = "";
                    VillainSection.Visibility = Visibility.Hidden;
                    var placeholder = Chip("placeholder", "UTG", "", "", false);
                    placeholder.IsEnabled = false;
                    placeholder.Focusable = false;
                    VillainPanel.Children.Add(placeholder);
                }
                else
                {
                    if (!villains.Contains(_villain)) _villain = villains[0];
                    VillainSection.Visibility = Visibility.Visible;
                    foreach (var villain in villains)
                        VillainPanel.Children.Add(Chip("villain", S.PositionLabel(villain, _scenario), S.PositionHelp(villain, _scenario), villain, villain == _villain));
                }
            }
            finally
            {
                _building = false;
            }

            RefreshChart();
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
            if (_compact)
            {
                chip.Padding = new Thickness(10, 4, 10, 4);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.FontSize = 12;
            }
            // Para el control por voz el nombre es exactamente lo que se lee en la ficha.
            SetName(chip, text);
            System.Windows.Automation.AutomationProperties.SetHelpText(chip, tooltip);
            chip.Checked += (_, _) =>
            {
                if (_building) return;
                switch (group)
                {
                    case "table":
                        _headsUp = value == "HU";
                        _scenario = _headsUp ? S.HuOpen : S.Open;
                        _hero = _headsUp ? "SB" : "CO";
                        break;
                    case "scenario": _scenario = value; break;
                    case "hero": _hero = value; break;
                    default: _villain = value; break;
                }
                RebuildSelectors();
            };
            return chip;
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
                    var label = new TextBlock
                    {
                        Text = hand,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var cell = new Border
                    {
                        Margin = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1),
                        Child = label,
                        Tag = hand,
                        Cursor = Cursors.Hand
                    };
                    cell.MouseLeftButtonUp += (_, _) => HandSearchBox.Text = hand;
                    _cells[row, col] = cell;
                    ChartGrid.Children.Add(cell);
                }
            }
        }

        private string Code(string hand) => _charts.GetCode(_scenario, _hero, _villain, hand) ?? "F";

        private void RefreshChart()
        {
            if (_available.Count == 0)
                return;

            ChartTitleText.Text = S.ChartTitle(_scenario, _hero, _villain);
            ChartTitleText.ToolTip = ChartTitleText.Text;
            SizeText.Text = S.Sizing(_scenario, _hero);
            SizeText.ToolTip = SizeText.Text;
            DraftBadge.Visibility = S.IsDraft(_scenario) ? Visibility.Visible : Visibility.Hidden;

            var combos = new Dictionary<string, int>(StringComparer.Ordinal) { ["R"] = 0, ["C"] = 0, ["F"] = 0 };
            for (var row = 0; row < 13; row++)
            {
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    var code = Code(hand);
                    if (!combos.ContainsKey(code)) code = "F";
                    combos[code] += S.Combos(hand);

                    var cell = _cells[row, col];
                    PaintCell(cell, (TextBlock)cell.Child, code);
                    cell.ToolTip = $"{hand} · {S.ActionLabel(_scenario, _hero, code)} · {S.Combos(hand)} {T("Preflop.CombosWord")}";
                }
            }

            BuildLegend(combos);
            HighlightSearch();
            ShowYou();
        }

        internal static void PaintCell(Border cell, TextBlock label, string code)
        {
            switch (code)
            {
                case "R":
                    cell.Background = RaiseBrush;
                    cell.BorderBrush = RaiseBrush;
                    label.Foreground = InkOnColor;
                    break;
                case "C":
                    cell.Background = CallBrush;
                    cell.BorderBrush = CallBrush;
                    label.Foreground = InkOnColor;
                    break;
                default:
                    cell.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");
                    cell.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
                    label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
                    break;
            }
        }

        internal static Border Swatch(string code, double size = 14)
        {
            var swatch = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            PaintCell(swatch, new TextBlock(), code);
            return swatch;
        }

        private void BuildLegend(Dictionary<string, int> combos)
        {
            LegendPanel.Children.Clear();
            foreach (var code in new[] { "R", "C", "F" })
            {
                var pct = 100.0 * combos[code] / 1326;
                var share = string.Format(CultureInfo.CurrentCulture, T("Preflop.Share"), pct, combos[code]);
                var text = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = $"{S.ActionLabel(_scenario, _hero, code)}: {share}"
                };
                text.Inlines.Add(new System.Windows.Documents.Run(S.ActionLabel(_scenario, _hero, code)) { FontWeight = FontWeights.SemiBold });
                text.Inlines.Add(new System.Windows.Documents.Run("  " + share) { Foreground = ThemeBrushes.TextDim });

                var item = new DockPanel { Margin = new Thickness(0, 0, 16, 6) };
                item.Children.Add(Swatch(code));
                item.Children.Add(text);
                // "Pagar" no existe al abrir: su casilla queda vacia pero ocupa su lugar.
                if (code == "C" && combos[code] == 0)
                    item.Visibility = Visibility.Hidden;
                LegendPanel.Children.Add(item);
            }
        }

        // ---------------------------------------------------------------- tu contra la tabla

        private async Task LoadSpotsAsync()
        {
            YouList.Children.Clear();
            YouList.Children.Add(DimText(T("Preflop.YouLoading")));
            try
            {
                _spots = _handFiles.Count == 0 ? new List<PreflopSpot>() : await PreflopSpotReader.LoadAsync(_handFiles);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Tablas preflop: no se pudieron leer las manos", ex);
                _spots = new List<PreflopSpot>();
            }

            var players = _spots.GroupBy(s => s.Player).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            PlayerCombo.Items.Clear();
            PlayerCombo.Items.Add(new ComboBoxItem { Content = T("Preflop.AllPlayers"), Tag = null });
            foreach (var player in players)
                PlayerCombo.Items.Add(new ComboBoxItem { Content = player, Tag = player });
            PlayerCombo.SelectedIndex = 0;
            PlayerCombo.Visibility = players.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SetName(PlayerCombo, T("Preflop.PlayerFilter"));
            ShowYou();
        }

        private void PlayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _player = (PlayerCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            ShowYou();
        }

        private void ShowYou()
        {
            if (_spots is null || _compact)
                return;

            YouList.Children.Clear();
            var here = _spots.Where(s => s.Scenario == _scenario && s.Hero == _hero && s.Villain == _villain &&
                                         (_player is null || s.Player == _player)).ToList();
            if (here.Count == 0)
            {
                YouList.Children.Add(DimText(_spots.Count == 0 ? T("Preflop.YouNoHands") : T("Preflop.YouNone")));
                return;
            }

            YouList.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, T("Preflop.YouCount"), here.Count),
                TextWrapping = TextWrapping.Wrap
            });

            // Lo que hicieron contra lo que dice la tabla, por accion (en % de las veces).
            var chartCombos = new Dictionary<string, int> { ["R"] = 0, ["C"] = 0, ["F"] = 0 };
            for (var row = 0; row < 13; row++)
                for (var col = 0; col < 13; col++)
                {
                    var hand = AphPreflopProvider.HandAt(row, col);
                    chartCombos[Code(hand)] += S.Combos(hand);
                }

            foreach (var code in new[] { "R", "C", "F" })
            {
                var played = 100.0 * here.Count(s => s.Action == code) / here.Count;
                var chart = 100.0 * chartCombos[code] / 1326;
                if (played == 0 && chart == 0)
                    continue;
                YouList.Children.Add(CompareRow(code, played, chart));
            }

            var different = here.Where(s => s.Action != Code(s.Hand)).ToList();
            var matches = here.Count - different.Count;
            YouList.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, T("Preflop.YouMatch"), matches, here.Count),
                Margin = new Thickness(0, 10, 0, 4),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var spot in different.TakeLast(8).Reverse())
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Cursor = Cursors.Hand };
                line.Inlines.Add(new System.Windows.Documents.Run(spot.Hand) { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run("  " + string.Format(CultureInfo.CurrentCulture, T("Preflop.YouDiff"),
                    S.ActionLabel(_scenario, _hero, spot.Action), S.ActionLabel(_scenario, _hero, Code(spot.Hand))))
                {
                    Foreground = ThemeBrushes.TextDim
                });
                line.ToolTip = $"{spot.Player} · {spot.Table} · #{spot.HandId}";
                line.MouseLeftButtonUp += (_, _) => HandSearchBox.Text = spot.Hand;
                YouList.Children.Add(line);
            }
        }

        /// <summary>Dos barras finas: lo que hicieron (arriba) y lo que dice la tabla (abajo).</summary>
        private FrameworkElement CompareRow(string code, double played, double chart)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var header = new DockPanel();
            header.Children.Add(Swatch(code, 12));
            var numbers = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, T("Preflop.YouVsChart"), played, chart),
                Foreground = ThemeBrushes.TextDim
            };
            DockPanel.SetDock(numbers, Dock.Right);
            header.Children.Add(numbers);
            header.Children.Add(new TextBlock { Text = S.ActionLabel(_scenario, _hero, code), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(header);
            panel.Children.Add(Bar(played, code, 1.0));
            panel.Children.Add(Bar(chart, code, 0.45));
            return panel;
        }

        private static FrameworkElement Bar(double pct, string code, double opacity)
        {
            var grid = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(3), Opacity = opacity };
            PaintCell(fill, new TextBlock(), code);
            if (code == "F") fill.SetResourceReference(Border.BackgroundProperty, "Brush.TextDim");
            var track = new Border { CornerRadius = new CornerRadius(3) };
            track.SetResourceReference(Border.BackgroundProperty, "Brush.Inset");
            Grid.SetColumn(track, 1);
            grid.Children.Add(fill);
            grid.Children.Add(track);
            return grid;
        }

        // ---------------------------------------------------------------- buscar una mano

        private void HandSearchBox_TextChanged(object sender, TextChangedEventArgs e) => HighlightSearch();

        private void HighlightSearch()
        {
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
                    else
                        PaintCell(cell, (TextBlock)cell.Child, Code((string)cell.Tag));
                    cell.Opacity = hands.Count > 0 && !match ? 0.45 : 1;
                }
            }

            ShowLookup(string.IsNullOrWhiteSpace(HandSearchBox.Text) ? null : hands);
        }

        private void ShowLookup(IReadOnlyList<string>? hands)
        {
            LookupList.Children.Clear();
            if (hands is null || hands.Count == 0)
            {
                LookupTitle.Text = T("Preflop.LookupHeader");
                LookupList.Children.Add(DimText(T(hands is null ? "Preflop.LookupEmpty" : "Preflop.LookupInvalid")));
                return;
            }

            LookupTitle.Text = string.Format(CultureInfo.CurrentCulture, T("Preflop.LookupTitle"), string.Join(" / ", hands));

            // Sin rival se compara cada posicion; con rival, tu posicion contra cada rival.
            var openLike = !_available.Any(c => c.Scenario == _scenario && c.Villain != "");
            var rows = openLike
                ? S.PositionOrder.Where(p => _charts.HasChart(_scenario, p, ""))
                    .Select(p => (Label: S.PositionLabel(p, _scenario), Hero: p, Villain: "", Current: p == _hero))
                : S.PositionOrder.Where(v => _charts.HasChart(_scenario, _hero, v))
                    .Select(v => (Label: string.Format(CultureInfo.CurrentCulture, T("Preflop.LookupVs"), S.PositionLabel(_hero, _scenario), S.PositionLabel(v, _scenario)),
                        Hero: _hero, Villain: v, Current: v == _villain));

            foreach (var hand in hands)
            {
                if (hands.Count > 1)
                    LookupList.Children.Add(new TextBlock { Text = hand, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });

                foreach (var row in rows)
                {
                    var code = _charts.GetCode(_scenario, row.Hero, row.Villain, hand) ?? "F";
                    var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                    line.Children.Add(Swatch(code, 12));
                    var action = new TextBlock { Text = S.ActionLabel(_scenario, row.Hero, code), FontWeight = FontWeights.SemiBold };
                    DockPanel.SetDock(action, Dock.Right);
                    line.Children.Add(action);
                    // La etiqueta va al final para que llene el espacio entre la muestra y la accion.
                    line.Children.Add(new TextBlock
                    {
                        Text = row.Label + (row.Current ? "  ◂" : ""),
                        Foreground = row.Current ? ThemeBrushes.Text : ThemeBrushes.TextDim
                    });
                    LookupList.Children.Add(line);
                }
            }
        }

        // ---------------------------------------------------------------- ventana flotante

        private void FloatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_floating is { IsLoaded: true })
            {
                _floating.WindowState = WindowState.Normal;
                _floating.Activate();
                return;
            }

            _floating = new PreflopChartsWindow(compact: true, _handFiles, _scenario, _hero, _villain);
            _floating.Closed += (_, _) => _floating = null;
            _floating.Show();
        }

        private void TopmostCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_compact)
                Topmost = TopmostCheck.IsChecked == true;
        }

        // ---------------------------------------------------------------- utilidades

        private static TextBlock DimText(string text, Thickness? margin = null) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.TextDim,
            Margin = margin ?? new Thickness(0)
        };

        private static void SetName(DependencyObject element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private static string T(string key) => LocalizationManager.Text(key);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
