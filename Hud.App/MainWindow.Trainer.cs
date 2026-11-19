using System.Linq;
using System.Windows;
using Hud.App.Views;

namespace Hud.App
{
    public partial class MainWindow
    {
        private void BtnTrainer_Click(object sender, RoutedEventArgs e)
        {
            // Una sola ventana: dos lectores guardarian a la vez las plantillas de cartas y se
            // pisarian lo aprendido.
            var open = Application.Current.Windows.OfType<TrainerWindow>().FirstOrDefault();
            if (open is not null)
            {
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;
                open.Activate();
                InfoText.Text = "Modo Entrenador ya estaba abierto.";
                return;
            }

            var window = new TrainerWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
            InfoText.Text = "Modo Entrenador abierto.";
        }
    }
}
