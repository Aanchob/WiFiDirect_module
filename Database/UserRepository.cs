using System;
using Microsoft.Data.Sqlite;
using direct_module.Models;

namespace direct_module.Database
{
    public class UserRepository
    {
        private readonly DatabaseService _databaseService;

        public UserRepository(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        /// <summary>
        /// MachineNameからユーザー情報を取得
        /// </summary>
        public User? GetByMachineName(string machineName)
        {
            using var connection = _databaseService.GetConnection();
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
            @"
            SELECT
                DeviceId,
                MachineName,
                DisplayName,
                CreatedAt
            FROM Users
            WHERE MachineName = @MachineName;
            ";

            command.Parameters.AddWithValue("@MachineName", machineName);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            return new User
            {
                DeviceId = reader.GetString(0),
                MachineName = reader.GetString(1),
                DisplayName = reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3))
            };
        }

        /// <summary>
        /// ユーザーを保存
        /// </summary>
        public void Save(User user)
        {
            using var connection = _databaseService.GetConnection();
            connection.Open();

            var command = connection.CreateCommand();

            command.CommandText =
            @"
            INSERT OR REPLACE INTO Users
            (
                DeviceId,
                MachineName,
                DisplayName,
                CreatedAt
            )
            VALUES
            (
                @DeviceId,
                @MachineName,
                @DisplayName,
                @CreatedAt
            );
            ";

            command.Parameters.AddWithValue("@DeviceId", user.DeviceId);
            command.Parameters.AddWithValue("@MachineName", user.MachineName);
            command.Parameters.AddWithValue("@DisplayName", user.DisplayName);
            command.Parameters.AddWithValue("@CreatedAt", user.CreatedAt.ToString("o"));

            command.ExecuteNonQuery();
        }
    }
}