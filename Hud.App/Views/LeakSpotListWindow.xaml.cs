using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hud.App.Views
{
    public partial class LeakSpotListWindow : Window
    {
        private readonly LeakSpotListViewModel _viewModel;
        private bool _filtersReady;

        public LeakSpotListWindow(string title, string summary, IEnumerable<LeakSpotRow> hands)
        {
            InitializeComponent();
            Height = Math.Max(720, SystemParameters.WorkArea.Height - 44);
            MaxHeight = SystemParameters.WorkArea.Height;

            _viewModel = new LeakSpotListViewModel(title, summary, hands);
            DataContext = _viewModel;
            InitializeFilters();
        }

        private void InitializeFilters()
        {
            ConfigureOptionCombo(PositionFilter, new[] { LocalizedOption.Key("ALL", "Common.All") }
                .Concat(_viewModel.AllHands.Select(hand => hand.Position).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value).Select(LocalizedOption.Raw))
                .ToList());
            ConfigureOptionCombo(PotFilter, new[] { LocalizedOption.Key("ALL", "Common.AllMale") }
                .Concat(_viewModel.AllHands.Select(hand => hand.PotType).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value).Select(LocalizedOption.Raw))
                .ToList());
            ConfigureOptionCombo(ResultFilter, new[]
            {
                LocalizedOption.Key("ALL", "Common.AllMale"),
                LocalizedOption.Key("LOSSES", "Common.Lost"),
                LocalizedOption.Key("WINS", "Filter.Profits")
            });
            ConfigureOptionCombo(SortFilter, new[]
            {
                LocalizedOption.Key("WORST_FIRST", "Filter.WorstFirst"),
                LocalizedOption.Key("BEST_FIRST", "Filter.BestFirst"),
                LocalizedOption.Key("RECENT_DATE", "Filter.RecentDate"),
                LocalizedOption.Key("OLD_DATE", "Filter.OldDate"),
                LocalizedOption.Raw("Combo"),
                LocalizedOption.Key("POT", "Common.Pot")
            });

            PositionFilter.SelectedIndex = 0;
            PotFilter.SelectedIndex = 0;
            ResultFilter.SelectedIndex = 0;
            SortFilter.SelectedIndex = 0;
            _filtersReady = true;
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_filtersReady)
                return;

            _viewModel.ApplyFilters(
                SelectedText(PositionFilter),
                SelectedText(PotFilter),
                SelectedText(ResultFilter),
                SelectedText(SortFilter));
        }

        private void HandsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as DataGrid)?.SelectedItem is not LeakSpotRow row)
                return;

            var window = new TableDetailWindow(row.Table, row.HandIndex)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }

        private static string SelectedText(ComboBox comboBox) =>
            comboBox.SelectedValue?.ToString() ?? "";

        private static void ConfigureOptionCombo(ComboBox comboBox, IEnumerable<LocalizedOption> options)
        {
            comboBox.DisplayMemberPath = nameof(LocalizedOption.Label);
            comboBox.SelectedValuePath = nameof(LocalizedOption.Value);
            comboBox.ItemsSource = options.ToList();
        }

        private sealed class LeakSpotListViewModel : INotifyPropertyChanged
        {
            private readonly List<LeakSpotRow> _allHands;

            public LeakSpotListViewModel(string title, string summary, IEnumerable<LeakSpotRow> hands)
            {
                Title = title;
                Summary = summary;
                _allHands = hands.ToList();
                Hands = new ObservableCollection<LeakSpotRow>();
                ApplyFilters("ALL", "ALL", "ALL", "WORST_FIRST");
            }

            public string Title { get; }
            public string Summary { get; }
            public IReadOnlyList<LeakSpotRow> AllHands => _allHands;
            public ObservableCollection<LeakSpotRow> Hands { get; }
            public double TotalBb => Hands.Sum(item => item.NetBb);
            public string CountLabel => $"{Hands.Count} manos";
            public string TotalBbLabel => $"{TotalBb:+0.#;-0.#;0} bb";
            public Brush TotalBbBrush => TotalBb >= 0
                ? new SolidColorBrush(Color.FromRgb(33, 192, 122))
                : new SolidColorBrush(Color.FromRgb(226, 78, 91));

            public event PropertyChangedEventHandler? PropertyChanged;

            public void ApplyFilters(string position, string potType, string result, string sort)
            {
                IEnumerable<LeakSpotRow> query = _allHands;

                if (!string.IsNullOrWhiteSpace(position) && position != "ALL")
                    query = query.Where(hand => string.Equals(hand.Position, position, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(potType) && potType != "ALL")
                    query = query.Where(hand => string.Equals(hand.PotType, potType, StringComparison.OrdinalIgnoreCase));

                query = result switch
                {
                    "LOSSES" => query.Where(hand => hand.NetBb < 0),
                    "WINS" => query.Where(hand => hand.NetBb > 0),
                    _ => query
                };

                query = sort switch
                {
                    "BEST_FIRST" => query.OrderByDescending(hand => hand.NetBb).ThenByDescending(hand => hand.DateTime),
                    "RECENT_DATE" => query.OrderByDescending(hand => hand.DateTime).ThenBy(hand => hand.NetBb),
                    "OLD_DATE" => query.OrderBy(hand => hand.DateTime).ThenBy(hand => hand.NetBb),
                    "Combo" => query.OrderBy(hand => hand.Combo, StringComparer.OrdinalIgnoreCase).ThenBy(hand => hand.NetBb),
                    "POT" => query.OrderBy(hand => hand.PotType, StringComparer.OrdinalIgnoreCase).ThenBy(hand => hand.NetBb),
                    _ => query.OrderBy(hand => hand.NetBb).ThenBy(hand => hand.DateTime).ThenBy(hand => hand.TableName, StringComparer.OrdinalIgnoreCase)
                };

                Hands.Clear();
                foreach (var hand in query)
                    Hands.Add(hand);

                OnPropertyChanged(nameof(CountLabel));
                OnPropertyChanged(nameof(TotalBb));
                OnPropertyChanged(nameof(TotalBbLabel));
                OnPropertyChanged(nameof(TotalBbBrush));
            }

            private void OnPropertyChanged(string propertyName) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed record LeakSpotRow(
        MainWindow.TableSessionStats Table,
        string TableName,
        DateTime DateTime,
        int HandIndex,
        string Cards,
        string Combo,
        string Position,
        string Action,
        string PotType,
        string BoardTexture,
        string FlopCards,
        string TurnCard,
        string RiverCard,
        string PreflopLine,
        string FlopLine,
        string TurnLine,
        string RiverLine,
        string MadeHand,
        string DrawLabel,
        double NetBb,
        double CumulativeBb,
        string Reason)
    {
        public double AbsNetBb => Math.Abs(NetBb);
        public string DateTimeLabel => DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        public string DateStack => DateTime.ToString("yyyy-MM-dd\nHH:mm", CultureInfo.InvariantCulture);
        public string EffectiveStackLabel => "100.0";
        public string Spot => $"{Position} | {PotType}";
        public string RealLabel => NetBb >= 0 ? "win" : "loss";
        public string NetBbLabel => $"{NetBb:+0.#;-0.#;0} bb";
        public string NetBbTrendIcon => NetBb switch
        {
            > 0 => "\u25B2",
            < 0 => "\u25BC",
            _ => "\u25AC"
        };
        public Brush NetBbTrendBrush => NetBb switch
        {
            > 0 => new SolidColorBrush(Color.FromRgb(33, 192, 122)),
            < 0 => new SolidColorBrush(Color.FromRgb(226, 78, 91)),
            _ => new SolidColorBrush(Color.FromRgb(135, 145, 156))
        };
        public IReadOnlyList<CardChipViewModel> HeroCards => CardChipViewModel.FromCards(Cards);
        public IReadOnlyList<CardChipViewModel> FlopCardChips => CardChipViewModel.FromCards(FlopCards);
        public IReadOnlyList<CardChipViewModel> TurnCardChips => CardChipViewModel.FromCards(TurnCard);
        public IReadOnlyList<CardChipViewModel> RiverCardChips => CardChipViewModel.FromCards(RiverCard);
    }
}

