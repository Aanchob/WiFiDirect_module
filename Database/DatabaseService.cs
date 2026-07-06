using Microsoft.Data.Sqlite;
using System;
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

            // フォルダが存在しなければ作成
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            _connectionString = $"Data Source={dbPath}";

            InitializeDatabase();
        }

        /// <summary>
        /// SQLiteデータベースとテーブルを作成
        /// </summary>
        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);

            connection.Open();

            string sql =
            @"
            CREATE TABLE IF NOT EXISTS ChatMessages
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Sender TEXT NOT NULL,
                Receiver TEXT NOT NULL,
                Message TEXT NOT NULL,
                SendTime TEXT NOT NULL,
                IsMine INTEGER NOT NULL
            );
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// SQLite接続を取得
        /// </summary>
        public SqliteConnection GetConnection()
        {
            return new SqliteConnection(_connectionString);
        }
    }
}