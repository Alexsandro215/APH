using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Hud.App.Views
{
    public partial class GlobalAnalysisWindow : Window
    {
        private readonly List<MainWindow.TableSessionStats> _allTables;
        private readonly string _summary;
        private bool _filtersReady;

        public ObservableCollection<MainWindow.TableSessionStats> Tables { get; }

        public GlobalAnalysisWindow(IEnumerable<MainWindow.TableSessionStats> tables, string summary)
        {
            InitializeComponent();
            FitToWorkArea();
            Loaded += GlobalAnalysisWindow_Loaded;

            _summary = summary;
            _allTables = tables
                    .OrderByDescending(table => table.LastPlayedAt)
                    .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            Tables = new ObservableCollection<MainWindow.TableSessionStats>(_allTables);

            DataContext = this;
            ConfigureFilters();
            ApplyFilters();
        }

        private void GlobalAnalysisWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= GlobalAnalysisWindow_Loaded;
            FitToWorkArea();
        }

        private void FitToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            const double desiredWidth = 1460;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Height = workArea.Height;
            Top = workArea.Top;
            Width = Math.Min(desiredWidth, workArea.Width);
            MaxWidth = workArea.Width;
            Left = workArea.Width > Width
                ? workArea.Left + (workArea.Width - Width) / 2
                : workArea.Left;
        }

        private void ConfigureFilters()
        {
            ConfigureOptionCombo(DateFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Key("LAST_7", "Filter.Last7"),
                LocalizedOption.Key("LAST_30", "Filter.Last30"),
                LocalizedOption.Key("CURRENT_MONTH", "Filter.CurrentMonth")
            });
            ConfigureOptionCombo(ResultFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Key("WINNING", "Filter.Winning"),
                LocalizedOption.Key("LOSING", "Filter.Losing"),
                LocalizedOption.Key("WIN_100", "Filter.Win100"),
                LocalizedOption.Key("LOSS_100", "Filter.Loss100")
            });
            ConfigureOptionCombo(HandsFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Raw("10+"),
                LocalizedOption.Raw("25+"),
                LocalizedOption.Raw("50+"),
                LocalizedOption.Raw("100+")
            });
            ConfigureOptionCombo(MoneyTypeFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.All"),
                LocalizedOption.Raw("Cash"),
                LocalizedOption.Key("CHIPS", "Common.Chips")
            });
            ConfigureOptionCombo(SortFilter, new[]
            {
                LocalizedOption.Key("RECENT", "Filter.MostRecent"),
                LocalizedOption.Key("BB_WIN", "Filter.BbWin"),
                LocalizedOption.Key("BB_LOSS", "Filter.BbLoss"),
                LocalizedOption.Key("AMOUNT_WIN", "Filter.AmountWin"),
                LocalizedOption.Key("AMOUNT_LOSS", "Filter.AmountLoss")
            });

            ConfigureOptionCombo(BlindFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_allTables.Select(ExtractBlindLabel)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());

            ConfigureOptionCombo(FormatFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_allTables.Select(table => table.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(LocalizedOption.Raw))
                .ToList());

            DateFilter.SelectedIndex = 0;
            BlindFilter.SelectedIndex = 0;
            FormatFilter.SelectedIndex = 0;
            ResultFilter.SelectedIndex = 0;
            HandsFilter.SelectedIndex = 0;
            MoneyTypeFilter.SelectedIndex = 0;
            SortFilter.SelectedIndex = 0;
            _filtersReady = true;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_filtersReady)
                ApplyFilters();
        }

        private void TablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TablesGrid.SelectedItem is not MainWindow.TableSessionStats table)
                return;

            var window = new TableDetailWindow(table)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            TablesGrid.SelectedItem = null;
            window.Show();
        }

        private void ApplyFilters()
        {
            var query = _allTables.AsEnumerable();
            var search = SearchBox.Text?.Trim();

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(table =>
                    table.TableName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            query = ApplyDateFilter(query, SelectedText(DateFilter));
            query = ApplyBlindFilter(query, SelectedText(BlindFilter));
            query = ApplyFormatFilter(query, SelectedText(FormatFilter));
            query = ApplyResultFilter(query, SelectedText(ResultFilter));
            query = ApplyHandsFilter(query, SelectedText(HandsFilter));
            query = ApplyMoneyTypeFilter(query, SelectedText(MoneyTypeFilter));
            query = ApplySort(query, SelectedText(SortFilter));

            Tables.Clear();
            foreach (var table in query)
                Tables.Add(table);

            SummaryText.Text = $"{_summary} | {Hud.App.Services.LocalizationManager.Text("Grid.Table")}: {Tables.Count}/{_allTables.Count}";
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyDateFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            var today = DateTime.Today;

            return filter switch
            {
                "LAST_7" => query.Where(table => table.LastPlayedAt >= today.AddDays(-7)),
                "LAST_30" => query.Where(table => table.LastPlayedAt >= today.AddDays(-30)),
                "CURRENT_MONTH" => query.Where(table =>
                    table.LastPlayedAt.Year == today.Year && table.LastPlayedAt.Month == today.Month),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyBlindFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter == "ALL"
                ? query
                : query.Where(table => string.Equals(ExtractBlindLabel(table), filter, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyFormatFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter == "ALL"
                ? query
                : query.Where(table => string.Equals(table.GameFormat, filter, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyResultFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter switch
            {
                "WINNING" => query.Where(table => table.NetBb > 0),
                "LOSING" => query.Where(table => table.NetBb < 0),
                "WIN_100" => query.Where(table => table.NetBb >= 100),
                "LOSS_100" => query.Where(table => table.NetBb <= -100),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyHandsFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            if (filter == "ALL")
                return query;

            return int.TryParse(filter.TrimEnd('+'), out var minimum)
                ? query.Where(table => table.HandsReceived >= minimum)
                : query;
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyMoneyTypeFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter switch
            {
                "Cash" => query.Where(table => table.IsCash),
                "CHIPS" => query.Where(table => !table.IsCash),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplySort(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter switch
            {
                "BB_WIN" => query.OrderByDescending(table => table.NetBb)
                    .ThenByDescending(table => table.LastPlayedAt),
                "BB_LOSS" => query.OrderBy(table => table.NetBb)
                    .ThenByDescending(table => table.LastPlayedAt),
                "AMOUNT_WIN" => query.OrderByDescending(table => table.NetAmount)
                    .ThenByDescending(table => table.LastPlayedAt),
                "AMOUNT_LOSS" => query.OrderBy(table => table.NetAmount)
                    .ThenByDescending(table => table.LastPlayedAt),
                _ => query.OrderByDescending(table => table.LastPlayedAt)
                    .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void ConfigureOptionCombo(ComboBox comboBox, IEnumerable<LocalizedOption> options)
        {
            comboBox.DisplayMemberPath = nameof(LocalizedOption.Label);
            comboBox.SelectedValuePath = nameof(LocalizedOption.Value);
            comboBox.ItemsSource = options.ToList();
        }

        private static string SelectedText(ComboBox comboBox) =>
            comboBox.SelectedValue?.ToString() ?? "ALL";

        private static string ExtractBlindLabel(MainWindow.TableSessionStats table)
        {
            var start = table.TableName.LastIndexOf('(');
            var end = table.TableName.LastIndexOf(')');

            return start >= 0 && end > start
                ? table.TableName.Substring(start + 1, end - start - 1)
                : "";
        }
    }
}

