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
            ContinueButton.Content = _hasLocalPassword ? "APH" : "Crear contrasena y entrar";
            PasswordTitleText.Text = _hasLocalPassword ? "2. Desbloquea esta PC" : "2. Crea bloqueo local";
            PasswordHelpText.Text = _hasLocalPassword
                ? "Google ya queda validado; escribe la contrasena local de esta PC."
                : "Esta contrasena protege APH si alguien abre tu computadora.";
            GoogleStatusText.Text = GoogleDriveBackupService.HasCredentialsFile
                ? "Listo para conectar con Google."
                : $"Falta google_client_secret.json en {GoogleDriveBackupService.CredentialsFolder}.";
        }

        private async void GoogleButton_Click(object sender, RoutedEventArgs e)
        {
            GoogleButton.IsEnabled = false;
            GoogleStatusText.Text = "Abriendo autorizacion de Google...";

            try
            {
                var result = await GoogleDriveBackupService.ConnectAsync();
                ApplyGoogleAccount(result);
                _googleReady = result.Success;
                GoogleStatusText.Text = result.Message;
                EnablePasswordStep();
            }
            catch (FileNotFoundException ex)
            {
                GoogleStatusText.Text = ex.Message;
                GoogleButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                GoogleStatusText.Text = $"Google fallo: {ex.Message}";
                GoogleButton.IsEnabled = true;
            }
        }

        private void EnablePasswordStep()
        {
            PasswordBox.IsEnabled = true;
            ConfirmPasswordBox.IsEnabled = !_hasLocalPassword;
            ContinueButton.IsEnabled = true;
            PasswordBox.Focus();
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_googleReady)
            {
                PasswordStatusText.Text = "Primero inicia sesion con Google.";
                return;
            }

            if (_hasLocalPassword)
            {
                if (!LocalAppLockService.VerifyPassword(_settings, PasswordBox.Password))
                {
                    PasswordStatusText.Text = "Contrasena local incorrecta.";
                    PasswordBox.SelectAll();
                    PasswordBox.Focus();
                    return;
                }

                DialogResult = true;
                return;
            }

            if (PasswordBox.Password.Length < 8)
            {
                PasswordStatusText.Text = "Usa minimo 8 caracteres.";
                return;
            }

            if (PasswordBox.Password != ConfirmPasswordBox.Password)
            {
                PasswordStatusText.Text = "La confirmacion no coincide.";
                return;
            }

            LocalAppLockService.SetPassword(_settings, PasswordBox.Password);
            _settings.GoogleSyncEnabled = true;
            AppSettingsService.Save(_settings);
            DialogResult = true;
        }

        private void ApplyGoogleAccount(GoogleDriveBackupResult result)
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.AccountEmail))
                return;

            var previousEmail = _settings.GoogleAccountEmail;
            if (!string.IsNullOrWhiteSpace(previousEmail) &&
                !string.Equals(previousEmail, result.AccountEmail, StringComparison.OrdinalIgnoreCase))
            {
                AphBackupDatabaseService.DeleteLocalDatabase();
                GoogleStatusText.Text = $"Cuenta Google cambiada de {previousEmail} a {result.AccountEmail}. DB local anterior borrada.";
            }

            _settings.GoogleAccountEmail = result.AccountEmail;
            AppSettingsService.Save(_settings);
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ContinueButton.IsEnabled)
                ContinueButton_Click(sender, e);
        }
    }
}
