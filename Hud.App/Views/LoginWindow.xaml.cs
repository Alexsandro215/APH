using System.IO;
using System.Windows;
using System.Windows.Input;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class LoginWindow : Window
    {
        private readonly AppSettings _settings;
        private bool _googleReady;
        private readonly bool _hasLocalPassword;

        public LoginWindow()
        {
            InitializeComponent();
            _settings = AppSettingsService.Load();
            _hasLocalPassword = LocalAppLockService.HasPassword(_settings);

            ConfirmPasswordPanel.Visibility = _hasLocalPassword ? Visibility.Collapsed : Visibility.Visible;
            ContinueButton.Content = _hasLocalPassword
                ? LocalizationManager.Text("Login.Enter")
                : LocalizationManager.Text("Login.CreatePasswordEnter");
            PasswordTitleText.Text = _hasLocalPassword
                ? LocalizationManager.Text("Login.UnlockStep")
                : LocalizationManager.Text("Login.CreateLockStep");
            PasswordHelpText.Text = _hasLocalPassword
                ? LocalizationManager.Text("Login.GoogleValidatedHelp")
                : LocalizationManager.Text("Login.LocalPasswordHelp");
            GoogleStatusText.Text = GoogleDriveBackupService.HasCredentialsFile
                ? LocalizationManager.Text("Login.GoogleReady")
                : string.Format(LocalizationManager.Text("Login.MissingClientSecretOptional"), GoogleDriveBackupService.CredentialsFolder);

            // El respaldo en Drive es opcional: la puerta de acceso es la contrasena local,
            // asi que el paso 2 esta disponible desde el principio.
            ConfirmPasswordBox.IsEnabled = !_hasLocalPassword;
            Loaded += (_, _) => PasswordBox.Focus();
        }

        private async void GoogleButton_Click(object sender, RoutedEventArgs e)
        {
            GoogleButton.IsEnabled = false;
            GoogleStatusText.Text = LocalizationManager.Text("Login.OpeningGoogle");

            try
            {
                _accountChangeNote = null;
                var result = await GoogleDriveBackupService.ConnectAsync();
                var accepted = ApplyGoogleAccount(result);
                _googleReady = result.Success && accepted;

                // Antes el aviso de "cuenta cambiada, base borrada" se sobrescribia al instante
                // con el mensaje de conexion y el usuario nunca lo veia.
                GoogleStatusText.Text = !accepted
                    ? LocalizationManager.Text("Login.AccountChangeCancelled")
                    : _accountChangeNote ?? result.Message;
                if (!accepted)
                    GoogleButton.IsEnabled = true;
                PasswordBox.Focus();
            }
            catch (FileNotFoundException ex)
            {
                GoogleStatusText.Text = ex.Message;
                GoogleButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                GoogleStatusText.Text = string.Format(LocalizationManager.Text("Login.GoogleFailed"), ex.Message);
                GoogleButton.IsEnabled = true;
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasLocalPassword)
            {
                if (!LocalAppLockService.VerifyPassword(_settings, PasswordBox.Password))
                {
                    PasswordStatusText.Text = LocalizationManager.Text("Login.BadLocalPassword");
                    PasswordBox.SelectAll();
                    PasswordBox.Focus();
                    return;
                }

                DialogResult = true;
                return;
            }

            if (PasswordBox.Password.Length < 8)
            {
                PasswordStatusText.Text = LocalizationManager.Text("Login.MinPassword");
                return;
            }

            if (PasswordBox.Password != ConfirmPasswordBox.Password)
            {
                PasswordStatusText.Text = LocalizationManager.Text("Login.ConfirmMismatch");
                return;
            }

            LocalAppLockService.SetPassword(_settings, PasswordBox.Password);

            // Solo se activa la sincronizacion si de verdad hay sesion de Google; si no,
            // el cierre de la app intentaria subir a Drive sin credenciales.
            _settings.GoogleSyncEnabled = _googleReady;
            AppSettingsService.Save(_settings);
            DialogResult = true;
        }

        private string? _accountChangeNote;

        // Devuelve false si el usuario no acepta cambiar de cuenta.
        private bool ApplyGoogleAccount(GoogleDriveBackupResult result)
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.AccountEmail))
                return true;

            var previousEmail = _settings.GoogleAccountEmail;
            if (!string.IsNullOrWhiteSpace(previousEmail) &&
                !string.Equals(previousEmail, result.AccountEmail, StringComparison.OrdinalIgnoreCase))
            {
                // Antes se borraba la base local sin preguntar: un clic en la cuenta equivocada
                // eliminaba todo el historial de manos de esta PC.
                var answer = MessageBox.Show(
                    this,
                    string.Format(LocalizationManager.Text("Login.AccountChangeConfirm"), previousEmail, result.AccountEmail),
                    "APH",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes)
                {
                    // El token guardado ya es el de la otra cuenta: se olvida para que los
                    // respaldos no vayan a parar a un Drive ajeno.
                    GoogleDriveBackupService.ClearStoredToken();
                    return false;
                }

                string? safetyCopy = null;
                try
                {
                    safetyCopy = AphBackupDatabaseService.KeepSafetyCopy("antes_cambio_cuenta");
                }
                catch (Exception ex)
                {
                    AppLog.Error("No se pudo guardar la copia antes del cambio de cuenta", ex);
                    MessageBox.Show(
                        this,
                        string.Format(LocalizationManager.Text("Common.LoadFailed"), ex.Message, AppLog.LogFolder),
                        "APH",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    GoogleDriveBackupService.ClearStoredToken();
                    return false;
                }

                AphBackupDatabaseService.DeleteLocalDatabase();
                _accountChangeNote = string.Format(LocalizationManager.Text("Login.AccountChanged"), previousEmail, result.AccountEmail);
                if (safetyCopy is not null)
                    _accountChangeNote += " " + string.Format(LocalizationManager.Text("Login.SafetyCopyKept"), safetyCopy);
            }

            _settings.GoogleAccountEmail = result.AccountEmail;
            AppSettingsService.Save(_settings);
            return true;
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ContinueButton.IsEnabled)
                ContinueButton_Click(sender, e);
        }
    }
}
