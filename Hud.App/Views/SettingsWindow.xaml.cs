using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hud.App.Services;
using Microsoft.Win32;

namespace Hud.App.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        public SettingsWindow()
        {
            InitializeComponent();

            _settings = AppSettingsService.Load();

            // El idioma se guarda con su clave interna ("Espanol", "Francais") pero se muestra con su nombre.
            LanguageCombo.ItemsSource = LocalizationManager.Languages.Select(LanguageOption.From).ToList();
            LanguageCombo.SelectedValue = string.IsNullOrWhiteSpace(_settings.Language)
                ? LocalizationManager.NormalizeLanguage(null)
                : LocalizationManager.NormalizeLanguage(_settings.Language);

            AskRealTimeCheck.IsChecked = _settings.AskToStartRealTime;

            // Apariencia: animaciones y tamaño de la interfaz.
            var motion = UiMotion.Parse(_settings.AnimationLevel);
            (motion switch
            {
                MotionLevel.Reduced => MotionReduced,
                MotionLevel.Off => MotionOff,
                _ => MotionNormal
            }).IsChecked = true;
            var scale = AppSettings.NormalizeUiScale(_settings.UiScale);
            foreach (var choice in ScaleChoices.Children.OfType<RadioButton>())
                choice.IsChecked = Math.Abs(ParseTag(choice.Tag) - scale) < 0.001;

            var previews = ThemePaletteManager.Palettes.Select(PalettePreview.FromPalette).ToList();
            foreach (var p in previews) p.IsSelected = p.Key == _settings.Palette;
            PalettePreviewList.ItemsSource = previews;

            ReportsFolderText.Text = string.IsNullOrWhiteSpace(_settings.ReportsFolder)
                ? ReportSessionIndexService.DefaultReportsFolder
                : _settings.ReportsFolder;
            ProtectReportsCheck.IsChecked = _settings.ProtectReportsWithPassword;
            ReportPasswordStatus.Text = ReportSecurityService.HasPassword(_settings)
                ? LocalizationManager.Text("Settings.ReportPasswordConfigured")
                : LocalizationManager.Text("Settings.ReportPasswordMissing");
            GoogleAccountText.Text = string.IsNullOrWhiteSpace(_settings.GoogleAccountEmail)
                ? LocalizationManager.Text("Settings.NoAccountDetected")
                : _settings.GoogleAccountEmail;
            GoogleDriveStatus.Text = GoogleDriveBackupService.HasCredentialsFile
                ? LocalizationManager.Text("Settings.DriveReady")
                : string.Format(LocalizationManager.Text("Login.MissingClientSecret"), GoogleDriveBackupService.CredentialsFolder);

            // Se abre arriba, en Apariencia. Sin esto el foco caia en un campo de mas abajo y la
            // ventana aparecia desplazada hasta el final.
            Loaded += (_, _) =>
            {
                TabAppearance.Focus();
                Dispatcher.BeginInvoke(() => SectionScroll.ScrollToTop(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };

            LanguageCombo.SelectionChanged += (_, _) =>
            {
                if (LanguageCombo.SelectedValue is string language)
                {
                    SyncSettingsFromControls();
                    _settings.Language = LocalizationManager.NormalizeLanguage(language);
                    AppSettingsService.Save(_settings);
                    LocalizationManager.Apply(language);
                    StatusText.Text = LocalizationManager.Text("Settings.Status");
                }
            };
        }

        public AppSettings SavedSettings => _settings;

        // ---------------------------------------------------------------- secciones

        /// <summary>Muestra la seccion elegida en el indice; entra con un fundido.</summary>
        private void SectionTab_Checked(object sender, RoutedEventArgs e)
        {
            if (SectionAppearance is null)
                return; // durante InitializeComponent

            var show = sender == TabLanguage ? SectionLanguage
                : sender == TabRealTime ? SectionRealTime
                : sender == TabReports ? SectionReports
                : sender == TabSync ? SectionSync
                : SectionAppearance;
            foreach (var section in new FrameworkElement[] { SectionAppearance, SectionLanguage, SectionRealTime, SectionReports, SectionSync })
                section.Visibility = section == show ? Visibility.Visible : Visibility.Collapsed;
            SectionScroll.ScrollToTop();
            UiMotion.EnterPage(SectionHost);
        }

        // ---------------------------------------------------------------- apariencia

        private void Motion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string key } choice)
                return;

            UiMotion.Level = UiMotion.Parse(key);
            _settings.AnimationLevel = UiMotion.Level.ToString();
            AppSettingsService.Save(_settings);
            ShowStatus(string.Format(LocalizationManager.Text("Settings.MotionSaved"), ChoiceTitle(choice)));
            // Una muestra del nivel elegido: los temas se reparten de nuevo.
            UiMotion.Stagger(FindWrapPanel(PalettePreviewList), 18, 24);
        }

        /// <summary>Preguntar o no si se arranca Tiempo real al ver mesas abiertas (se guarda al instante).</summary>
        private void AskRealTimeCheck_Click(object sender, RoutedEventArgs e)
        {
            _settings.AskToStartRealTime = AskRealTimeCheck.IsChecked == true;
            AppSettingsService.Save(_settings);
            ShowStatus(LocalizationManager.Text(_settings.AskToStartRealTime ? "Settings.RtAskOn" : "Settings.RtAskOff"));
        }

        private void Scale_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton choice)
                return;

            _settings.UiScale = AppSettings.NormalizeUiScale(ParseTag(choice.Tag));
            AppSettingsService.Save(_settings);
            if (Owner is MainWindow main)
                main.ApplyInterfaceScale(_settings.UiScale);
            ShowStatus(string.Format(LocalizationManager.Text("Settings.UiScaleSaved"),
                $"{ChoiceTitle(choice)} ({_settings.UiScale.ToString("P0", CultureInfo.CurrentCulture)})"));
        }

        private static double ParseTag(object? tag) =>
            double.TryParse(tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 1.0;

        private static string ChoiceTitle(RadioButton choice) =>
            System.Windows.Automation.AutomationProperties.GetName(choice);

        private static Panel? FindWrapPanel(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is WrapPanel panel)
                    return panel;
                if (FindWrapPanel(child) is { } nested)
                    return nested;
            }
            return null;
        }

        /// <summary>El aviso del pie cambia con un fundido para que se note.</summary>
        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            UiMotion.FadeIn(StatusText, 220);
        }

        private async void BtnGoogleUpload_Click(object sender, RoutedEventArgs e)
        {
            await RunGoogleDriveActionAsync(LocalizationManager.Text("Settings.UploadingDrive"), GoogleDriveBackupService.UploadDatabaseAsync);
        }

        private async void BtnGoogleRestore_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                LocalizationManager.Text("Settings.RestoreConfirm"),
                LocalizationManager.Text("Settings.RestoreTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            await RunGoogleDriveActionAsync(LocalizationManager.Text("Settings.RestoringDrive"), GoogleDriveBackupService.RestoreDatabaseAsync);
        }

        private async Task RunGoogleDriveActionAsync(
            string pendingMessage,
            Func<CancellationToken, Task<GoogleDriveBackupResult>> action)
        {
            SetGoogleDriveButtonsEnabled(false);
            GoogleDriveStatus.Text = pendingMessage;

            try
            {
                var result = await action(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(result.AccountEmail))
                {
                    _settings.GoogleAccountEmail = result.AccountEmail;
                    GoogleAccountText.Text = result.AccountEmail;

                    // Quien entro en modo local y conecta Drive ahora tambien quiere el
                    // respaldo automatico al cerrar.
                    if (result.Success)
                        _settings.GoogleSyncEnabled = true;

                    AppSettingsService.Save(_settings);
                }

                GoogleDriveStatus.Text = result.Message;
            }
            catch (FileNotFoundException ex)
            {
                GoogleDriveStatus.Text = string.Format(LocalizationManager.Text("Settings.GoogleOAuthHint"), ex.Message);
            }
            catch (Exception ex)
            {
                GoogleDriveStatus.Text = string.Format(LocalizationManager.Text("Settings.GoogleDriveFailed"), ex.Message);
            }
            finally
            {
                SetGoogleDriveButtonsEnabled(true);
            }
        }

        private void SetGoogleDriveButtonsEnabled(bool isEnabled)
        {
            BtnGoogleUpload.IsEnabled = isEnabled;
            BtnGoogleRestore.IsEnabled = isEnabled;
        }

        private void BtnPickReportsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationManager.Text("Settings.SelectReportsFolder")
            };

            if (!string.IsNullOrWhiteSpace(ReportsFolderText.Text) &&
                Directory.Exists(ReportsFolderText.Text))
            {
                dlg.InitialDirectory = ReportsFolderText.Text;
            }

            if (dlg.ShowDialog(this) == true)
            {
                ReportsFolderText.Text = dlg.FolderName;
                SyncSettingsFromControls();
                AppSettingsService.Save(_settings);
                ShowStatus(LocalizationManager.Text("Settings.ReportsFolderSaved"));
            }
        }

        private void ProtectReportsCheck_Click(object sender, RoutedEventArgs e)
        {
            var requestedState = ProtectReportsCheck.IsChecked == true;
            var currentState = _settings.ProtectReportsWithPassword && ReportSecurityService.HasPassword(_settings);

            if (requestedState == currentState)
                return;

            if (!ReportSecurityService.HasPassword(_settings))
            {
                ProtectReportsCheck.IsChecked = false;
                ReportPasswordStatus.Text = LocalizationManager.Text("Settings.SavePasswordFirst");
                return;
            }

            if (!AskCurrentReportPassword())
            {
                ProtectReportsCheck.IsChecked = currentState;
                ReportPasswordStatus.Text = LocalizationManager.Text("Settings.ChangeCanceled");
                return;
            }

            _settings.ProtectReportsWithPassword = requestedState;
            AppSettingsService.Save(_settings);
            ProtectReportsCheck.IsChecked = requestedState;
            ReportPasswordStatus.Text = requestedState
                ? LocalizationManager.Text("Settings.ReportsEnabled")
                : LocalizationManager.Text("Settings.ReportsDisabled");
        }

        private void BtnSaveReportPassword_Click(object sender, RoutedEventArgs e)
        {
            var hasPassword = ReportSecurityService.HasPassword(_settings);
            if (hasPassword && !ReportSecurityService.VerifyPassword(_settings, OldPasswordBox.Password))
            {
                ReportPasswordStatus.Text = LocalizationManager.Text("Settings.CurrentPasswordMismatch");
                return;
            }

            if (string.IsNullOrWhiteSpace(NewPasswordBox.Password))
            {
                if (ProtectReportsCheck.IsChecked == true)
                {
                    ReportPasswordStatus.Text = LocalizationManager.Text("Settings.WriteNewPassword");
                    return;
                }

                ReportSecurityService.ClearPassword(_settings);
                SyncSettingsFromControls();
                AppSettingsService.Save(_settings);
                ReportPasswordStatus.Text = LocalizationManager.Text("Settings.ReportProtectionOff");
                return;
            }

            if (NewPasswordBox.Password.Length < 8)
            {
                ReportPasswordStatus.Text = LocalizationManager.Text("Login.MinPassword");
                return;
            }

            if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
            {
                ReportPasswordStatus.Text = LocalizationManager.Text("Login.ConfirmMismatch");
                return;
            }

            ReportSecurityService.SetPassword(_settings, NewPasswordBox.Password);
            SyncSettingsFromControls();
            _settings.ProtectReportsWithPassword = true;
            AppSettingsService.Save(_settings);
            OldPasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            ProtectReportsCheck.IsChecked = true;
            ReportPasswordStatus.Text = LocalizationManager.Text("Settings.ReportPasswordSaved");
        }

        private bool AskCurrentReportPassword()
        {
            var prompt = new ReportPasswordPromptWindow
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true)
                return false;

            return ReportSecurityService.VerifyPassword(_settings, prompt.Password);
        }

        private void BtnPalette_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string key)
            {
                _settings.Palette = key;
                ThemePaletteManager.Apply(key);
                // El tema se guarda al instante, como el idioma.
                SyncSettingsFromControls();
                AppSettingsService.Save(_settings);

                // La marca pasa al tema elegido sin rehacer la cuadricula.
                if (PalettePreviewList.ItemsSource is List<PalettePreview> previews)
                {
                    foreach (var p in previews) p.IsSelected = p.Key == key;
                }

                ShowStatus(string.Format(LocalizationManager.Text("Common.PreviewApplied"), ThemePaletteManager.Get(key).Name));
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromControls();

            AppSettingsService.Save(_settings);
            ThemePaletteManager.Apply(_settings.Palette);
            LocalizationManager.Apply(_settings.Language);
            DialogResult = true;
        }

        private void SyncSettingsFromControls()
        {
            _settings.ReportsFolder = string.IsNullOrWhiteSpace(ReportsFolderText.Text)
                ? null
                : ReportsFolderText.Text.Trim();
            _settings.ProtectReportsWithPassword = ProtectReportsCheck.IsChecked == true && ReportSecurityService.HasPassword(_settings);
            _settings.Language = LocalizationManager.NormalizeLanguage(LanguageCombo.SelectedValue?.ToString());
            _settings.Palette = _settings.Palette ?? ThemePaletteManager.DefaultPaletteKey;
        }

        /// <summary>Idioma en la lista: clave interna y nombre que se ve.</summary>
        private sealed record LanguageOption(string Key, string Display)
        {
            public static LanguageOption From(string key) => new(key, key switch
            {
                "Espanol" => "Español",
                "Francais" => "Français",
                "Portugues" => "Português",
                _ => key
            });
        }

        /// <summary>Tarjeta de un tema: sus propios colores, para verlo antes de elegirlo.</summary>
        private sealed class PalettePreview : INotifyPropertyChanged
        {
            private bool _isSelected;

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public required string Key { get; init; }
            public required string Name { get; init; }
            public required string Description { get; init; }
            public required Brush BackgroundBrush { get; init; }
            public required Brush PanelBrush { get; init; }
            public required Brush PreviewBrush { get; init; }
            public required Brush CardBrush { get; init; }
            public required Brush BorderBrush { get; init; }
            public required Brush TextBrush { get; init; }
            public required Brush TextDimBrush { get; init; }
            public required Brush AccentBrush { get; init; }
            public required Brush SecondaryBrush { get; init; }
            public required Brush DangerBrush { get; init; }
            public required Brush GoldBrush { get; init; }

            public static PalettePreview FromPalette(ThemePalette palette) =>
                new()
                {
                    Key = palette.Key,
                    Name = palette.Name,
                    Description = palette.Description,
                    BackgroundBrush = Frozen(new SolidColorBrush(palette.Background)),
                    PanelBrush = Frozen(new SolidColorBrush(palette.Panel)),
                    PreviewBrush = Frozen(new LinearGradientBrush(palette.Background, palette.Card, 35)),
                    CardBrush = Frozen(new SolidColorBrush(palette.Card)),
                    BorderBrush = Frozen(new SolidColorBrush(palette.Border)),
                    TextBrush = Frozen(new SolidColorBrush(palette.Text)),
                    TextDimBrush = Frozen(new SolidColorBrush(palette.TextDim)),
                    AccentBrush = Frozen(new SolidColorBrush(palette.Accent)),
                    SecondaryBrush = Frozen(new SolidColorBrush(palette.Secondary)),
                    DangerBrush = Frozen(new SolidColorBrush(palette.Danger)),
                    GoldBrush = Frozen(new SolidColorBrush(palette.Gold))
                };

            private static Brush Frozen(Brush brush)
            {
                brush.Freeze();
                return brush;
            }
        }
    }
}


