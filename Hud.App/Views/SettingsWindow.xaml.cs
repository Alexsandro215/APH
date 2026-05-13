using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

            LanguageCombo.ItemsSource = LocalizationManager.Languages;
            LanguageCombo.SelectedItem = string.IsNullOrWhiteSpace(_settings.Language)
                ? LocalizationManager.NormalizeLanguage(null)
                : LocalizationManager.NormalizeLanguage(_settings.Language);

            var previews = ThemePaletteManager.Palettes.Select(PalettePreview.FromPalette).ToList();
            foreach (var p in previews) p.IsSelected = p.Key == _settings.Palette;
            PalettePreviewList.ItemsSource = previews;

            ReportsFolderText.Text = string.IsNullOrWhiteSpace(_settings.ReportsFolder)
                ? ReportSessionIndexService.DefaultReportsFolder
                : _settings.ReportsFolder;
            ProtectReportsCheck.IsChecked = _settings.ProtectReportsWithPassword;
            ReportPasswordStatus.Text = ReportSecurityService.HasPassword(_settings)
                ? "Contrasena de reportes configurada."
                : "Sin contrasena de reportes configurada.";
            GoogleAccountText.Text = string.IsNullOrWhiteSpace(_settings.GoogleAccountEmail)
                ? "Sin cuenta detectada"
                : _settings.GoogleAccountEmail;
            GoogleDriveStatus.Text = GoogleDriveBackupService.HasCredentialsFile
                ? "Drive listo. Subir respaldo guarda aph.db en la cuenta conectada."
                : $"Falta google_client_secret.json en {GoogleDriveBackupService.CredentialsFolder}.";

            LanguageCombo.SelectionChanged += (_, _) =>
            {
                if (LanguageCombo.SelectedItem is string language)
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

        private async void BtnGoogleUpload_Click(object sender, RoutedEventArgs e)
        {
            await RunGoogleDriveActionAsync("Subiendo aph.db a Google Drive...", GoogleDriveBackupService.UploadDatabaseAsync);
        }

        private async void BtnGoogleRestore_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                this,
                "Esto reemplazara la aph.db local con la copia de Google Drive. Se guardara un .bak antes de reemplazar. ¿Continuar?",
                "Restaurar respaldo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            await RunGoogleDriveActionAsync("Restaurando aph.db desde Google Drive...", GoogleDriveBackupService.RestoreDatabaseAsync);
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
                    AppSettingsService.Save(_settings);
                }

                GoogleDriveStatus.Text = result.Message;
            }
            catch (FileNotFoundException ex)
            {
                GoogleDriveStatus.Text = $"{ex.Message}. Crea OAuth Desktop en Google Cloud y guarda el JSON ahi.";
            }
            catch (Exception ex)
            {
                GoogleDriveStatus.Text = $"Google Drive fallo: {ex.Message}";
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
                Title = "Selecciona carpeta de reportes"
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
                StatusText.Text = "Carpeta de reportes guardada.";
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
                ReportPasswordStatus.Text = "Primero guarda una contrasena nueva para reportes.";
                return;
            }

            if (!AskCurrentReportPassword())
            {
                ProtectReportsCheck.IsChecked = currentState;
                ReportPasswordStatus.Text = "Cambio cancelado o contrasena incorrecta.";
                return;
            }

            _settings.ProtectReportsWithPassword = requestedState;
            AppSettingsService.Save(_settings);
            ProtectReportsCheck.IsChecked = requestedState;
            ReportPasswordStatus.Text = requestedState
                ? "Proteccion de reportes habilitada."
                : "Proteccion de reportes deshabilitada.";
        }

        private void BtnSaveReportPassword_Click(object sender, RoutedEventArgs e)
        {
            var hasPassword = ReportSecurityService.HasPassword(_settings);
            if (hasPassword && !ReportSecurityService.VerifyPassword(_settings, OldPasswordBox.Password))
            {
                ReportPasswordStatus.Text = "La contrasena actual no coincide.";
                return;
            }

            if (string.IsNullOrWhiteSpace(NewPasswordBox.Password))
            {
                if (ProtectReportsCheck.IsChecked == true)
                {
                    ReportPasswordStatus.Text = "Escribe una nueva contrasena.";
                    return;
                }

                ReportSecurityService.ClearPassword(_settings);
                SyncSettingsFromControls();
                AppSettingsService.Save(_settings);
                ReportPasswordStatus.Text = "Proteccion de reportes desactivada.";
                return;
            }

            if (NewPasswordBox.Password.Length < 8)
            {
                ReportPasswordStatus.Text = "Usa minimo 8 caracteres.";
                return;
            }

            if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
            {
                ReportPasswordStatus.Text = "La confirmacion no coincide.";
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
            ReportPasswordStatus.Text = "Contrasena de reportes guardada de forma codificada.";
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
                
                // Actualizar seleccion visual
                if (PalettePreviewList.ItemsSource is List<PalettePreview> previews)
                {
                    foreach (var p in previews) p.IsSelected = p.Key == key;
                    PalettePreviewList.Items.Refresh();
                }

                StatusText.Text = string.Format(LocalizationManager.Text("Common.PreviewApplied"), ThemePaletteManager.Get(key).Name);
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
            _settings.Language = LocalizationManager.NormalizeLanguage(LanguageCombo.SelectedItem?.ToString());
            _settings.Palette = _settings.Palette ?? ThemePaletteManager.DefaultPaletteKey;
        }

        private sealed class PalettePreview
        {
            public bool IsSelected { get; set; }
            public required string Key { get; init; }
            public required string Name { get; init; }
            public required string Description { get; init; }
            public required Brush CardBrush { get; init; }
            public required Brush BorderBrush { get; init; }
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
                    CardBrush = new SolidColorBrush(palette.Card),
                    BorderBrush = new SolidColorBrush(palette.Border),
                    AccentBrush = new SolidColorBrush(palette.Accent),
                    SecondaryBrush = new SolidColorBrush(palette.Secondary),
                    DangerBrush = new SolidColorBrush(palette.Danger),
                    GoldBrush = new SolidColorBrush(palette.Gold)
                };
        }
    }
}


