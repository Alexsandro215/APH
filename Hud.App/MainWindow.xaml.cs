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
        private string _bestRecentTableDetail = LocalizationManager.Text("Main.NoData");
        private string _worstRecentTableSummary = "-";
        private string _worstRecentTableDetail = LocalizationManager.Text("Main.NoData");
        private bool _isInitializingPokerRoomCombo;
        private string _activePokerRoom = "PokerStars";
        private bool _isClosingAfterSync;

        public ObservableCollection<PlayerStats> DashboardPlayers { get; } = new();
        public ObservableCollection<TableSessionStats> RecentTables { get; } = new();
        public ObservableCollection<DashboardTagViewModel> HeroTags { get; } = new();
        private readonly List<TableSessionStats> _allTables = new();
        private bool _isCashMode = true;

        /// <summary>Cada mano del heroe en orden, con su resultado y la suerte del all-in (grafica mano a mano).</summary>
        private IReadOnlyList<HandPoint> _handSeries = Array.Empty<HandPoint>();
        private sealed record HandPoint(DateTime At, string SourcePath, bool IsCash, double NetBb, double LuckBb);

        private string _heroSummarySentence = "";

        /// <summary>El perfil y el resultado en una frase, bajo el nombre del heroe.</summary>
        public string HeroSummarySentence
        {
            get => _heroSummarySentence;
            private set
            {
                if (_heroSummarySentence == value) return;
                _heroSummarySentence = value;
                OnPropertyChanged(nameof(HeroSummarySentence));
            }
        }

        public bool IsCashMode
        {
            get => _isCashMode;
            set
            {
                if (_isCashMode == value) return;
                _isCashMode = value;
                OnPropertyChanged(nameof(IsCashMode));
                _revealChart = true;
                RefreshFilteredData();
                // La retroalimentacion de las etiquetas cambia con el modo (ficticio o dinero real).
                UpdatePlayerTags();
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

            // La tabla de mesas recientes, al cargarse, pedia "traerse a la vista" y el panel se
            // abria desplazado hacia abajo, sin los indicadores de arriba.
            RecentTablesGrid.RequestBringIntoView += (_, e) => e.Handled = true;

            // Volver como en un navegador: boton "atras" del raton o Alt + flecha izquierda.
            PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1 && BackButton.Visibility == Visibility.Visible)
                {
                    GoBack();
                    e.Handled = true;
                }
            };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.System && e.SystemKey == Key.Left && BackButton.Visibility == Visibility.Visible)
                {
                    GoBack();
                    e.Handled = true;
                }
            };
            Title = LocalizationManager.Text("App.Title");
            FitHeightToScreen();
            InitializePokerRoomSelector();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            ThemePaletteManager.PaletteApplied += ThemePaletteManager_PaletteApplied;
            LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;

            // Avisos flotantes, el oscurecido detras de las ventanas de dialogo y el tamaño de la interfaz.
            Toast.Register(ToastHost);
            System.Windows.Interop.ComponentDispatcher.EnterThreadModal += OnEnterThreadModal;
            System.Windows.Interop.ComponentDispatcher.LeaveThreadModal += OnLeaveThreadModal;
            Closed += (_, _) =>
            {
                System.Windows.Interop.ComponentDispatcher.EnterThreadModal -= OnEnterThreadModal;
                System.Windows.Interop.ComponentDispatcher.LeaveThreadModal -= OnLeaveThreadModal;
            };
            ApplyInterfaceScale(AppSettingsService.Load().UiScale);

            // Al ver mesas de PokerStars abiertas, pregunta si arranca Tiempo real (MainWindow.TableWatch.cs).
            StartTableWatch();
        }

        // Ventanas de dialogo abiertas ahora mismo (una puede abrir otra: Configuracion > contraseña).
        private int _openDialogs;

        /// <summary>Mientras hay una ventana de dialogo encima, la principal se oscurece un poco.</summary>
        private void OnEnterThreadModal(object? sender, EventArgs e)
        {
            if (++_openDialogs == 1)
                AnimateDim(0.42);
        }

        private void OnLeaveThreadModal(object? sender, EventArgs e)
        {
            _openDialogs = Math.Max(0, _openDialogs - 1);
            if (_openDialogs == 0)
                AnimateDim(0);
        }

        private void AnimateDim(double to)
        {
            DimOverlay.Visibility = Visibility.Visible;
            if (!UiMotion.Enabled)
            {
                DimOverlay.BeginAnimation(OpacityProperty, null);
                DimOverlay.Opacity = to;
                if (to <= 0)
                    DimOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var fade = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 180 : 140));
            if (to <= 0)
                fade.Completed += (_, _) =>
                {
                    if (_openDialogs == 0)
                        DimOverlay.Visibility = Visibility.Collapsed;
                };
            DimOverlay.BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Tamaño de la interfaz (Configuracion > Apariencia): 90 %, 100 %, 110 % o 125 %.</summary>
        public void ApplyInterfaceScale(double scale)
        {
            scale = AppSettings.NormalizeUiScale(scale);
            ShellRoot.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        private void FitHeightToScreen()
        {
            var workArea = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, workArea.Height);
            MinWidth = Math.Min(MinWidth, workArea.Width);
            MaxHeight = workArea.Height;
            Height = workArea.Height;
            // Las paginas de analisis viven dentro de esta ventana: se aprovecha casi todo el ancho.
            Width = Math.Min(1680, workArea.Width);
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
            _paletteChanged = true;
            Dispatcher.BeginInvoke(() => RefreshBindings(DashboardPage), System.Windows.Threading.DispatcherPriority.Background);
            UpdatePlayerTags();
            Dispatcher.BeginInvoke(DrawPerformanceChart);
        }

        private void LocalizationManager_LanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(RefreshLocalizedDashboardText);
        }

        private void RefreshLocalizedDashboardText()
        {
            Title = LocalizationManager.Text("App.Title");
            ActiveRoomLabel.Text = LocalizationManager.Text("Main.ActiveRoom");
            UpdateLastSessionFromReports();
            UpdatePlayerTags();
            UpdatePerformanceSummary();
            ApplyMainViewMode();
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

            _ = Task.Run(BackupConfiguredHandHistoryFolders);
            // Ritmo de la sala: cuanto se retira de verdad la gente, leido de los historiales (en segundo plano).
            Hud.App.Services.Trainer.TrainerRoomRhythm.Shared.Start();
            UpdateLastSessionFromReports();
        }

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;

            if (_isClosingAfterSync)
                return;

            var settings = AppSettingsService.Load();
            if (!settings.GoogleSyncEnabled)
            {
                BackupConfiguredHandHistoryFolders();
                return;
            }

            e.Cancel = true;
            IsEnabled = false;

            try
            {
                await Task.Run(BackupConfiguredHandHistoryFolders);

                if (GoogleDriveBackupService.HasCredentialsFile &&
                    GoogleDriveBackupService.HasStoredToken &&
                    File.Exists(AphBackupDatabaseService.DatabasePath))
                {
                    await GoogleDriveBackupService.UploadDatabaseAsync();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"close sync failed: {ex.Message}");
            }
            finally
            {
                _isClosingAfterSync = true;
                Close();
            }
        }

        private Dictionary<string, TableSessionStats> SnapshotKnownTables() =>
            _allTables
                .Where(table => !string.IsNullOrWhiteSpace(table.SourcePath))
                .GroupBy(table => table.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(table => table.LastPlayedAt).First(),
                    StringComparer.OrdinalIgnoreCase);

        private void BackupConfiguredHandHistoryFolders()
        {
            try
            {
                var settings = AppSettingsService.Load();
                var folders = GetConfiguredPokerRoomFolders(settings);
                if (folders.Count == 0)
                    return;

                // _allTables y DashboardPlayers pertenecen al hilo de UI y este metodo tambien
                // corre en Task.Run: hay que copiarlas dentro del Dispatcher antes de tocarlas.
                Dictionary<string, TableSessionStats> knownTables;
                string? heroName;

                if (Dispatcher.CheckAccess())
                {
                    knownTables = SnapshotKnownTables();
                    heroName = DashboardPlayers.FirstOrDefault()?.Name;
                }
                else
                {
                    var snapshot = Dispatcher.Invoke(() =>
                        (Tables: SnapshotKnownTables(), Hero: DashboardPlayers.FirstOrDefault()?.Name));
                    knownTables = snapshot.Tables;
                    heroName = snapshot.Hero;
                }

                foreach (var (room, folder) in folders)
                    AphBackupDatabaseService.BackupFolder(folder, room, heroName, knownTables);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"automatic backup failed: {ex.Message}");
            }
        }

        private void UpdateLastSessionFromReports()
        {
            var sessions = ReportSessionIndexService.LoadSessions();
            if (sessions.Count > 0)
            {
                // La sesion jugada mas reciente (no el ultimo informe creado: se puede generar hoy el
                // informe de una sesion de ayer), en hora local.
                var date = sessions
                    .Select(record => record.StartedAt is { } started ? ReportSessionIndexService.ToLocal(started) : record.CreatedAt)
                    .Max();
                LastSessionText.Text = $"{LocalizationManager.Text("Main.LastSession")} {date:dd/MM/yyyy HH:mm}";
            }
            else
            {
                LastSessionText.Text = $"{LocalizationManager.Text("Main.LastSession")} {LocalizationManager.Text("Main.NoData")}";
            }
        }

        // Las funciones de analisis ya no abren ventanas sueltas: se muestran como paginas dentro de
        // esta ventana (MainWindow.Navigation.cs). Sin manos cargadas, se pide la carpeta como antes.
        private bool EnsureTablesLoaded(object sender, RoutedEventArgs e, string statusKey)
        {
            if (RecentTables.Count > 0)
                return true;

            InfoText.Text = LocalizationManager.Text(statusKey);
            SyncNavSelection();
            BtnPickFolder_Click(sender, e);
            return false;
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderGlobal"))
                return;

            NavigateToPage("tables", NavTables, () => new GlobalAnalysisWindow(_allTables, ""));
            InfoText.Text = LocalizationManager.Text("Common.StatusGlobalOpen");
        }

        private void BtnOne_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderTables"))
                return;

            NavigateToPage("mytables", NavMyTables, () => new MisTablasWindow(_allTables, ""));
            InfoText.Text = LocalizationManager.Text("Common.StatusTablesOpen");
        }

        private void BtnGain_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTablesLoaded(sender, e, "Main.GainFolderRequired"))
                return;

            NavigateToPage("gain", NavGain, () => new GainAnalysisWindow(_allTables));
            InfoText.Text = LocalizationManager.Text("Main.RailGain");
        }

        private void BtnBestWorst_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderVillains"))
                return;

            NavigateToPage("villains", NavVillains, () => new DataVillainsWindow(_allTables, ""));
            InfoText.Text = LocalizationManager.Text("Common.StatusVillainsOpen");
        }

        private void BtnLeakFinder_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderLeaks"))
                return;

            NavigateToPage("leaks", NavLeaks, () => new LeakFinderWindow(_allTables, ""));
            InfoText.Text = LocalizationManager.Text("Common.StatusLeaksOpen");
        }

        private void BtnPreflop_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPage("preflop", NavPreflop, () => new PreflopChartsWindow(_allTables.Select(t => t.SourcePath)));
        }

        private void BtnPractice_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPage("practice", NavPractice, () => new PreflopPracticeWindow(_allTables.Select(t => t.SourcePath)));
        }

        private void BtnSimulator_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPage("simulator", NavSimulator, () => new SimulatorWindow());
        }

        private void BtnPushFold_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPage("push-fold", NavPushFold, () => new PushFoldWindow());
        }

        private void BtnSessions_Click(object sender, RoutedEventArgs e)
        {
            // Las dos clases de dinero: la pagina marca cada sesion como real o ficticio.
            NavigateToPage("sessions", NavSessions, () => new SessionsWindow(_allTables.ToList()));
            InfoText.Text = LocalizationManager.Text("Main.SessionsOpened");
        }

        private void BtnHeroProfile_Click(object sender, RoutedEventArgs e)
        {
            var hero = DashboardPlayers.FirstOrDefault();
            if (hero is null)
            {
                EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderHero");
                return;
            }

            if (!EnsureTablesLoaded(sender, e, "Common.StatusSelectFolderHero"))
                return;

            NavigateToPage("hero", NavHeroProfile, () => new HeroProfileWindow(hero, _allTables, DashboardStake, ""));
            InfoText.Text = string.Format(LocalizationManager.Text("Common.StatusHeroOpen"), hero.Name);
        }

        private void NavDashboard_Click(object sender, RoutedEventArgs e) => ShowDashboardPage();

        private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

        private void RecentTablesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as DataGrid)?.SelectedItem is TableSessionStats table)
                OpenDetailPage(new TableDetailWindow(table));
        }

        private void BtnRT_Click(object sender, RoutedEventArgs e)
        {
            // Si Tiempo real ya esta abierto se trae al frente: dos a la vez ponian dos HUD en cada mesa.
            var open = Application.Current.Windows.OfType<RealTimeWindow>().FirstOrDefault(window => window.IsVisible);
            if (open is not null)
            {
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }

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
            ResetPageCache();
            DashboardPlayers.Clear();
            RecentTables.Clear();
            _allTables.Clear();
            _handSeries = Array.Empty<HandPoint>();
            DashboardStake = StakeProfile.Low;
            UpdatePerformanceSummary();
            UpdatePlayerTags();
            InfoText.Text = string.Format(LocalizationManager.Text("Main.NoDataForRoom"), room);
        }

        private async void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow
            {
                Owner = this,
                ShowInTaskbar = false
            };

            _paletteChanged = false;
            var shown = window.ShowDialog();
            // El tamaño de la interfaz se ve al momento, aunque se cierre sin guardar la carpeta.
            ApplyInterfaceScale(AppSettingsService.Load().UiScale);
            if (shown == true)
            {
                RefreshLocalizedDashboardText();
                InfoText.Text = LocalizationManager.Text("Common.SettingsSaved");
                Toast.Show(InfoText.Text, ToastKind.Success);
            }
            else
            {
                RefreshLocalizedDashboardText();
            }

            // Si cambio el aspecto, las paginas se rehacen con los colores nuevos.
            if (_paletteChanged)
            {
                _paletteChanged = false;
                RebuildPagesAfterPaletteChange();
            }
        }

        private bool _paletteChanged;

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                this,
                LocalizationManager.Text("Main.LogoutConfirm"),
                LocalizationManager.Text("Main.LogoutTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // Antes se borraba la base local sin subir antes el respaldo: lo importado desde el
            // ultimo cierre de la app se perdia, aunque el aviso prometia recuperarlo desde Drive.
            // Ahora: 1) copia local de seguridad, 2) subida a Drive si esta activo, 3) borrado.
            IsEnabled = false;
            string? safetyCopy = null;
            string? uploadError = null;
            try
            {
                safetyCopy = await Task.Run(() => AphBackupDatabaseService.KeepSafetyCopy("antes_cerrar_sesion"));

                var syncSettings = AppSettingsService.Load();
                if (syncSettings.GoogleSyncEnabled &&
                    GoogleDriveBackupService.HasCredentialsFile &&
                    GoogleDriveBackupService.HasStoredToken &&
                    File.Exists(AphBackupDatabaseService.DatabasePath))
                {
                    InfoText.Text = LocalizationManager.Text("Settings.UploadingDrive");
                    var upload = await GoogleDriveBackupService.UploadDatabaseAsync();
                    if (!upload.Success)
                        uploadError = upload.Message;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Respaldo previo al cierre de sesion", ex);
                uploadError = ex.Message;
            }

            if (safetyCopy is null && uploadError is not null && File.Exists(AphBackupDatabaseService.DatabasePath))
            {
                // Sin copia local ni respaldo en Drive no se borra nada.
                IsEnabled = true;
                MessageBox.Show(
                    this,
                    string.Format(LocalizationManager.Text("Common.LoadFailed"), uploadError, AppLog.LogFolder),
                    "APH",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (uploadError is not null)
            {
                MessageBox.Show(
                    this,
                    string.Format(LocalizationManager.Text("Main.LogoutUploadFailed"), uploadError, safetyCopy),
                    "APH",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            try
            {
                _isClosingAfterSync = true;
                AphBackupDatabaseService.DeleteLocalDatabase();
                GoogleDriveBackupService.ClearStoredToken();

                var settings = AppSettingsService.Load();
                LocalAppLockService.ClearPassword(settings);
                settings.GoogleSyncEnabled = false;
                AppSettingsService.Save(settings);
            }
            catch (Exception ex)
            {
                IsEnabled = true;
                _isClosingAfterSync = false;
                AppLog.Error("No se pudo completar el cierre de sesion", ex);
                MessageBox.Show(
                    this,
                    $"No se pudo completar el log out: {ex.Message}",
                    "APH",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            Application.Current.Shutdown();
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

        private static Dictionary<string, string> GetConfiguredPokerRoomFolders(AppSettings settings)
        {
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (room, folder) in settings.PokerRoomFolders)
            {
                if (!string.IsNullOrWhiteSpace(room) &&
                    !string.IsNullOrWhiteSpace(folder) &&
                    Directory.Exists(folder))
                {
                    folders[room] = folder;
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                Directory.Exists(settings.PokerStarsHandHistoryFolder))
            {
                folders.TryAdd("PokerStars", settings.PokerStarsHandHistoryFolder);
            }

            return folders;
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

            // El boton es solo un icono: su nombre y su ayuda dicen a que modo cambia.
            var modeLabel = _isLiteMode ? "Modo pro" : LocalizationManager.Text("Main.RailLiteMode");
            LiteModeButton.Content = modeLabel;
            System.Windows.Automation.AutomationProperties.SetName(LiteModeButton, modeLabel);
        }

        private async Task AnalyzeAndLoadFolderAsync(string folder, string pokerRoom, string successMessage)
        {
            ShowLoading(true);
            try
            {
                await AnalyzeAndLoadFolderCoreAsync(folder, pokerRoom, successMessage);
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>La capa de "Cargando manos" con la pica: aparece al empezar y se desvanece al tener los datos.</summary>
        private void ShowLoading(bool show)
        {
            if (show)
            {
                LoadingOverlay.BeginAnimation(OpacityProperty, null);
                LoadingOverlay.Opacity = 0.94;
                LoadingOverlay.Visibility = Visibility.Visible;
                return;
            }

            if (LoadingOverlay.Visibility != Visibility.Visible)
                return;
            UiMotion.FadeOut(LoadingOverlay, 260, () =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.BeginAnimation(OpacityProperty, null);
                LoadingOverlay.Opacity = 0.94;
            });
        }

        private async Task AnalyzeAndLoadFolderCoreAsync(string folder, string pokerRoom, string successMessage)
        {
            ResetPageCache();
            DashboardPlayers.Clear();
            RecentTables.Clear();
            _allTables.Clear();
            _handSeries = Array.Empty<HandPoint>();
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

                // El panel arrancaba siempre en CASH: con un historial solo de dinero ficticio
                // mostraba 0 manos, 0 mesas y "Sin datos" aunque hubiera manos. Si el modo
                // actual no tiene mesas y el otro si, se cambia solo.
                var hasCurrentMode = _allTables.Any(t => t.IsCash == IsCashMode);
                var hasOtherMode = _allTables.Any(t => t.IsCash != IsCashMode);
                if (!hasCurrentMode && hasOtherMode && GameModeSwitch.Visibility == Visibility.Visible)
                    _isCashMode = !_isCashMode;
                OnPropertyChanged(nameof(IsCashMode));
                UpdatePlayerTags();

                RefreshFilteredData();
                // Los datos del panel ya estan: se quita la capa de carga y la grafica se completa despues.
                ShowLoading(false);

                // La grafica mano a mano (con la linea sin suerte) se calcula aparte: lee cada mano.
                var tablesForSeries = result.Tables.ToList();
                _handSeries = await Task.Run(() => BuildHandSeries(tablesForSeries));
                UpdateHeroSummary();
                _revealChart = true;
                DrawPerformanceChart();

                await Task.Run(() => VillainHistoryStore.RebuildFromTables(result.Tables));

                InfoText.Text = result.Tables.Count > 0
                    ? successMessage
                    : string.Format(LocalizationManager.Text("Common.NoHandsDetected"), pokerRoom);
                Toast.Show(InfoText.Text, result.Tables.Count > 0 ? ToastKind.Success : ToastKind.Warning);
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
            UpdateHeroSummary();
            var recent = RecentTables.ToList();
            if (recent.Count == 0)
            {
                BestRecentTableSummary = "-";
                BestRecentTableDetail = LocalizationManager.Text("Main.NoData");
                WorstRecentTableSummary = "-";
                WorstRecentTableDetail = LocalizationManager.Text("Main.NoData");
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

        /// <summary>
        /// Cada mano del heroe de cada mesa, en orden de hora: lo que gano o perdio y la suerte en un
        /// all-in con showdown antes del river (lo que cobro menos lo que le tocaba con su equity).
        /// </summary>
        // La proxima vez que se dibuje la grafica, sus lineas se dibujan de izquierda a derecha.
        private bool _revealChart = true;

        private static IReadOnlyList<HandPoint> BuildHandSeries(IReadOnlyList<TableSessionStats> tables)
        {
            var points = new List<HandPoint>();
            foreach (var table in tables)
            {
                if (!File.Exists(table.SourcePath) || table.BigBlind <= 0 || string.IsNullOrWhiteSpace(table.HeroName))
                    continue;
                try
                {
                    foreach (var hand in PokerStarsHandHistory.SplitHands(File.ReadLines(table.SourcePath)))
                    {
                        if (!PokerStarsHandHistory.TryGetDealtCards(hand, table.HeroName, out _))
                            continue;
                        var net = PokerStarsHandHistory.EstimateNetForPlayer(hand, table.HeroName) / table.BigBlind;
                        var luck = 0.0;
                        if (hand.Any(line => line.Contains("all-in", StringComparison.OrdinalIgnoreCase)) &&
                            hand.Any(line => line.StartsWith("*** SHOW", StringComparison.OrdinalIgnoreCase)) &&
                            Hud.App.Services.Trainer.TrainerHandParser.Parse(hand, table.HeroName) is { } parsed)
                            luck = HandTypeProfit.AllInLuckBb(parsed);
                        points.Add(new HandPoint(PokerStarsHandHistory.ExtractTimestamp(hand) ?? table.LastPlayedAt,
                            table.SourcePath, table.IsCash, net, luck));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Una mesa que no se puede leer ahora solo falta en la grafica mano a mano.
                }
            }

            return points.OrderBy(point => point.At).ToList();
        }

        /// <summary>Perfil y resultado en una frase, con el winrate sin la suerte de los all-in si ya se calculo.</summary>
        private void UpdateHeroSummary()
        {
            var hero = DashboardPlayers.FirstOrDefault();
            var tables = _allTables.Where(t => t.IsCash == _isCashMode).ToList();
            var hands = tables.Sum(t => t.HandsReceived);
            if (hero is null || hands == 0)
            {
                HeroSummarySentence = "";
                return;
            }

            var net = tables.Sum(t => t.NetBb);
            var profile = ClassifyProfile(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct, hero.AF);
            var points = _handSeries.Where(point => point.IsCash == _isCashMode).ToList();
            HeroSummarySentence = points.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Main.Summary"),
                    hero.VPIPPct, hero.PFRPct, profile, hands, net * 100 / hands, (net - points.Sum(point => point.LuckBb)) * 100 / hands)
                : string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Main.SummaryNoLuck"),
                    hero.VPIPPct, hero.PFRPct, profile, hands, net * 100 / hands);
        }

        private void UpdatePlayerTags()
        {
            HeroTags.Clear();

            var hero = DashboardPlayers.FirstOrDefault();
            if (hero is null || hero.HandsReceived == 0)
            {
                HeroTags.Add(BuildTag(LocalizationManager.Text("Main.NoData"), LocalizationManager.Text("Main.LoadHandsForTags"), Neutral()));
                return;
            }

            foreach (var tag in BuildHeroProfileTags(hero, !IsCashMode, DashboardStake).Take(8))
                HeroTags.Add(tag);
        }

        private static IEnumerable<DashboardTagViewModel> BuildHeroProfileTags(PlayerStats hero, bool playMoney, StakeProfile stake)
        {
            yield return BuildTag(
                ClassifyProfile(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct, hero.AF),
                LocalizationManager.Text("Tag.ProfileBaseHero.Desc"),
                Neutral());

            if (hero.HandsReceived < 30)
                yield return BuildTag(LocalizationManager.Text("Tag.NoSample"), LocalizationManager.Text("Tag.NoSample.Short"), Neutral());

            // Las mismas etiquetas que el perfil y el informe: salen de la retroalimentacion con semaforo.
            var feedback = HeroStatCoach.Evaluate(new HeroStatInput(hero.HandsReceived, hero.VPIPPct, hero.PFRPct, hero.ThreeBetPct,
                hero.AF, hero.AFqPct, hero.CBetFlopPct, hero.FoldVsCBetFlopPct, hero.WTSDPct, hero.WSDPct, hero.WWSFPct), playMoney, stake);
            foreach (var tag in HeroStatCoach.Tags(feedback))
                yield return BuildTag(tag.Text, tag.Description, tag.Verdict switch
                {
                    StatVerdict.Good => Accent(),
                    StatVerdict.Adjust => Warning(),
                    _ => Danger()
                });
        }

        private static string ClassifyProfile(int hands, double vpip, double pfr, double threeBet, double af) =>
            Hud.App.Services.PlayerProfileClassifier.Classify(hands, vpip, pfr, threeBet, af);

        private static DashboardTagViewModel BuildTag(string text, string description, TagPalette palette) =>
            new(text, description, palette.Background, palette.Border, palette.Foreground);

        private static TagPalette Accent() =>
            new(BrushFrom(16, 44, 32), BrushFrom(33, 192, 122), BrushFrom(177, 255, 214));

        private static TagPalette Danger() =>
            new(BrushFrom(55, 22, 30), BrushFrom(226, 78, 91), BrushFrom(255, 199, 204));

        private static TagPalette Warning() =>
            new(BrushFrom(56, 40, 12), BrushFrom(224, 160, 40), BrushFrom(255, 226, 170));

        private static TagPalette Neutral() =>
            new(Hud.App.Services.ThemeBrushes.NeutralBackground, Hud.App.Services.ThemeBrushes.NeutralBorder, Hud.App.Services.ThemeBrushes.Text);

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
                AddChartLabel(LocalizationManager.Text("Main.NoData"), PerformanceChart.ActualWidth / 2 - 24, PerformanceChart.ActualHeight / 2 - 8, Brushes.White);
                return;
            }

            // Con las manos ya leidas: grafica mano a mano con la linea sin suerte. Si no, por mesa.
            var handPoints = _handSeries.Where(point => point.IsCash == IsCashMode).ToList();
            if (handPoints.Count >= 2)
            {
                DrawHandChart(handPoints, orderedTables);
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

        /// <summary>
        /// Ganancia acumulada mano a mano: la linea continua es lo que se gano y la discontinua lo mismo
        /// sin la suerte de los all-in (si va por debajo, hubo buena suerte). Un punto al final de cada
        /// mesa con su resumen; el eje de abajo cuenta manos.
        /// </summary>
        private void DrawHandChart(IReadOnlyList<HandPoint> points, IReadOnlyList<TableSessionStats> orderedTables)
        {
            var real = new double[points.Count + 1];
            var adjusted = new double[points.Count + 1];
            for (var i = 0; i < points.Count; i++)
            {
                real[i + 1] = real[i] + points[i].NetBb;
                adjusted[i + 1] = adjusted[i] + points[i].NetBb - points[i].LuckBb;
            }

            var width = PerformanceChart.ActualWidth;
            var height = PerformanceChart.ActualHeight;
            const double padLeft = 46;
            const double padTop = 26;
            const double padRight = 14;
            const double padBottom = 20;
            var plotLeft = padLeft;
            var plotTop = padTop;
            var plotRight = Math.Max(plotLeft + 1, width - padRight);
            var plotBottom = Math.Max(plotTop + 1, height - padBottom);
            var min = Math.Min(0, Math.Min(real.Min(), adjusted.Min()));
            var max = Math.Max(0, Math.Max(real.Max(), adjusted.Max()));
            if (Math.Abs(max - min) < 0.001)
            {
                max += 1;
                min -= 1;
            }

            double X(int i) => plotLeft + (plotRight - plotLeft) * i / (real.Length - 1);
            double Y(double value) => plotBottom - (value - min) / (max - min) * (plotBottom - plotTop);

            var gridBrush = new SolidColorBrush(Color.FromArgb(70, 43, 61, 82));
            for (var i = 0; i <= 3; i++)
            {
                var y = plotTop + (plotBottom - plotTop) * i / 3;
                PerformanceChart.Children.Add(new Line { X1 = plotLeft, X2 = plotRight, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
                var value = max - (max - min) * i / 3;
                AddChartLabel(value.ToString("+0;-0;0", CultureInfo.CurrentCulture), 4, y - 7, Brushes.White);
            }

            var zeroY = Y(0);
            PerformanceChart.Children.Add(new Line
            {
                X1 = plotLeft, X2 = plotRight, Y1 = zeroY, Y2 = zeroY,
                Stroke = new SolidColorBrush(Color.FromArgb(140, 158, 173, 188)), StrokeThickness = 1
            });

            var positiveColor = GetThemeColor("Brush.Accent", Color.FromRgb(48, 217, 139));
            var negativeColor = GetThemeColor("Brush.Negative", Color.FromRgb(240, 93, 108));
            var chartColor = real[^1] >= 0 ? positiveColor : negativeColor;
            var adjustedColor = Color.FromRgb(120, 172, 235);

            var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(40, chartColor.R, chartColor.G, chartColor.B)) };
            area.Points.Add(new Point(X(0), zeroY));
            for (var i = 0; i < real.Length; i++)
                area.Points.Add(new Point(X(i), Y(real[i])));
            area.Points.Add(new Point(X(real.Length - 1), zeroY));
            PerformanceChart.Children.Add(area);

            var adjustedLine = new Polyline
            {
                Stroke = new SolidColorBrush(adjustedColor),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                StrokeLineJoin = PenLineJoin.Round
            };
            var realLine = new Polyline { Stroke = new SolidColorBrush(chartColor), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
            for (var i = 0; i < real.Length; i++)
            {
                adjustedLine.Points.Add(new Point(X(i), Y(adjusted[i])));
                realLine.Points.Add(new Point(X(i), Y(real[i])));
            }
            PerformanceChart.Children.Add(adjustedLine);
            PerformanceChart.Children.Add(realLine);

            // Recien cargadas las manos, las lineas se dibujan de izquierda a derecha y los puntos
            // de cada mesa aparecen al final. Al cambiar el tamaño de la ventana se redibuja sin animar.
            var reveal = _revealChart && width > plotLeft + 10;
            if (reveal)
            {
                _revealChart = false;
                UiMotion.Reveal(adjustedLine, plotLeft, width, height, 900);
                UiMotion.Reveal(realLine, plotLeft, width, height, 900);
            }

            // Un punto al final de cada mesa, con su resumen al pasar el raton.
            foreach (var table in orderedTables)
            {
                var last = -1;
                for (var i = points.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(points[i].SourcePath, table.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        last = i + 1;
                        break;
                    }
                }
                if (last < 0)
                    continue;

                var dot = new Ellipse
                {
                    Width = 8, Height = 8, Fill = Brushes.White,
                    Stroke = new SolidColorBrush(chartColor), StrokeThickness = 2, Cursor = Cursors.Hand,
                    ToolTip = new ToolTip
                    {
                        Content = BuildPerformanceChartToolTip(table, real[last], chartColor),
                        Style = TryFindResource("ChartToolTipStyle") as Style
                    }
                };
                ToolTipService.SetInitialShowDelay(dot, 0);
                ToolTipService.SetBetweenShowDelay(dot, 0);
                Canvas.SetLeft(dot, X(last) - 4);
                Canvas.SetTop(dot, Y(real[last]) - 4);
                PerformanceChart.Children.Add(dot);
                if (reveal)
                    UiMotion.FadeIn(dot, 200, 120 + 760 * (X(last) - plotLeft) / Math.Max(1, plotRight - plotLeft));
            }

            // Eje de abajo: manos.
            var total = points.Count;
            foreach (var (index, align) in new[] { (0, 0.0), (total / 2, 0.5), (total, 1.0) })
                AddChartLabel(string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Main.ChartHands"), index),
                    Math.Clamp(X(index) - 40 * align, plotLeft, plotRight - 50), plotBottom + 3, Brushes.White);

            // Leyenda: que es cada linea, con lo que vale al final.
            void Legend(double left, Color color, bool dashed, string text)
            {
                PerformanceChart.Children.Add(new Line
                {
                    X1 = left, X2 = left + 18, Y1 = 11, Y2 = 11, Stroke = new SolidColorBrush(color), StrokeThickness = 2.5,
                    StrokeDashArray = dashed ? new DoubleCollection { 3, 2 } : null
                });
                AddChartLabel(text, left + 22, 4, Brushes.White);
            }
            Legend(plotLeft, chartColor, false,
                string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Main.ChartReal"), real[^1].ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
            Legend(plotLeft + 170, adjustedColor, true,
                string.Format(CultureInfo.CurrentCulture, LocalizationManager.Text("Main.ChartAdjusted"), adjusted[^1].ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
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

            var result = BuildTooltipMetric(LocalizationManager.Text("Gain.TooltipResult"), $"{(table.NetBb >= 0 ? "+" : "")}{table.NetBb:0.#} bb", borderBrush);
            var total = BuildTooltipMetric(LocalizationManager.Text("Gain.TooltipTotal"), $"{cumulativeBb:0.#} bb", new SolidColorBrush(chartColor));
            Grid.SetColumn(total, 1);
            metrics.Children.Add(result);
            metrics.Children.Add(total);
            root.Children.Add(metrics);

            var hands = new TextBlock
            {
                Text = string.Format(LocalizationManager.Text("Gain.TooltipHands"), table.HandsReceived),
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
            // Antes solo contaba como ganada una mesa de 100 bb o mas: una mesa de +54 bb salia con
            // la flecha roja hacia abajo, como si se hubiera perdido.
            public bool IsWin => NetBb > 0;
            public bool IsProfitable => NetBb >= 0;
            public string TrendIcon => IsWin ? "\u25B2" : "\u25BC";
        }
    }
}


