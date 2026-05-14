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
                : string.Format(LocalizationManager.Text("Login.MissingClientSecret"), GoogleDriveBackupService.CredentialsFolder);
        }

        private async void GoogleButton_Click(object sender, RoutedEventArgs e)
        {
            GoogleButton.IsEnabled = false;
            GoogleStatusText.Text = LocalizationManager.Text("Login.OpeningGoogle");

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
                GoogleStatusText.Text = string.Format(LocalizationManager.Text("Login.GoogleFailed"), ex.Message);
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
                PasswordStatusText.Text = LocalizationManager.Text("Login.MustSignInGoogle");
                return;
            }

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
                GoogleStatusText.Text = string.Format(LocalizationManager.Text("Login.AccountChanged"), previousEmail, result.AccountEmail);
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
