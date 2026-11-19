using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hud.App.Services.Trainer
{
    /// <summary>
    /// Mesas con "Farol" activado (boton en cada mesa de Tiempo real). En dinero ficticio el consejo
    /// no farolea: casi todo se paga. Con el farol activado en una mesa, contra un rival que se deja
    /// farolear (nit o que se retira mucho a las c-bets, con 40+ manos), mano a mano y sin mano hecha,
    /// el consejo puede apostar hasta medio bote. Apagado por defecto; se recuerda por nombre de mesa.
    /// </summary>
    public static class TableBluffModes
    {
        private static readonly object Sync = new();
        private static HashSet<string>? _tables;

        /// <summary>Se avisa con el nombre de la mesa cuando cambia.</summary>
        public static event Action<string>? Changed;

        private static string FilePath => Path.Combine(TrainerCalibration.FolderPath, "farol_mesas.json");

        public static bool IsOn(string? tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return false;
            lock (Sync)
                return Tables().Contains(tableName.Trim());
        }

        public static void Set(string? tableName, bool on)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return;
            var name = tableName.Trim();
            lock (Sync)
            {
                var tables = Tables();
                if (!(on ? tables.Add(name) : tables.Remove(name)))
                    return;
                try
                {
                    Directory.CreateDirectory(TrainerCalibration.FolderPath);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(tables.OrderBy(table => table).ToList()));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("No se pudo guardar el farol por mesa", ex);
                }
            }

            Changed?.Invoke(name);
        }

        private static HashSet<string> Tables()
        {
            if (_tables is not null)
                return _tables;
            _tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } saved)
                    _tables.UnionWith(saved.Where(name => !string.IsNullOrWhiteSpace(name)));
            }
            catch
            {
                // Archivo danado: se empieza sin mesas con farol.
            }

            return _tables;
        }
    }
}
