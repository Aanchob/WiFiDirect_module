using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using direct_module.Models;

namespace direct_module.Database
{
    public class ChatRepository
    {
        private readonly DatabaseService _databaseService;

        public ChatRepository(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        /// <summary>
        /// メッセージを保存
        /// </summary>
        public void SaveMessage(ChatMessage message)
        {
            using var connection = _databaseService.GetConnection();
            connection.Open();

            var command = connection.CreateCommand();

            command.CommandText =
            @"
            INSERT INTO ChatMessages
            (
                ConversationId,
                SenderId,
                SenderName,
                ReceiverId,
                ReceiverName,
                Message,
                SendTime,
                IsMine,
                MessageType,
                FileId,
                FileName,
                FileSize,
                LocalFilePath,
                MimeType,
                IsGroup
            )
            VALUES
            (
                @ConversationId,
                @SenderId,
                @SenderName,
                @ReceiverId,
                @ReceiverName,
                @Message,
                @SendTime,
                @IsMine,
                @MessageType,
                @FileId,
                @FileName,
                @FileSize,
                @LocalFilePath,
                @MimeType,
                @IsGroup
            );
            ";

            command.Parameters.AddWithValue("@ConversationId", message.ConversationId);
            command.Parameters.AddWithValue("@SenderId", message.SenderId);
            command.Parameters.AddWithValue("@SenderName", message.SenderName);
            command.Parameters.AddWithValue("@ReceiverId", message.ReceiverId);
            command.Parameters.AddWithValue("@ReceiverName", message.ReceiverName);
            command.Parameters.AddWithValue("@Message", message.Message);
            command.Parameters.AddWithValue("@SendTime", message.SendTime.ToString("o"));
            command.Parameters.AddWithValue("@IsMine", message.IsMine ? 1 : 0);
            command.Parameters.AddWithValue("@MessageType", message.MessageType);
            command.Parameters.AddWithValue("@FileId", (object?)message.FileId ?? DBNull.Value);
            command.Parameters.AddWithValue("@FileName", (object?)message.FileName ?? DBNull.Value);
            command.Parameters.AddWithValue("@FileSize", message.FileSize);
            command.Parameters.AddWithValue("@LocalFilePath", (object?)message.LocalFilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("@MimeType", (object?)message.MimeType ?? DBNull.Value);
            command.Parameters.AddWithValue("@IsGroup", message.IsGroup ? 1 : 0);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 全履歴取得
        /// </summary>
        public List<ChatMessage> GetMessages()
        {
            List<ChatMessage> messages = new();

            using var connection = _databaseService.GetConnection();
            connection.Open();

            var command = connection.CreateCommand();

            command.CommandText =
            @"
            SELECT
                ConversationId,
                SenderId,
                SenderName,
                ReceiverId,
                ReceiverName,
                Message,
                SendTime,
                IsMine,
                MessageType,
                FileId,
                FileName,
                FileSize,
                LocalFilePath,
                MimeType,
                IsGroup
            FROM ChatMessages
            ORDER BY SendTime ASC;
            ";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                messages.Add(new ChatMessage
                {
                    ConversationId = reader.GetString(0),
                    SenderId = reader.GetString(1),
                    SenderName = reader.GetString(2),
                    ReceiverId = reader.GetString(3),
                    ReceiverName = reader.GetString(4),
                    Message = reader.GetString(5),
                    SendTime = DateTime.Parse(reader.GetString(6)),
                    IsMine = reader.GetInt32(7) == 1,
                    MessageType = reader.IsDBNull(8) ? "chat" : reader.GetString(8),
                    FileId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    FileName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    FileSize = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                    LocalFilePath = reader.IsDBNull(12) ? null : reader.GetString(12),
                    MimeType = reader.IsDBNull(13) ? null : reader.GetString(13),
                    IsGroup = reader.IsDBNull(14) ? false : reader.GetInt32(14) == 1
                });
            }

            return messages;
        }

        /// <summary>
        /// 会話ごとの履歴取得
        /// </summary>
        public List<ChatMessage> GetMessages(string conversationId)
        {
            List<ChatMessage> messages = new();

            using var connection = _databaseService.GetConnection();
            connection.Open();

            var command = connection.CreateCommand();

            command.CommandText =
            @"
            SELECT
                ConversationId,
                SenderId,
                SenderName,
                ReceiverId,
                ReceiverName,
                Message,
                SendTime,
                IsMine,
                MessageType,
                FileId,
                FileName,
                FileSize,
                LocalFilePath,
                MimeType,
                IsGroup
            FROM ChatMessages
            WHERE ConversationId = @ConversationId
            ORDER BY SendTime ASC;
            ";

            command.Parameters.AddWithValue("@ConversationId", conversationId);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                messages.Add(new ChatMessage
                {
                    ConversationId = reader.GetString(0),
                    SenderId = reader.GetString(1),
                    SenderName = reader.GetString(2),
                    ReceiverId = reader.GetString(3),
                    ReceiverName = reader.GetString(4),
                    Message = reader.GetString(5),
                    SendTime = DateTime.Parse(reader.GetString(6)),
                    IsMine = reader.GetInt32(7) == 1,
                    MessageType = reader.IsDBNull(8) ? "chat" : reader.GetString(8),
                    FileId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    FileName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    FileSize = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                    LocalFilePath = reader.IsDBNull(12) ? null : reader.GetString(12),
                    MimeType = reader.IsDBNull(13) ? null : reader.GetString(13),
                    IsGroup = reader.IsDBNull(14) ? false : reader.GetInt32(14) == 1
                });
            }

            return messages;
        }
    }
}