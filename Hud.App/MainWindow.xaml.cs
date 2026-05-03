using System.Windows;
using Hud.App.Services;
using Hud.App.Views;

namespace Hud.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.Title = "APH — Analyzer Poker Hands";
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            InfoText.Text = "Análisis global: KPIs agregados y gráfica de ganancias (placeholder).";
        }

        private void BtnOne_Click(object sender, RoutedEventArgs e)
        {
            InfoText.Text = "Partida específica: selector y gráfica individual (placeholder).";
        }

        private void BtnBestWorst_Click(object sender, RoutedEventArgs e)
        {
            InfoText.Text = "Mejores/peores manos (global o por partida) — ranking por ganancia/pérdida (placeholder).";
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

        rtWindow.Show(); // sin Owner
        InfoText.Text = "Analizador RT abierto (8 mesas).";
    }
    else
    {
        MessageBox.Show(
            "El servicio HandReaderService no está inicializado.\n" +
            "Reinicia la aplicación o revisa App.xaml.cs.",
            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
        }
    }
}
