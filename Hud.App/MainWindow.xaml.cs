using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;
using Hud.App.Views;

namespace Hud.App
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private StakeProfile _dashboardStake = StakeProfile.Low;
        private bool _isLiteMode;
        private string _bestRecentTableSummary = "-";
        private string _bestRecentTableDetail = "Sin datos";
        private string _worstRecentTableSummary = "-";
        private string _worstRecentTableDetail = "Sin datos";
        private bool _isInitializingPokerRoomCombo;
        private string _activePokerRoom = "PokerStars";

        public ObservableCollection<PlayerStats> DashboardPlayers { get; } = new();
        public ObservableCollection<TableSessionStats> RecentTables { get; } = new();
        public ObservableCollection<DashboardTagViewModel> HeroTags { get; } = new();
        private readonly List<TableSessionStats> _allTables = new();
        private bool _isCashMode = true;

        public bool IsCashMode
        {
            get => _isCashMode;
            set
            {
                if (_isCashMode == value) return;
                _isCashMode = value;
                OnPropertyChanged(nameof(IsCashMode));
                RefreshFilteredData();
            }
        }

        public string TotalEarningsLabel => $"{_allTables.Where(t => t.IsCash == _isCashMode).Sum(t => t.NetBb):0.#} bb";

        public string WinrateLabel
        {
            get
            {
                var filtered = _allTables.Where(t => t.IsCash == _isCashMode).ToList();
                var totalHands = filtered.Sum(t => t.HandsReceived);
                if (totalHands == 0) return "0 bb/100";
                var totalNetBb = filtered.Sum(t => t.NetBb);
                return $"{(totalNetBb / (double)totalHands) * 100:0.#} bb/100";
            }
        }

        public int TotalTablesCount => _allTables.Count(t => t.IsCash == _isCashMode);
        public long TotalHandsCount => _allTables.Where(t => t.IsCash == _isCashMode).Sum(t => (long)t.HandsReceived);

        public string BestRecentTableSummary
        {
            get => _bestRecentTableSummary;
            private set
            {
                if (_bestRecentTableSummary == value) return;
                _bestRecentTableSummary = value;
                OnPropertyChanged(nameof(BestRecentTableSummary));
            }
        }

        public string BestRecentTableDetail
        {
            get => _bestRecentTableDetail;
            private set
            {
                if (_bestRecentTableDetail == value) return;
                _bestRecentTableDetail = value;
                OnPropertyChanged(nameof(BestRecentTableDetail));
            }
        }

        public string WorstRecentTableSummary
        {
            get => _worstRecentTableSummary;
            private set
            {
                if (_worstRecentTableSummary == value) return;
                _worstRecentTableSummary = value;
                OnPropertyChanged(nameof(WorstRecentTableSummary));
            }
        }

        public string WorstRecentTableDetail
        {
            get => _worstRecentTableDetail;
            private set
            {
                if (_worstRecentTableDetail == value) return;
                _worstRecentTableDetail = value;
                OnPropertyChanged(nameof(WorstRecentTableDetail));
            }
        }

        public StakeProfile DashboardStake
        {
            get => _dashboardStake;
            private set
            {
                if (_dashboardStake == value) return;
                _dashboardStake = value;
                OnPropertyChanged(nameof(DashboardStake));
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            Title = LocalizationManager.Text("App.Title");
            FitHeightToScreen();
            InitializePokerRoomSelector();
            Loaded += MainWindow_Loaded;
            ThemePaletteManager.PaletteApplied += ThemePaletteManager_PaletteApplied;
        }

        private void FitHeightToScreen()
        {
            var workArea = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, workArea.Height);
            MaxHeight = workArea.Height;
            Height = workArea.Height;
            Top = workArea.Top;
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        }

        private void InitializePokerRoomSelector()
        {
            _isInitializingPokerRoomCombo = true;
            var settings = AppSettingsService.Load();
            PokerRoomCombo.ItemsSource = PokerRoomPathsWindow.DefaultRooms;
            _activePokerRoom = PokerRoomPathsWindow.DefaultRooms.Contains(settings.SelectedPokerRoom)
                ? settings.SelectedPokerRoom
                : "PokerStars";
            PokerRoomCombo.SelectedItem = _activePokerRoom;
            ApplyPokerRoomCapabilities(PokerRoomCombo.SelectedItem as string);
            _isInitializingPokerRoomCombo = false;
        }

        private void ThemePaletteManager_PaletteApplied(object? sender, EventArgs e)
        {
            UpdatePlayerTags();
            Dispatcher.BeginInvoke(DrawPerformanceChart);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await Task.Delay(150);

            var settings = AppSettingsService.Load();
            _activePokerRoom = PokerRoomPathsWindow.DefaultRooms.Contains(settings.SelectedPokerRoom)
                ? settings.SelectedPokerRoom
                : "PokerStars";
            var startupFolder = GetSelectedPokerRoomFolder(settings);
            if (!string.IsNullOrWhiteSpace(startupFolder) &&
                Directory.Exists(startupFolder))
            {
                await AnalyzeAndLoadFolderAsync(startupFolder, _activePokerRoom, LocalizationManager.Text("Common.DefaultFolderLoaded"));
            }

            UpdateLastSessionFromReports();
        }

        private void UpdateLastSessionFromReports()
        {
            var sessions = ReportSessionIndexService.LoadSessions();
            if (sessions.Count > 0)
            {
                var last = sessions[0];
                var date = last.StartedAt ?? last.CreatedAt;
                LastSessionText.Text = $"Última sesión: {date:dd/MM/yyyy HH:mm}";
            }
            else
            {
                LastSessionText.Text = "Última sesión: Sin informes";
            }
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderGlobal");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new GlobalAnalysisWindow(_allTables, "")
            {
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusGlobalOpen");
        }

        private void BtnOne_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderTables");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new MisTablasWindow(_allTables, "")
            {
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusTablesOpen");
        }

        private void BtnGain_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = "Selecciona una carpeta primero para cargar Ganancia.";
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new GainAnalysisWindow(_allTables)
            {
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = "Analisis de ganancia abierto.";
        }

        private void BtnBestWorst_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderVillains");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new DataVillainsWindow(_allTables, "")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusVillainsOpen");
        }

        private void BtnLeakFinder_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderLeaks");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new LeakFinderWindow(_allTables, "")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusLeaksOpen");
        }

        private void BtnSessions_Click(object sender, RoutedEventArgs e)
        {
            var window = new SessionsWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = "Sesiones guardadas abiertas.";
        }

        private void BtnHeroProfile_Click(object sender, RoutedEventArgs e)
        {
            var hero = DashboardPlayers.FirstOrDefault();
            if (hero is null || RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderHero");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new HeroProfileWindow(hero, _allTables, DashboardStake, "")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = string.Format(LocalizationManager.Text("Common.StatusHeroOpen"), hero.Name);
        }

        private void BtnRT_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.Resources["HandReaderService"] is HandReaderService handService)
            {
                var rtWindow = new RealTimeWindow
                {
                    DataContext = handService,
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                rtWindow.Show();
                InfoText.Text = LocalizationManager.Text("Common.StatusRtOpen");

                // Actualizar al cerrar si es posible, o simplemente al abrir para refrescar
                UpdateLastSessionFromReports();
            }
            else
            {
                MessageBox.Show(
                    "El servicio HandReaderService no esta inicializado.\n" +
                    "Reinicia la aplicacion o revisa App.xaml.cs.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            var window = new PokerRoomPathsWindow
            {
                Owner = this,
                ShowInTaskbar = false
            };

            if (window.ShowDialog() != true ||
                string.IsNullOrWhiteSpace(window.SelectedFolder) ||
                !Directory.Exists(window.SelectedFolder))
            {
                return;
            }

            PokerRoomCombo.SelectedItem = window.SelectedRoom;
            await AnalyzeAndLoadFolderAsync(window.SelectedFolder, window.SelectedRoom ?? _activePokerRoom, $"{window.SelectedRoom} cargado en dashboard.");
        }

        private async void PokerRoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingPokerRoomCombo || PokerRoomCombo.SelectedItem is not string selectedRoom)
                return;

            var settings = AppSettingsService.Load();
            settings.SelectedPokerRoom = selectedRoom;
            AppSettingsService.Save(settings);
            _activePokerRoom = selectedRoom;
            ApplyPokerRoomCapabilities(selectedRoom);

            var folder = GetSelectedPokerRoomFolder(settings);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                await AnalyzeAndLoadFolderAsync(folder, selectedRoom, $"{selectedRoom} cargado en dashboard.");
                return;
            }

            ClearDashboardForRoom(selectedRoom);
            InfoText.Text = $"Configura la ruta de {selectedRoom} para analizar esa sala.";
        }

        private void ApplyPokerRoomCapabilities(string? room)
        {
            var supportsGameModeSwitch = SupportsGameModeSwitch(room);
            GameModeSwitch.Visibility = supportsGameModeSwitch ? Visibility.Visible : Visibility.Collapsed;

            if (!supportsGameModeSwitch && !IsCashMode)
                IsCashMode = true;
        }

        private static bool SupportsGameModeSwitch(string? room) =>
            string.Equals(room, "PokerStars", StringComparison.OrdinalIgnoreCase);

        private void ClearDashboardForRoom(string room)
        {
            DashboardPlayers.Clear();
            RecentTables.Clear();
            _allTables.Clear();
            DashboardStake = StakeProfile.Low;
            UpdatePerformanceSummary();
            UpdatePlayerTags();
            InfoText.Text = $"Sin datos cargados para {room}.";
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow
            {
                Owner = this,
                ShowInTaskbar = false
            };

            if (window.ShowDialog() == true)
            {
                Title = LocalizationManager.Text("App.Title");
                InfoText.Text = LocalizationManager.Text("Common.SettingsSaved");
            }
        }

        private static string? GetSelectedPokerRoomFolder(AppSettings settings)
        {
            if (settings.PokerRoomFolders.TryGetValue(settings.SelectedPokerRoom, out var selectedFolder) &&
                !string.IsNullOrWhiteSpace(selectedFolder))
            {
                return selectedFolder;
            }

            return string.Equals(settings.SelectedPokerRoom, "PokerStars", StringComparison.OrdinalIgnoreCase)
                ? settings.PokerStarsHandHistoryFolder
                : null;
        }

        private void BtnLiteMode_Click(object sender, RoutedEventArgs e)
        {
            _isLiteMode = !_isLiteMode;
            ApplyMainViewMode();
        }

        private void ApplyMainViewMode()
        {
            var proVisibility = _isLiteMode ? Visibility.Collapsed : Visibility.Visible;
            var liteVisibility = _isLiteMode ? Visibility.Visible : Visibility.Collapsed;

            GlobalStatsProHeader.Visibility = proVisibility;
            RecentTablesProHeader.Visibility = proVisibility;
            DashboardMetricsPanel.Visibility = proVisibility;
            PerformanceSummaryPanel.Visibility = proVisibility;
            GlobalStatsLiteHeader.Visibility = liteVisibility;
            RecentTablesLiteHeader.Visibility = liteVisibility;

            LiteModePrimaryText.Text = _isLiteMode ? "PRO" : "LITE";
            LiteModeSecondaryText.Text = "MODE";
        }

        private async Task AnalyzeAndLoadFolderAsync(string folder, string pokerRoom, string successMessage)
        {
            DashboardPlayers.Clear();
            RecentTables.Clear();
            _allTables.Clear();
            UpdatePerformanceSummary();
            UpdatePlayerTags();

            if (!SupportsHandHistoryAnalysis(pokerRoom))
            {
                await Task.Run(() => VillainHistoryStore.RebuildFromTables(Array.Empty<TableSessionStats>()));
                InfoText.Text = $"{pokerRoom} todavia no tiene parser activo. No se mezclan datos de otras salas.";
                return;
            }

            try
            {
                var result = await Task.Run(() => AnalyzeFolder(folder, pokerRoom));

                DashboardStake = result.Stake;
                if (result.Hero is not null && result.Tables.Count > 0)
                    DashboardPlayers.Add(result.Hero);
                UpdatePlayerTags();

                foreach (var table in result.Tables)
                    _allTables.Add(table);

                RefreshFilteredData();

                await Task.Run(() => VillainHistoryStore.RebuildFromTables(result.Tables));

                InfoText.Text = result.Tables.Count > 0
                    ? successMessage
                    : $"No encontre manos compatibles de {pokerRoom} en esa ruta.";
            }
            catch (Exception ex)
            {
                InfoText.Text = LocalizationManager.Text("Common.FolderAnalysisFailed");
                MessageBox.Show(ex.Message, LocalizationManager.Text("Common.AnalyzeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool SupportsHandHistoryAnalysis(string? room) =>
            string.Equals(room, "PokerStars", StringComparison.OrdinalIgnoreCase);

        private void PerformanceChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPerformanceChart();

        private void UpdatePerformanceSummary()
        {
            var recent = RecentTables.ToList();
            if (recent.Count == 0)
            {
                BestRecentTableSummary = "-";
                BestRecentTableDetail = "Sin datos";
                WorstRecentTableSummary = "-";
                WorstRecentTableDetail = "Sin datos";
                PerformanceTotalText.Text = "0 bb";
                OnPropertyChanged(nameof(TotalEarningsLabel));
                OnPropertyChanged(nameof(WinrateLabel));
                OnPropertyChanged(nameof(TotalTablesCount));
                OnPropertyChanged(nameof(TotalHandsCount));
                DrawPerformanceChart();
                return;
            }

            var best = recent.OrderByDescending(t => t.NetBb).First();
            var worst = recent.OrderBy(t => t.NetBb).First();
            BestRecentTableSummary = $"{best.TableName}  {best.NetBb:0.#} bb";
            BestRecentTableDetail = $"{best.GameFormat} · {best.PlayedDate}";
            WorstRecentTableSummary = $"{worst.TableName}  {worst.NetBb:0.#} bb";
            WorstRecentTableDetail = $"{worst.GameFormat} · {worst.PlayedDate}";
            PerformanceTotalText.Text = TotalEarningsLabel;
            
            OnPropertyChanged(nameof(TotalEarningsLabel));
            OnPropertyChanged(nameof(WinrateLabel));
            OnPropertyChanged(nameof(TotalTablesCount));
            OnPropertyChanged(nameof(TotalHandsCount));

            DrawPerformanceChart();
        }

        private void UpdatePlayerTags()
        {
            HeroTags.Clear();

            var hero = DashboardPlayers.FirstOrDefault();
            if (hero is null || hero.HandsReceived == 0)
            {
                HeroTags.Add(BuildTag("Sin datos", "Carga manos para generar los tags del perfil.", Neutral()));
                return;
            }

            foreach (var tag in BuildHeroProfileTags(hero).Take(8))
                HeroTags.Add(tag);
        }

        private static IEnumerable<DashboardTagViewModel> BuildHeroProfileTags(PlayerStats hero)
        {
            yield return BuildTag(
                ClassifyProfile(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct, hero.AF),
                LocalizationManager.Text("Tag.ProfileBaseHero.Desc"),
                Neutral());

            if (hero.HandsReceived < 30)
                yield return BuildTag(LocalizationManager.Text("Tag.NoSample"), LocalizationManager.Text("Tag.NoSample.Short"), Neutral());
            if (hero.VPIPPct >= 35)
                yield return BuildTag(LocalizationManager.Text("Tag.PlaysManyHands"), $"VPIP {hero.VPIPPct:0.#}%.", Danger());
            if (hero.AF >= 4 || hero.AFqPct >= 65)
                yield return BuildTag(LocalizationManager.Text("Tag.Aggressor"), $"AF {hero.AF:0.#} | AFq {hero.AFqPct:0.#}%.", Danger());
            if (hero.ThreeBetPct >= 10)
                yield return BuildTag(LocalizationManager.Text("Tag.High3Bet"), $"3Bet {hero.ThreeBetPct:0.#}%.", Danger());
            if (hero.FoldVsCBetFlopPct >= 65)
                yield return BuildTag(LocalizationManager.Text("Tag.FoldsCBet"), $"FvCB {hero.FoldVsCBetFlopPct:0.#}%.", Accent());
            if (hero.FoldVsCBetFlopPct > 0 && hero.FoldVsCBetFlopPct <= 30)
                yield return BuildTag(LocalizationManager.Text("Tag.NoFoldCBet"), $"FvCB {hero.FoldVsCBetFlopPct:0.#}%.", Danger());
            if (hero.WTSDPct >= 35)
                yield return BuildTag(LocalizationManager.Text("Tag.ShowdownOften"), $"WTSD {hero.WTSDPct:0.#}%.", Danger());
            if (hero.VPIPPct >= 30 && hero.PFRPct < 12 && hero.AF < 1.5)
                yield return BuildTag("Calling station", LocalizationManager.Text("Tag.CallingStation.Desc"), Accent());
            if (hero.VPIPPct < 14 && hero.PFRPct < 10 && hero.HandsReceived >= 50)
                yield return BuildTag(LocalizationManager.Text("Tag.Rock"), LocalizationManager.Text("Tag.Rock.Desc"), Neutral());

            yield return BuildTag(
                hero.VPIPPct >= 35 ? "Loose" : hero.VPIPPct <= 18 ? "Tight" : LocalizationManager.Text("Tag.MidRange"),
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.Vpip.Dynamic"), hero.VPIPPct),
                Accent());
            yield return BuildTag(
                hero.PFRPct >= 22 ? LocalizationManager.Text("Tag.AggressivePreflop") : hero.PFRPct <= 10 ? LocalizationManager.Text("Tag.LowPfr") : LocalizationManager.Text("Tag.StablePfr"),
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.Pfr.Dynamic"), hero.PFRPct),
                Neutral());
            yield return BuildTag(
                hero.FoldVsCBetFlopPct >= 65 ? "Overfold vs CBet" : LocalizationManager.Text("Tag.CBetDefenseOk"),
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.FoldCBet.Dynamic"), hero.FoldVsCBetFlopPct),
                hero.FoldVsCBetFlopPct >= 65 ? Danger() : Neutral());
            yield return BuildTag(
                hero.CBetFlopPct >= 55 ? LocalizationManager.Text("Tag.FrequentCBet") : LocalizationManager.Text("Tag.SelectiveCBet"),
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.CBet.Dynamic"), hero.CBetFlopPct),
                Neutral());
            yield return BuildTag(
                hero.WSDPct >= 55 ? LocalizationManager.Text("Tag.StrongShowdown") : LocalizationManager.Text("Tag.ReviewShowdown"),
                string.Format(CultureInfo.InvariantCulture, LocalizationManager.Text("Tag.Wsd.Dynamic"), hero.WSDPct),
                hero.WSDPct >= 55 ? Accent() : Danger());
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af)
        {
            if (hands < 30) return LocalizationManager.Text("Tag.NoSample");
            if (vpip >= 40 && pfr <= 10 && af < 1.5) return "Fish";
            if (vpip >= 45 || af >= 5 || threeBet >= 15) return "Maniac";
            if (vpip >= 35 && pfr < 15) return LocalizationManager.Text("Tag.LoosePassive");
            if (vpip >= 28 && pfr >= 20) return "LAG";
            if (vpip >= 18 && vpip < 29 && pfr >= 13 && pfr < 24) return "TAG";
            if (vpip < 14 && pfr < 10) return "Nit";
            if (vpip < 22 && pfr < 15) return "Tight";
            if (af < 1.2) return LocalizationManager.Text("Tag.Passive");
            return LocalizationManager.Text("Tag.Regular");
        }

        private static DashboardTagViewModel BuildTag(string text, string description, TagPalette palette) =>
            new(text, description, palette.Background, palette.Border, palette.Foreground);

        private static TagPalette Accent() =>
            new(BrushFrom(16, 44, 32), BrushFrom(33, 192, 122), BrushFrom(177, 255, 214));

        private static TagPalette Danger() =>
            new(BrushFrom(55, 22, 30), BrushFrom(226, 78, 91), BrushFrom(255, 199, 204));

        private static TagPalette Neutral() =>
            new(BrushFrom(18, 31, 45), BrushFrom(64, 92, 118), BrushFrom(203, 226, 245));

        private static Brush BrushFrom(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        private void RefreshFilteredData()
        {
            RecentTables.Clear();
            var filtered = _allTables
                .Where(t => t.IsCash == IsCashMode)
                .OrderByDescending(t => t.LastPlayedAt)
                .ToList();

            foreach (var table in filtered.Take(10))
                RecentTables.Add(table);

            UpdatePerformanceSummary();
        }

        private void DrawPerformanceChart()
        {
            if (!IsLoaded || PerformanceChart.ActualWidth <= 1 || PerformanceChart.ActualHeight <= 1)
                return;

            PerformanceChart.Children.Clear();

            var orderedTables = _allTables
                .Where(t => t.IsCash == IsCashMode)
                .OrderBy(t => t.LastPlayedAt)
                .ThenBy(t => t.TableName)
                .ToList();

            if (orderedTables.Count == 0)
            {
                AddChartLabel("Sin datos", PerformanceChart.ActualWidth / 2 - 24, PerformanceChart.ActualHeight / 2 - 8, Brushes.White);
                return;
            }

            var cumulative = new List<double>();
            var running = 0d;
            foreach (var table in orderedTables)
            {
                running += table.NetBb;
                cumulative.Add(running);
            }

            var width = PerformanceChart.ActualWidth;
            var height = PerformanceChart.ActualHeight;
            const double padLeft = 22;
            const double padTop = 12;
            const double padRight = 12;
            const double padBottom = 18;
            var plotLeft = padLeft;
            var plotTop = padTop;
            var plotRight = Math.Max(plotLeft + 1, width - padRight);
            var plotBottom = Math.Max(plotTop + 1, height - padBottom);
            var min = Math.Min(0, cumulative.Min());
            var max = Math.Max(0, cumulative.Max());
            if (Math.Abs(max - min) < 0.001)
            {
                max += 1;
                min -= 1;
            }

            double X(int i) => cumulative.Count == 1
                ? (plotLeft + plotRight) / 2
                : plotLeft + (plotRight - plotLeft) * i / (cumulative.Count - 1);
            double Y(double value) => plotBottom - (value - min) / (max - min) * (plotBottom - plotTop);

            var gridBrush = new SolidColorBrush(Color.FromArgb(70, 43, 61, 82));
            for (var i = 0; i <= 3; i++)
            {
                var y = plotTop + (plotBottom - plotTop) * i / 3;
                PerformanceChart.Children.Add(new Line
                {
                    X1 = plotLeft,
                    X2 = plotRight,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                });
            }

            var zeroY = Y(0);
            PerformanceChart.Children.Add(new Line
            {
                X1 = plotLeft,
                X2 = plotRight,
                Y1 = zeroY,
                Y2 = zeroY,
                Stroke = new SolidColorBrush(Color.FromArgb(140, 158, 173, 188)),
                StrokeThickness = 1
            });

            var isPositive = running >= 0;
            var positiveColor = GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139));
            var negativeColor = GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));
            var chartColor = isPositive ? positiveColor : negativeColor;

            var area = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(48, chartColor.R, chartColor.G, chartColor.B))
            };
            area.Points.Add(new Point(X(0), zeroY));
            for (var i = 0; i < cumulative.Count; i++)
                area.Points.Add(new Point(X(i), Y(cumulative[i])));
            area.Points.Add(new Point(X(cumulative.Count - 1), zeroY));
            PerformanceChart.Children.Add(area);

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(chartColor),
                StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round
            };
            for (var i = 0; i < cumulative.Count; i++)
                line.Points.Add(new Point(X(i), Y(cumulative[i])));
            PerformanceChart.Children.Add(line);

            // Dibujar todos los puntos interactivos
            for (var i = 0; i < cumulative.Count; i++)
            {
                var table = orderedTables[i];
                var x = X(i);
                var y = Y(cumulative[i]);

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(chartColor),
                    StrokeThickness = 2,
                    Cursor = Cursors.Hand,
                    ToolTip = new ToolTip
                    {
                        Content = BuildPerformanceChartToolTip(table, cumulative[i], chartColor),
                        Style = TryFindResource("ChartToolTipStyle") as Style
                    }
                };

                ToolTipService.SetInitialShowDelay(dot, 0);
                ToolTipService.SetBetweenShowDelay(dot, 0);

                Canvas.SetLeft(dot, x - 4);
                Canvas.SetTop(dot, y - 4);
                PerformanceChart.Children.Add(dot);
            }

            // Añadir etiquetas de fecha en el eje X (distribuidas)
            if (cumulative.Count > 0)
            {
                var labelCount = Math.Min(orderedTables.Count, 4);
                for (int i = 0; i < labelCount; i++)
                {
                    var idx = (labelCount <= 1) ? 0 : i * (orderedTables.Count - 1) / (labelCount - 1);
                    var x = X(idx);
                    var dateStr = orderedTables[idx].PlayedDate;
                    
                    // Intentar acortar la fecha si es muy larga (ej: 2026-02-05 -> 02-05)
                    if (dateStr.Length > 5 && dateStr.Contains("-"))
                        dateStr = dateStr.Substring(dateStr.IndexOf("-") + 1);

                    AddChartLabel(dateStr, x - 15, plotBottom + 4, Brushes.White);
                }
            }

            AddChartLabel($"{max:0.#}", 2, plotTop - 2, Brushes.White);
            AddChartLabel($"{min:0.#}", 2, plotBottom - 12, Brushes.White);
        }

        private void AddChartLabel(string text, double left, double top, Brush foreground)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.76
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            PerformanceChart.Children.Add(label);
        }

        private FrameworkElement BuildPerformanceChartToolTip(TableSessionStats table, double cumulativeBb, Color chartColor)
        {
            var isWin = table.NetBb >= 0;
            var resultColor = isWin
                ? GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139))
                : GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));
            var textBrush = GetThemeBrush("Brush.Text", Brushes.White);
            var dimBrush = GetThemeBrush("Brush.TextDim", new SolidColorBrush(Color.FromRgb(164, 184, 203)));
            var borderBrush = new SolidColorBrush(resultColor);

            var card = new Border
            {
                MinWidth = 210,
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
                MaxWidth = 170
            });
            root.Children.Add(titleRow);

            root.Children.Add(new TextBlock
            {
                Text = $"{table.GameFormat} · {table.PlayedDate}",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(21, 2, 0, 10)
            });

            var metrics = new Grid();
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var result = BuildTooltipMetric("Resultado", $"{(table.NetBb >= 0 ? "+" : "")}{table.NetBb:0.#} bb", borderBrush);
            var total = BuildTooltipMetric("Acumulado", $"{cumulativeBb:0.#} bb", new SolidColorBrush(chartColor));
            Grid.SetColumn(total, 1);
            metrics.Children.Add(result);
            metrics.Children.Add(total);
            root.Children.Add(metrics);

            var hands = new TextBlock
            {
                Text = $"{table.HandsReceived} manos",
                Foreground = dimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0)
            };
            root.Children.Add(hands);

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



        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public sealed record DashboardTagViewModel(
            string Text,
            string Description,
            Brush Background,
            Brush Border,
            Brush Foreground);

        private sealed record TagPalette(Brush Background, Brush Border, Brush Foreground);

        private sealed record DashboardResult(int FileCount, StakeProfile Stake, PlayerStats? Hero, List<TableSessionStats> Tables);

        public sealed record TableSessionStats(
            string TableName,
            string GameFormat,
            string PlayedDate,
            DateTime LastPlayedAt,
            string SourcePath,
            string PokerRoom,
            string HeroName,
            double BigBlind,
            StakeProfile Stake,
            int HandsReceived,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetFlopPct,
            double FoldVsCBetFlopPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            double NetBb,
            double NetAmount,
            bool IsCash,
            string NetAmountLabel)
        {
            public bool IsWin => NetBb >= 100;
            public bool IsProfitable => NetBb >= 0;
            public string TrendIcon => IsWin ? "\u25B2" : "\u25BC";
        }
    }
}


