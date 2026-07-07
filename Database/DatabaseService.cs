using Microsoft.Data.Sqlite;
using System;
using System.Diagnostics;
using System.IO;

namespace direct_module.Database
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService()
        {
            string dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "direct_module",
                "chat.db");

            // データベースの保存先を出力
            Debug.WriteLine("==================================");
            Debug.WriteLine($"DB Path = {dbPath}");
            Debug.WriteLine("==================================");

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            Debug.WriteLine($"Folder Exists = {Directory.Exists(Path.GetDirectoryName(dbPath)!)}");

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

            Debug.WriteLine("SQLite初期化完了");
        }
    }
}