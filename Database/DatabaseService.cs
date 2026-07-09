using Microsoft.Data.Sqlite;
using System;
using System.Diagnostics;
using System.IO;

namespace direct_module.Database
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public string DatabasePath { get; }

        public DatabaseService()
        {
            string dbPath = ResolveDatabasePath();
            MigrateLegacyDatabaseIfNeeded(dbPath);

            // データベースの保存先を出力
            Debug.WriteLine("==================================");
            Debug.WriteLine($"DB Path = {dbPath}");
            Debug.WriteLine("==================================");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            Debug.WriteLine($"Folder Exists = {Directory.Exists(Path.GetDirectoryName(dbPath)!)}");

            DatabasePath = dbPath;
            _connectionString = $"Data Source={dbPath}";

            Initialize();
        }

        public SqliteConnection GetConnection()
        {
            return new SqliteConnection(_connectionString);
        }

        private void Initialize()
        {
            using var connection = GetConnection();

            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText =
            @"
CREATE TABLE IF NOT EXISTS Users
(
    DeviceId TEXT PRIMARY KEY,
    MachineName TEXT NOT NULL,
    DisplayName TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ChatMessages
(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,

    ConversationId TEXT NOT NULL,

    SenderId TEXT NOT NULL,
    SenderName TEXT NOT NULL,

    ReceiverId TEXT NOT NULL,
    ReceiverName TEXT NOT NULL,

    Message TEXT NOT NULL,

    SendTime TEXT NOT NULL,

    IsMine INTEGER NOT NULL
);
";

            command.ExecuteNonQuery();

            // Perform migrations for new columns
            string[] alterQueries = new[]
            {
                "ALTER TABLE ChatMessages ADD COLUMN MessageType TEXT DEFAULT 'chat';",
                "ALTER TABLE ChatMessages ADD COLUMN FileId TEXT;",
                "ALTER TABLE ChatMessages ADD COLUMN FileName TEXT;",
                "ALTER TABLE ChatMessages ADD COLUMN FileSize INTEGER DEFAULT 0;",
                "ALTER TABLE ChatMessages ADD COLUMN LocalFilePath TEXT;",
                "ALTER TABLE ChatMessages ADD COLUMN MimeType TEXT;",
                "ALTER TABLE ChatMessages ADD COLUMN IsGroup INTEGER DEFAULT 0;"
            };

            foreach (var query in alterQueries)
            {
                try
                {
                    using var alterCommand = connection.CreateCommand();
                    alterCommand.CommandText = query;
                    alterCommand.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // SQLITE_ERROR (column already exists)
                {
                    // Column already exists, ignore
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Migration error for query '{query}': {ex.Message}");
                }
            }

            Debug.WriteLine("SQLite初期化およびマイグレーション完了");
        }

        private static string ResolveDatabasePath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = string.IsNullOrWhiteSpace(userProfile)
                ? ""
                : Path.Combine(userProfile, "AppData", "Local");

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            }

            return Path.Combine(localAppData, "direct_module", "chat.db");
        }

        private static void MigrateLegacyDatabaseIfNeeded(string newDbPath)
        {
            string legacyDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "direct_module",
                "chat.db");

            if (string.Equals(legacyDbPath, newDbPath, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(newDbPath) ||
                !File.Exists(legacyDbPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newDbPath)!);
            File.Copy(legacyDbPath, newDbPath);
            Debug.WriteLine($"SQLite DB migrated: {legacyDbPath} -> {newDbPath}");
        }
    }
}
