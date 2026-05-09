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
using HandReader.Core.Models;
using HandReader.Core.Parsing;
using HandReader.Core.Stats;
using Hud.App.Services;
using Hud.App.Views;
using Microsoft.Win32;

namespace Hud.App
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private StakeProfile _dashboardStake = StakeProfile.Low;

        public ObservableCollection<PlayerStats> DashboardPlayers { get; } = new();
        public ObservableCollection<TableSessionStats> RecentTables { get; } = new();

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
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            var settings = AppSettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                Directory.Exists(settings.PokerStarsHandHistoryFolder))
            {
                await AnalyzeAndLoadFolderAsync(settings.PokerStarsHandHistoryFolder, LocalizationManager.Text("Common.DefaultFolderLoaded"));
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

            var window = new GlobalAnalysisWindow(RecentTables, "")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
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

            var window = new MisTablasWindow(RecentTables, "")
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = LocalizationManager.Text("Common.StatusTablesOpen");
        }

        private void BtnBestWorst_Click(object sender, RoutedEventArgs e)
        {
            if (RecentTables.Count == 0)
            {
                InfoText.Text = LocalizationManager.Text("Common.StatusSelectFolderVillains");
                BtnPickFolder_Click(sender, e);
                return;
            }

            var window = new DataVillainsWindow(RecentTables, "")
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

            var window = new LeakFinderWindow(RecentTables, "")
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

            var window = new HeroProfileWindow(hero, RecentTables, DashboardStake, "")
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
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationManager.Text("Common.SelectHandFolderTitle")
            };

            var settings = AppSettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.PokerStarsHandHistoryFolder) &&
                Directory.Exists(settings.PokerStarsHandHistoryFolder))
            {
                dlg.InitialDirectory = settings.PokerStarsHandHistoryFolder;
            }

            if (dlg.ShowDialog(this) != true)
                return;

            var folder = dlg.FolderName;
            settings.PokerStarsHandHistoryFolder = folder;
            AppSettingsService.Save(settings);

            await AnalyzeAndLoadFolderAsync(folder, "Dashboard global actualizado.");
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
                var folder = window.SavedSettings.PokerStarsHandHistoryFolder;
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    await AnalyzeAndLoadFolderAsync(folder, "Configuracion guardada y carpeta cargada.");
                else
                    InfoText.Text = LocalizationManager.Text("Common.SettingsSaved");
            }
        }

        private async Task AnalyzeAndLoadFolderAsync(string folder, string successMessage)
        {
            DashboardPlayers.Clear();
            RecentTables.Clear();

            try
            {
                var result = await Task.Run(() => AnalyzeFolder(folder));

                DashboardStake = result.Stake;
                if (result.Hero is not null)
                    DashboardPlayers.Add(result.Hero);
                foreach (var table in result.Tables)
                    RecentTables.Add(table);

                await Task.Run(() => VillainHistoryStore.RebuildFromTables(result.Tables));

                InfoText.Text = successMessage;
            }
            catch (Exception ex)
            {
                InfoText.Text = LocalizationManager.Text("Common.FolderAnalysisFailed");
                MessageBox.Show(ex.Message, LocalizationManager.Text("Common.AnalyzeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private sealed record DashboardResult(int FileCount, StakeProfile Stake, PlayerStats? Hero, List<TableSessionStats> Tables);

        public sealed record TableSessionStats(
            string TableName,
            string GameFormat,
            string PlayedDate,
            DateTime LastPlayedAt,
            string SourcePath,
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


