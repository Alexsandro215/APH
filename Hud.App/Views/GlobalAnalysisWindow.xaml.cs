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

        private void ConfigureFilters()
        {
            DateFilter.ItemsSource = new[] { "Todas", "Ultimos 7 dias", "Ultimos 30 dias", "Mes actual" };
            ResultFilter.ItemsSource = new[] { "Todas", "Ganadoras", "Perdedoras", "+100bb o mas", "-100bb o peor" };
            HandsFilter.ItemsSource = new[] { "Todas", "10+", "25+", "50+", "100+" };
            MoneyTypeFilter.ItemsSource = new[] { "Todas", "Cash", "Fichas" };
            SortFilter.ItemsSource = new[]
            {
                "Mas recientes",
                "Mayor ganancia bb",
                "Mayor perdida bb",
                "Mayor ganancia fichas/dinero",
                "Mayor perdida fichas/dinero"
            };

            BlindFilter.ItemsSource = new[] { "Todas" }
                .Concat(_allTables.Select(ExtractBlindLabel)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();

            FormatFilter.ItemsSource = new[] { "Todas" }
                .Concat(_allTables.Select(table => table.GameFormat)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                .ToList();

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
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
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

            SummaryText.Text = $"{_summary} | Mesas: {Tables.Count}/{_allTables.Count}";
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyDateFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            var today = DateTime.Today;

            return filter switch
            {
                "Ultimos 7 dias" => query.Where(table => table.LastPlayedAt >= today.AddDays(-7)),
                "Ultimos 30 dias" => query.Where(table => table.LastPlayedAt >= today.AddDays(-30)),
                "Mes actual" => query.Where(table =>
                    table.LastPlayedAt.Year == today.Year && table.LastPlayedAt.Month == today.Month),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyBlindFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter == "Todas"
                ? query
                : query.Where(table => string.Equals(ExtractBlindLabel(table), filter, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyFormatFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter == "Todas"
                ? query
                : query.Where(table => string.Equals(table.GameFormat, filter, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyResultFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter switch
            {
                "Ganadoras" => query.Where(table => table.NetBb > 0),
                "Perdedoras" => query.Where(table => table.NetBb < 0),
                "+100bb o mas" => query.Where(table => table.NetBb >= 100),
                "-100bb o peor" => query.Where(table => table.NetBb <= -100),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplyHandsFilter(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            if (filter == "Todas")
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
                "Fichas" => query.Where(table => !table.IsCash),
                _ => query
            };
        }

        private static IEnumerable<MainWindow.TableSessionStats> ApplySort(
            IEnumerable<MainWindow.TableSessionStats> query,
            string filter)
        {
            return filter switch
            {
                "Mayor ganancia bb" => query.OrderByDescending(table => table.NetBb)
                    .ThenByDescending(table => table.LastPlayedAt),
                "Mayor perdida bb" => query.OrderBy(table => table.NetBb)
                    .ThenByDescending(table => table.LastPlayedAt),
                "Mayor ganancia fichas/dinero" => query.OrderByDescending(table => table.NetAmount)
                    .ThenByDescending(table => table.LastPlayedAt),
                "Mayor perdida fichas/dinero" => query.OrderBy(table => table.NetAmount)
                    .ThenByDescending(table => table.LastPlayedAt),
                _ => query.OrderByDescending(table => table.LastPlayedAt)
                    .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static string SelectedText(ComboBox comboBox) =>
            comboBox.SelectedItem?.ToString() ?? "Todas";

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
