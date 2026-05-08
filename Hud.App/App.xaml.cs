using System.IO;
using System.Windows;
using Hud.App.Services;
using Hud.App.Views;

namespace Hud.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var settings = AppSettingsService.Load();
            ThemePaletteManager.Apply(settings.Palette);
            LocalizationManager.Apply(settings.Language);

            // Servicio compartido
            var hand = new HandReaderService();

            // Dejarlo accesible globalmente
            this.Resources["HandReaderService"] = hand;

            // MOSTRAR MENÚ PRINCIPAL
            var menu = new MainWindow();                 // <— tu ventana de menú
            // si usas VM, pásale el servicio a la VM:
            // menu.DataContext = new MainViewModel(hand);
            menu.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (this.Resources["HandReaderService"] is HandReaderService hand)
                hand.Dispose();
            base.OnExit(e);
        }

    }
}

