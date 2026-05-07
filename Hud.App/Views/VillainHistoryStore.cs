using System;
using System.Collections.Generic;
using System.Linq;

namespace Hud.App.Views
{
    public static class VillainHistoryStore
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, DataVillainsWindow.DataVillainRow> Villains =
            new(StringComparer.Ordinal);
        private static IReadOnlyList<MainWindow.TableSessionStats> _tables = Array.Empty<MainWindow.TableSessionStats>();

        public static void Replace(
            IEnumerable<DataVillainsWindow.DataVillainRow> villains,
            IEnumerable<MainWindow.TableSessionStats> tables)
        {
            lock (Sync)
            {
                Villains.Clear();
                foreach (var villain in villains)
                    Villains[villain.Name] = villain;

                _tables = tables.ToList();
            }
        }

        public static bool TryGet(string name, out DataVillainsWindow.DataVillainRow row)
        {
            lock (Sync)
                return Villains.TryGetValue(name, out row!);
        }

        public static IReadOnlyList<MainWindow.TableSessionStats> Tables
        {
            get
            {
                lock (Sync)
                    return _tables.ToList();
            }
        }
    }
}
