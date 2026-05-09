using System.Windows;
using System.Windows.Input;

namespace Hud.App.Views
{
    public partial class ReportPasswordPromptWindow : Window
    {
        public ReportPasswordPromptWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => PasswordInput.Focus();
        }

        public string Password => PasswordInput.Password;

        private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                DialogResult = true;
        }
    }
}
