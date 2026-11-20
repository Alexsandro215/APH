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
            InstallGlobalErrorHandlers();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settings = AppSettingsService.Load();
            ThemePaletteManager.Apply(settings.Palette);
            UiMotion.Level = UiMotion.Parse(settings.AnimationLevel);
            ThemedTitleBar.Install();
            LocalizationManager.Apply(settings.Language);
            AppLog.Info(PopulationBaselines.IsAvailable
                ? "Perfiles calibrados con la poblacion: " + PlayerProfileClassifier.Describe()
                : "Sin Data/population_baselines.json: perfiles y colores con los limites fijos");

            // Servicio compartido
            var hand = new HandReaderService();

            // Dejarlo accesible globalmente
            this.Resources["HandReaderService"] = hand;

            // Quien manda es el bloqueo local. Exigir ademas un token de Google devolvia a la
            // pantalla de alta a quien ya tenia contrasena pero no usa Drive.
            Window accessWindow = LocalAppLockService.HasPassword(settings)
                ? new UnlockWindow()
                : new LoginWindow();

            if (accessWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            // Menu principal
            var menu = new MainWindow();
            MainWindow = menu;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            menu.Show();
        }

        // Sin estos manejadores, cualquier error no previsto cerraba la app de golpe y sin dejar
        // rastro. Ahora queda en el registro y, si ocurre en la interfaz, se avisa y se sigue.
        private void InstallGlobalErrorHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                AppLog.Error("Error no controlado en la interfaz", args.Exception);
                args.Handled = true;
                try
                {
                    MessageBox.Show(
                        string.Format(LocalizationManager.Text("App.UnexpectedError"), args.Exception.Message, AppLog.LogFolder),
                        "APH",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch
                {
                    // Si ni siquiera se puede mostrar el aviso, el error ya quedo registrado.
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    AppLog.Error("Error no controlado fuera de la interfaz", ex);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Error("Error no observado en tarea de fondo", args.Exception);
                args.SetObserved();
            };

            AppLog.Info("APH iniciada");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (this.Resources["HandReaderService"] is HandReaderService hand)
                hand.Dispose();
            base.OnExit(e);
        }

    }
}

