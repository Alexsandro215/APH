using System.Windows;
using System.Windows.Interop;

namespace Hud.App.Views
{
    /// <summary>
    /// Abre un detalle (una mesa, un villano, una lista de manos) como pagina de la ventana
    /// principal, con boton "Volver". Si la ventana principal no esta disponible, o hay un dialogo
    /// modal abierto, lo abre como ventana aparte como antes.
    /// </summary>
    public static class AppNavigator
    {
        public static void Open(Window window)
        {
            if (!ComponentDispatcher.IsThreadModal &&
                Application.Current?.MainWindow is MainWindow main &&
                main.IsVisible)
            {
                main.OpenDetailPage(window);
                return;
            }

            window.Show();
        }
    }
}
