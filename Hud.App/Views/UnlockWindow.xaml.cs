using System.Windows;
using System.Windows.Input;
using Hud.App.Services;

namespace Hud.App.Views
{
    public partial class UnlockWindow : Window
    {
        private readonly AppSettings _settings;

        public UnlockWindow()
        {
            InitializeComponent();
            _settings = AppSettingsService.Load();
            Loaded += (_, _) => PasswordInput.Focus();
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (!LocalAppLockService.VerifyPassword(_settings, PasswordInput.Password))
            {
                StatusText.Text = "Contrasena local incorrecta.";
                PasswordInput.SelectAll();
                PasswordInput.Focus();
                return;
            }

            DialogResult = true;
        }

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                UnlockButton_Click(sender, e);
        }
    }
}
