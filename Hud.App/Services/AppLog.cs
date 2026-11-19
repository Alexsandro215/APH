using System.IO;
using System.Text;

namespace Hud.App.Services
{
    /// <summary>
    /// Registro de errores en archivo: %APPDATA%\APH\logs\aph-AAAAMMDD.log.
    /// Antes los errores solo iban a Debug.WriteLine, que en la version final no existe: cuando
    /// algo fallaba no quedaba ningun rastro. Nunca lanza excepciones; si no puede escribir, calla.
    /// </summary>
    public static class AppLog
    {
        private const int DaysToKeep = 14;
        private static readonly object Sync = new();
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
        private static string? _lastMessage;
        private static DateTime _lastWrite;
        private static bool _cleaned;

        public static string LogFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH", "logs");

        public static string CurrentLogPath =>
            Path.Combine(LogFolder, $"aph-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                var text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";

                lock (Sync)
                {
                    // Un mismo error que se repite en bucle (por ejemplo, un archivo bloqueado que
                    // se sondea cada 250 ms) se registra una vez por minuto, no miles de veces.
                    var now = DateTime.Now;
                    if (text == _lastMessage && now - _lastWrite < RepeatWindow)
                        return;

                    _lastMessage = text;
                    _lastWrite = now;

                    Directory.CreateDirectory(LogFolder);
                    var line = new StringBuilder()
                        .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                        .Append(" [").Append(level).Append("] ")
                        .Append(text);

                    if (ex is not null && level == "ERROR")
                        line.AppendLine().Append(ex.StackTrace);

                    File.AppendAllText(CurrentLogPath, line.AppendLine().ToString(), Encoding.UTF8);

                    if (!_cleaned)
                    {
                        _cleaned = true;
                        DeleteOldLogs();
                    }
                }
            }
            catch
            {
                // El registro nunca debe tumbar la app.
            }
        }

        private static void DeleteOldLogs()
        {
            foreach (var file in Directory.EnumerateFiles(LogFolder, "aph-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-DaysToKeep))
                    File.Delete(file);
            }
        }
    }
}
