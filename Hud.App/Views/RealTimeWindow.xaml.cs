using System;
using System.Windows;

namespace Hud.App.Views
{
    public partial class RealTimeWindow : Window
    {
        public RealTimeWindow()
        {
            InitializeComponent();

            // aplica escala inicial segun el checkbox (compacto por defecto)
            ApplyScale(ChkCompact?.IsChecked == true);
            if (ChkCompact != null)
            {
                ChkCompact.Checked += (_, __) => ApplyScale(true);
                ChkCompact.Unchecked += (_, __) => ApplyScale(false);
            }
        }

        private void ApplyScale(bool compact)
        {
            // 0.80 en compacto, 1.00 en normal
            if (ScaleProxy != null)
                ScaleProxy.Tag = compact ? 0.80 : 1.00;
        }

        private void BtnDictionary_Click(object sender, RoutedEventArgs e)
        {
            var window = new RealTimeDictionaryWindow
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            window.Show();
        }
    }
}
