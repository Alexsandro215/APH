using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Hud.App.Services
{
    public static class AphBackupDatabaseService
    {
        private const int SchemaVersion = 1;

        private static readonly object Sync = new();

        public static string DatabasePath
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "APH");
                return Path.Combine(folder, "aph.db");
            }
        }

        public static void DeleteLocalDatabase()
        {
            lock (Sync)
            {
                DeleteIfExists(DatabasePath);
                DeleteIfExists($"{DatabasePath}-wal");
                DeleteIfExists($"{DatabasePath}-shm");
            }
        }

        public static IReadOnlyList<string> MaterializeMissingBackups(string pokerRoom)
        {
            if (string.IsNullOrWhiteSpace(pokerRoom) || !File.Exists(DatabasePath))
                return Array.Empty<string>();

            var restored = new List<string>();
            try
            {
                lock (Sync)
                {
                    using var connection = new SqliteConnection($"Data Source={DatabasePath}");
                    connection.Open();
                    EnsureSchema(connection);

                    using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT file_hash, source_path, file_name, raw_text
                        FROM imported_files
                        WHERE poker_room = $poker_room
                        ORDER BY played_at_utc, imported_at_utc, id;
                        """;
                    command.Parameters.AddWithValue("$poker_room", pokerRoom);

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var hash = reader.GetString(0);
                        var sourcePath = reader.GetString(1);
                        var fileName = SanitizeFileName(reader.GetString(2));
                        var rawText = reader.GetString(3);

                        if (File.Exists(sourcePath))
                            continue;

                        var cachePath = GetMaterializedFilePath(pokerRoom, hash, fileName);
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                        File.WriteAllText(cachePath, rawText, Encoding.UTF8);
                        restored.Add(cachePath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"APH backup materialize error: {ex.Message}");
            }

            return restored;
        }

        public static int BackupFolder(
            string folder,
            string pokerRoom,
            string? heroName = null,
            IReadOnlyDictionary<string, MainWindow.TableSessionStats>? knownTables = null)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return 0;

            var backedUp = 0;
            foreach (var file in EnumerateHandHistoryFiles(folder))
            {
                MainWindow.TableSessionStats? table = null;
                knownTables?.TryGetValue(file, out table);
                BackupHandHistoryFile(file, pokerRoom, heroName, table);
                backedUp++;
            }

            return backedUp;
        }

        public static void BackupHandHistoryFile(
            string sourcePath,
            string pokerRoom,
            string? heroName,
            MainWindow.TableSessionStats? table)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return;

            if (IsMaterializedBackupPath(sourcePath))
                return;

            try
            {
                lock (Sync)
                {
                    var lines = File.ReadAllLines(sourcePath);
                    var rawText = string.Join(Environment.NewLine, lines);
                    var fileInfo = new FileInfo(sourcePath);
                    var fileHash = Sha256(rawText);

                    Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                    using var connection = new SqliteConnection($"Data Source={DatabasePath}");
                    connection.Open();
                    EnsureSchema(connection);

                    using var transaction = connection.BeginTransaction();
                    var fileId = UpsertImportedFile(
                        connection,
                        transaction,
                        sourcePath,
                        pokerRoom,
                        heroName,
                        table,
                        fileInfo,
                        fileHash,
                        rawText);

                    BackupRawHands(connection, transaction, fileId, lines, pokerRoom, heroName, table);
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"APH backup DB error for {sourcePath}: {ex.Message}");
            }
        }

        private static bool IsMaterializedBackupPath(string sourcePath)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "APH",
                "RestoredHandHistories");
            return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"APH backup DB delete failed for {path}: {ex.Message}");
            }
        }

        private static IEnumerable<string> EnumerateHandHistoryFiles(string folder)
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetMaterializedFilePath(string pokerRoom, string hash, string fileName)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "APH",
                "RestoredHandHistories",
                SanitizeFileName(pokerRoom));
            var ext = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            return Path.Combine(folder, $"{name}_{hash[..8]}{(string.IsNullOrWhiteSpace(ext) ? ".txt" : ext)}");
        }

        public static int ExportOriginalFiles(string destinationFolder)
        {
            if (string.IsNullOrWhiteSpace(destinationFolder) || !File.Exists(DatabasePath))
                return 0;

            Directory.CreateDirectory(destinationFolder);
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT file_hash, file_name, poker_room, raw_text
                FROM imported_files
                ORDER BY imported_at_utc, id;
                """;

            var exported = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hash = reader.GetString(0);
                var fileName = SanitizeFileName(reader.GetString(1));
                var room = SanitizeFileName(reader.GetString(2));
                var rawText = reader.GetString(3);
                var roomFolder = Path.Combine(destinationFolder, room);
                Directory.CreateDirectory(roomFolder);

                var target = Path.Combine(roomFolder, fileName);
                if (File.Exists(target))
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    target = Path.Combine(roomFolder, $"{name}_{hash[..8]}{ext}");
                }

                File.WriteAllText(target, rawText, Encoding.UTF8);
                exported++;
            }

            return exported;
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            ExecuteNonQuery(
                connection,
                null,
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS app_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT OR REPLACE INTO app_metadata(key, value)
                VALUES('schema_version', $schema_version);
                """,
                ("$schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture)));

            ExecuteNonQuery(
                connection,
                null,
                """
                CREATE TABLE IF NOT EXISTS imported_files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_hash TEXT NOT NULL UNIQUE,
                    source_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    poker_room TEXT NOT NULL,
                    hero_name TEXT,
                    table_name TEXT,
                    game_format TEXT,
                    played_at_utc TEXT,
                    big_blind REAL,
                    hands_received INTEGER,
                    net_amount REAL,
                    net_bb REAL,
                    is_cash INTEGER,
                    file_size INTEGER NOT NULL,
                    last_write_utc TEXT NOT NULL,
                    imported_at_utc TEXT NOT NULL,
                    raw_text TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_imported_files_source_path
                    ON imported_files(source_path);

                CREATE INDEX IF NOT EXISTS ix_imported_files_poker_room_played
                    ON imported_files(poker_room, played_at_utc);

                CREATE TABLE IF NOT EXISTS raw_hands (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    imported_file_id INTEGER NOT NULL,
                    hand_index INTEGER NOT NULL,
                    hand_hash TEXT NOT NULL UNIQUE,
                    poker_room TEXT NOT NULL,
                    hero_name TEXT,
                    table_name TEXT,
                    played_at_utc TEXT,
                    raw_text TEXT NOT NULL,
                    FOREIGN KEY(imported_file_id) REFERENCES imported_files(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_raw_hands_imported_file
                    ON raw_hands(imported_file_id);

                CREATE INDEX IF NOT EXISTS ix_raw_hands_played
                    ON raw_hands(played_at_utc);
                """);
        }

        private static long UpsertImportedFile(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourcePath,
            string pokerRoom,
            string? heroName,
            MainWindow.TableSessionStats? table,
            FileInfo fileInfo,
            string fileHash,
            string rawText)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                """
                INSERT INTO imported_files (
                    file_hash, source_path, file_name, poker_room, hero_name, table_name,
                    game_format, played_at_utc, big_blind, hands_received, net_amount,
                    net_bb, is_cash, file_size, last_write_utc, imported_at_utc, raw_text
                )
                VALUES (
                    $file_hash, $source_path, $file_name, $poker_room, $hero_name, $table_name,
                    $game_format, $played_at_utc, $big_blind, $hands_received, $net_amount,
                    $net_bb, $is_cash, $file_size, $last_write_utc, $imported_at_utc, $raw_text
                )
                ON CONFLICT(file_hash) DO UPDATE SET
                    source_path = excluded.source_path,
                    file_name = excluded.file_name,
                    poker_room = excluded.poker_room,
                    hero_name = excluded.hero_name,
                    table_name = excluded.table_name,
                    game_format = excluded.game_format,
                    played_at_utc = excluded.played_at_utc,
                    big_blind = excluded.big_blind,
                    hands_received = excluded.hands_received,
                    net_amount = excluded.net_amount,
                    net_bb = excluded.net_bb,
                    is_cash = excluded.is_cash,
                    file_size = excluded.file_size,
                    last_write_utc = excluded.last_write_utc,
                    imported_at_utc = excluded.imported_at_utc,
                    raw_text = excluded.raw_text;
                """,
                ("$file_hash", fileHash),
                ("$source_path", sourcePath),
                ("$file_name", Path.GetFileName(sourcePath)),
                ("$poker_room", pokerRoom),
                ("$hero_name", heroName),
                ("$table_name", table?.TableName),
                ("$game_format", table?.GameFormat),
                ("$played_at_utc", ToUtcText(table?.LastPlayedAt)),
                ("$big_blind", table?.BigBlind),
                ("$hands_received", table?.HandsReceived),
                ("$net_amount", table?.NetAmount),
                ("$net_bb", table?.NetBb),
                ("$is_cash", table is null ? null : table.IsCash ? 1 : 0),
                ("$file_size", fileInfo.Length),
                ("$last_write_utc", fileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$imported_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ("$raw_text", rawText));

            return Convert.ToInt64(
                ExecuteScalar(
                    connection,
                    transaction,
                    "SELECT id FROM imported_files WHERE file_hash = $file_hash;",
                    ("$file_hash", fileHash)),
                CultureInfo.InvariantCulture);
        }

        private static void BackupRawHands(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long importedFileId,
            string[] lines,
            string pokerRoom,
            string? heroName,
            MainWindow.TableSessionStats? table)
        {
            var index = 0;
            foreach (var hand in PokerStarsHandHistory.SplitHands(lines))
            {
                index++;
                var rawHand = string.Join(Environment.NewLine, hand);
                if (string.IsNullOrWhiteSpace(rawHand))
                    continue;

                var handHash = Sha256(rawHand);
                var playedAt = PokerStarsHandHistory.ExtractTimestamp(hand) ?? table?.LastPlayedAt;
                ExecuteNonQuery(
                    connection,
                    transaction,
                    """
                    INSERT INTO raw_hands (
                        imported_file_id, hand_index, hand_hash, poker_room, hero_name,
                        table_name, played_at_utc, raw_text
                    )
                    VALUES (
                        $imported_file_id, $hand_index, $hand_hash, $poker_room, $hero_name,
                        $table_name, $played_at_utc, $raw_text
                    )
                    ON CONFLICT(hand_hash) DO UPDATE SET
                        imported_file_id = excluded.imported_file_id,
                        hand_index = excluded.hand_index,
                        poker_room = excluded.poker_room,
                        hero_name = excluded.hero_name,
                        table_name = excluded.table_name,
                        played_at_utc = excluded.played_at_utc,
                        raw_text = excluded.raw_text;
                    """,
                    ("$imported_file_id", importedFileId),
                    ("$hand_index", index),
                    ("$hand_hash", handHash),
                    ("$poker_room", pokerRoom),
                    ("$hero_name", heroName),
                    ("$table_name", table?.TableName),
                    ("$played_at_utc", ToUtcText(playedAt)),
                    ("$raw_text", rawHand));
            }
        }

        private static string Sha256(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "APH_Backup" : clean;
        }

        private static string? ToUtcText(DateTime? value)
        {
            if (value is null || value.Value == DateTime.MinValue)
                return null;

            var date = value.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local)
                : value.Value;
            return date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        private static object? ExecuteScalar(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(connection, transaction, sql, parameters);
            return command.ExecuteScalar();
        }

        private static void ExecuteNonQuery(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(connection, transaction, sql, parameters);
            command.ExecuteNonQuery();
        }

        private static SqliteCommand CreateCommand(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
    }
}
