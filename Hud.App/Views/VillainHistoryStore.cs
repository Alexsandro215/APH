using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hud.App.Services;

namespace Hud.App.Views
{
    public static class VillainHistoryStore
    {
        private static readonly object Sync = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly Dictionary<string, DataVillainsWindow.DataVillainRow> Villains =
            new(StringComparer.Ordinal);
        private static IReadOnlyList<MainWindow.TableSessionStats> _tables = Array.Empty<MainWindow.TableSessionStats>();

        static VillainHistoryStore()
        {
            Load();
        }

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

            Save();
        }

        public static void RebuildFromTables(IEnumerable<MainWindow.TableSessionStats> tables)
        {
            var rows = DataVillainsWindow.BuildHistory(tables, out var sourceTables);
            Replace(rows, sourceTables);
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

        private static void Load()
        {
            try
            {
                var path = CachePath;
                if (!File.Exists(path))
                    return;

                var json = File.ReadAllText(path);
                var cache = JsonSerializer.Deserialize<VillainHistoryCache>(json, JsonOptions);
                if (cache is null)
                    return;

                lock (Sync)
                {
                    Villains.Clear();
                    foreach (var row in cache.Villains.Select(ToRow))
                        Villains[row.Name] = row;

                    _tables = cache.Tables.Select(ToTable).ToList();
                }
            }
            catch
            {
                // Cache is an optimization; corrupted or old files should never block the app.
            }
        }

        private static void Save()
        {
            try
            {
                VillainHistoryCache cache;
                lock (Sync)
                {
                    cache = new VillainHistoryCache(
                        Villains.Values.Select(ToDto).ToList(),
                        _tables.Select(ToDto).ToList());
                }

                var directory = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, JsonOptions));
            }
            catch
            {
                // Persistence should not interrupt analysis.
            }
        }

        private static string CachePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "APH",
                "villain-history.json");

        private static VillainDto ToDto(DataVillainsWindow.DataVillainRow row) =>
            new(
                row.Name,
                row.RecentTable,
                row.GameFormat,
                row.IsCash,
                row.SessionHandsVsHero,
                row.TotalHandsVsHero,
                row.TotalHands,
                row.VPIPPct,
                row.PFRPct,
                row.ThreeBetPct,
                row.AF,
                row.AFqPct,
                row.CBetFlopPct,
                row.FoldVsCBetFlopPct,
                row.WTSDPct,
                row.WSDPct,
                row.WWSFPct,
                row.SessionNetBb,
                row.TotalNetBb,
                row.Stake,
                row.LastSeen);

        private static DataVillainsWindow.DataVillainRow ToRow(VillainDto row) =>
            new(
                row.Name,
                row.RecentTable,
                row.GameFormat,
                row.IsCash,
                row.SessionHandsVsHero,
                row.TotalHandsVsHero,
                row.TotalHands,
                row.VPIPPct,
                row.PFRPct,
                row.ThreeBetPct,
                row.AF,
                row.AFqPct,
                row.CBetFlopPct,
                row.FoldVsCBetFlopPct,
                row.WTSDPct,
                row.WSDPct,
                row.WWSFPct,
                row.SessionNetBb,
                row.TotalNetBb,
                row.Stake,
                row.LastSeen);

        private static TableDto ToDto(MainWindow.TableSessionStats table) =>
            new(
                table.TableName,
                table.GameFormat,
                table.PlayedDate,
                table.LastPlayedAt,
                table.SourcePath,
                table.PokerRoom,
                table.HeroName,
                table.BigBlind,
                table.Stake,
                table.HandsReceived,
                table.VPIPPct,
                table.PFRPct,
                table.ThreeBetPct,
                table.AF,
                table.AFqPct,
                table.CBetFlopPct,
                table.FoldVsCBetFlopPct,
                table.WTSDPct,
                table.WSDPct,
                table.WWSFPct,
                table.NetBb,
                table.NetAmount,
                table.IsCash,
                table.NetAmountLabel);

        private static MainWindow.TableSessionStats ToTable(TableDto table) =>
            new(
                table.TableName,
                table.GameFormat,
                table.PlayedDate,
                table.LastPlayedAt,
                table.SourcePath,
                table.PokerRoom,
                table.HeroName,
                table.BigBlind,
                table.Stake,
                table.HandsReceived,
                table.VPIPPct,
                table.PFRPct,
                table.ThreeBetPct,
                table.AF,
                table.AFqPct,
                table.CBetFlopPct,
                table.FoldVsCBetFlopPct,
                table.WTSDPct,
                table.WSDPct,
                table.WWSFPct,
                table.NetBb,
                table.NetAmount,
                table.IsCash,
                table.NetAmountLabel);

        private sealed record VillainHistoryCache(List<VillainDto> Villains, List<TableDto> Tables);

        private sealed record VillainDto(
            string Name,
            string RecentTable,
            string GameFormat,
            bool IsCash,
            int SessionHandsVsHero,
            int TotalHandsVsHero,
            int TotalHands,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetFlopPct,
            double FoldVsCBetFlopPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            double SessionNetBb,
            double TotalNetBb,
            StakeProfile Stake,
            DateTime LastSeen);

        private sealed record TableDto(
            string TableName,
            string GameFormat,
            string PlayedDate,
            DateTime LastPlayedAt,
            string SourcePath,
            string PokerRoom,
            string HeroName,
            double BigBlind,
            StakeProfile Stake,
            int HandsReceived,
            double VPIPPct,
            double PFRPct,
            double ThreeBetPct,
            double AF,
            double AFqPct,
            double CBetFlopPct,
            double FoldVsCBetFlopPct,
            double WTSDPct,
            double WSDPct,
            double WWSFPct,
            double NetBb,
            double NetAmount,
            bool IsCash,
            string NetAmountLabel);
    }
}

