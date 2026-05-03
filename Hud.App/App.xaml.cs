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

            // Servicio compartido
            var hand = new HandReaderService();

            // (opcional) arrancar último archivo
            var lastPath = UserSettings.Load()?.LastPath;
            if (!string.IsNullOrWhiteSpace(lastPath) && File.Exists(lastPath))
                hand.Start(lastPath);

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

 public sealed class UserSettings
    {
        public string? LastPath { get; set; }

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "HudPoker", "user.settings");

        public static UserSettings? Load()
        {
            try
            {
                var fp = FilePath;
                if (!File.Exists(fp)) return new UserSettings();
                var txt = File.ReadAllText(fp).Trim();
                return new UserSettings { LastPath = string.IsNullOrWhiteSpace(txt) ? null : txt };
            }
            catch { return new UserSettings(); }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, LastPath ?? "");
            }
            catch { /* ignore */ }
        }
    }




    }
}
