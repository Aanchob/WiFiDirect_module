using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using direct_module.Models;
using direct_module.Services;
using Microsoft.Data.Sqlite;

namespace direct_module.Database
{
    public sealed class ChatRepository
    {
        public const int DefaultConversationLoadLimit = 500;
        public const int DefaultGlobalLoadLimit = 1_000;
        private const int MaximumLoadLimit = 5_000;
        private const string UnreadableHistoryPlaceholder = "[この履歴を復号できません]";

        private readonly DatabaseService _databaseService;

        public ChatRepository(DatabaseService databaseService)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        }

        public void SaveMessage(ChatMessage message)
        {
            _ = TrySaveMessage(message);
        }

        /// <summary>
        /// Persists a message. False means its MessageId already exists.
        /// </summary>
        public bool TrySaveMessage(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            string messageId;
            if (string.IsNullOrWhiteSpace(message.MessageId))
            {
                messageId = Guid.NewGuid().ToString("N");
            }
            else if (Guid.TryParse(message.MessageId.Trim(), out Guid parsedMessageId))
            {
                messageId = parsedMessageId.ToString("N");
            }
            else
            {
                throw new ArgumentException("MessageId must be a GUID.", nameof(message.MessageId));
            }
            ValidateLength(messageId, nameof(message.MessageId), 128, required: true);
            ValidateLength(message.MessageType, nameof(message.MessageType), 32, required: true);
            ValidateLength(message.ConversationId, nameof(message.ConversationId), 512, required: true);
            ValidateLength(message.SenderId, nameof(message.SenderId), 512, required: false);
            ValidateLength(message.SenderName, nameof(message.SenderName), 256, required: false);
            ValidateLength(message.ReceiverId, nameof(message.ReceiverId), 512, required: false);
            ValidateLength(message.ReceiverName, nameof(message.ReceiverName), 256, required: false);
            ValidateLength(message.Message, nameof(message.Message), 1_048_576, required: false);
            ValidateLength(message.FileId, nameof(message.FileId), 64, required: false);
            ValidateLength(message.FileName, nameof(message.FileName), 512, required: false);
            ValidateLength(message.LocalFilePath, nameof(message.LocalFilePath), 32_767, required: false);
            if (message.FileSize is < 0 or > FileTransferService.MaxFileSizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(message.FileSize));
            }

            DateTime utcNow = DateTime.UtcNow;
            DateTime sendTime = HistoryTimestampPolicy.NormalizeForStorage(
                message.SendTime,
                message.IsMine,
                utcNow);
            string conversationId = EmptyFallback(message.ConversationId, "unknown");
            (string messageLookupKey, string conversationLookupKey) =
                _databaseService.CreateHistoryLookupTokens(messageId, conversationId);

            using SqliteConnection connection = _databaseService.GetConnection();
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ChatMessages
                (
                    MessageId,
                    MessageLookupKey,
                    MessageType,
                    ConversationId,
                    ConversationLookupKey,
                    SenderId,
                    SenderName,
                    ReceiverId,
                    ReceiverName,
                    Message,
                    SendTime,
                    IsMine,
                    IsGroup,
                    FileId,
                    FileName,
                    LocalFilePath,
                    FileSize
                )
                VALUES
                (
                    @MessageId,
                    @MessageLookupKey,
                    @MessageType,
                    @ConversationId,
                    @ConversationLookupKey,
                    @SenderId,
                    @SenderName,
                    @ReceiverId,
                    @ReceiverName,
                    @Message,
                    @SendTime,
                    @IsMine,
                    @IsGroup,
                    @FileId,
                    @FileName,
                    @LocalFilePath,
                    @FileSize
                );
                """;

            command.Parameters.AddWithValue(
                "@MessageId",
                UserDataProtection.ProtectString(messageId));
            command.Parameters.AddWithValue("@MessageLookupKey", messageLookupKey);
            command.Parameters.AddWithValue(
                "@MessageType",
                UserDataProtection.ProtectString(EmptyFallback(message.MessageType, "chat")));
            command.Parameters.AddWithValue(
                "@ConversationId",
                UserDataProtection.ProtectString(conversationId));
            command.Parameters.AddWithValue("@ConversationLookupKey", conversationLookupKey);
            command.Parameters.AddWithValue(
                "@SenderId",
                UserDataProtection.ProtectString(message.SenderId ?? ""));
            command.Parameters.AddWithValue(
                "@SenderName",
                UserDataProtection.ProtectString(message.SenderName ?? ""));
            command.Parameters.AddWithValue(
                "@ReceiverId",
                UserDataProtection.ProtectString(message.ReceiverId ?? ""));
            command.Parameters.AddWithValue(
                "@ReceiverName",
                UserDataProtection.ProtectString(message.ReceiverName ?? ""));
            command.Parameters.AddWithValue("@Message", UserDataProtection.ProtectString(message.Message ?? ""));
            command.Parameters.AddWithValue("@SendTime", sendTime.ToString("O"));
            command.Parameters.AddWithValue(
                "@IsMine",
                UserDataProtection.ProtectString(message.IsMine ? "1" : "0"));
            command.Parameters.AddWithValue(
                "@IsGroup",
                UserDataProtection.ProtectString(message.IsGroup ? "1" : "0"));
            command.Parameters.AddWithValue(
                "@FileId",
                DbValue(message.FileId == null ? null : UserDataProtection.ProtectString(message.FileId)));
            command.Parameters.AddWithValue(
                "@FileName",
                DbValue(message.FileName == null ? null : UserDataProtection.ProtectString(message.FileName)));
            command.Parameters.AddWithValue(
                "@LocalFilePath",
                DbValue(message.LocalFilePath == null ? null : UserDataProtection.ProtectString(message.LocalFilePath)));
            command.Parameters.AddWithValue(
                "@FileSize",
                message.FileSize.HasValue
                    ? UserDataProtection.ProtectString(message.FileSize.Value.ToString(CultureInfo.InvariantCulture))
                    : DBNull.Value);

            try
            {
                command.ExecuteNonQuery();
                PruneConversation(connection, transaction, conversationLookupKey);
                PruneExpiredHistory(connection, transaction, utcNow);
                transaction.Commit();
                message.MessageId = messageId;
                message.SendTime = sendTime;
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
            {
                return false;
            }
        }

        private static void PruneConversation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string conversationLookupKey)
        {
            using SqliteCommand prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText =
                """
                DELETE FROM ChatMessages
                WHERE Id IN
                (
                    SELECT Id
                    FROM ChatMessages
                    WHERE ConversationLookupKey = @ConversationLookupKey
                    ORDER BY SendTime DESC, Id DESC
                    LIMIT -1 OFFSET @Maximum
                );
                """;
            prune.Parameters.AddWithValue("@ConversationLookupKey", conversationLookupKey);
            prune.Parameters.AddWithValue("@Maximum", DatabaseService.MaxMessagesPerConversation);
            prune.ExecuteNonQuery();
        }

        private static void PruneExpiredHistory(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DateTime utcNow)
        {
            using SqliteCommand prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText =
                "DELETE FROM ChatMessages WHERE SendTime < @Cutoff;";
            prune.Parameters.AddWithValue(
                "@Cutoff",
                utcNow.Subtract(DatabaseService.MessageRetention).ToString("O"));
            prune.ExecuteNonQuery();
        }

        public List<ChatMessage> GetMessages() => GetRecentMessages(DefaultGlobalLoadLimit);

        public List<ChatMessage> GetRecentMessages(int limit = DefaultGlobalLoadLimit)
        {
            int safeLimit = ValidateLimit(limit);
            using SqliteConnection connection = _databaseService.GetConnection();
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT {SelectColumns}
                FROM
                (
                    SELECT {BoundedSelectColumns}
                    FROM ChatMessages
                    ORDER BY SendTime DESC, Id DESC
                    LIMIT @Limit
                )
                ORDER BY SendTime ASC, Id ASC;
                """;
            command.Parameters.AddWithValue("@Limit", safeLimit);
            return ReadMessages(command);
        }

        public List<ChatMessage> GetMessages(string conversationId) =>
            GetMessages(conversationId, DefaultConversationLoadLimit);

        public List<ChatMessage> GetMessages(string conversationId, int limit)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return new List<ChatMessage>();
            }

            int safeLimit = ValidateLimit(limit);
            string conversationLookupKey = _databaseService.CreateConversationLookupToken(
                conversationId.Trim());
            using SqliteConnection connection = _databaseService.GetConnection();
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT {SelectColumns}
                FROM
                (
                    SELECT {BoundedSelectColumns}
                    FROM ChatMessages
                    WHERE ConversationLookupKey = @ConversationLookupKey
                    ORDER BY SendTime DESC, Id DESC
                    LIMIT @Limit
                )
                ORDER BY SendTime ASC, Id ASC;
                """;
            command.Parameters.AddWithValue("@ConversationLookupKey", conversationLookupKey);
            command.Parameters.AddWithValue("@Limit", safeLimit);
            return ReadMessages(command);
        }

        private static List<ChatMessage> ReadMessages(SqliteCommand command)
        {
            var messages = new List<ChatMessage>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    // INTEGER PRIMARY KEY normally guarantees this cannot happen,
                    // but an out-of-range row must not make every other message unreadable.
                    continue;
                }

                long storedId = reader.GetInt64(0);
                if (storedId is <= 0 or > int.MaxValue)
                {
                    continue;
                }
                int id = (int)storedId;
                messages.Add(new ChatMessage
                {
                    Id = id,
                    MessageId = UnprotectOrFallback(ReadStringOrNull(reader, 1), $"corrupt:{id}"),
                    MessageType = UnprotectOrFallback(ReadStringOrNull(reader, 2), "unreadable"),
                    ConversationId = UnprotectOrFallback(ReadStringOrNull(reader, 3), $"unknown:corrupt:{id}"),
                    SenderId = UnprotectOrFallback(ReadStringOrNull(reader, 4), ""),
                    SenderName = UnprotectOrFallback(ReadStringOrNull(reader, 5), UnreadableHistoryPlaceholder),
                    ReceiverId = UnprotectOrFallback(ReadStringOrNull(reader, 6), ""),
                    ReceiverName = UnprotectOrFallback(ReadStringOrNull(reader, 7), UnreadableHistoryPlaceholder),
                    Message = UnprotectOrFallback(ReadStringOrNull(reader, 8), UnreadableHistoryPlaceholder),
                    SendTime = ParseSendTime(ReadStringOrNull(reader, 9)),
                    IsMine = UnprotectBooleanOrFalse(ReadStringOrNull(reader, 10)),
                    IsGroup = UnprotectBooleanOrFalse(ReadStringOrNull(reader, 11)),
                    FileId = reader.IsDBNull(12) ? null : UnprotectOrNull(reader.GetString(12)),
                    FileName = reader.IsDBNull(13)
                        ? null
                        : UnprotectOrFallback(reader.GetString(13), UnreadableHistoryPlaceholder),
                    LocalFilePath = reader.IsDBNull(14) ? null : UnprotectOrNull(reader.GetString(14)),
                    FileSize = reader.IsDBNull(15)
                        ? null
                        : UnprotectFileSizeOrNull(reader.GetString(15))
                });
            }

            return messages;
        }

        private static DateTime ParseSendTime(string? value)
        {
            DateTime parsed = DateTime.TryParse(
                value ?? "",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTime result)
                ? result
                : DateTime.UtcNow;
            return HistoryTimestampPolicy.NormalizePersistedForDisplay(parsed, DateTime.UtcNow);
        }

        private static string? ReadStringOrNull(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static string UnprotectOrFallback(string? value, string fallback)
        {
            try
            {
                if (!UserDataProtection.IsProtected(value))
                {
                    throw new CryptographicException(
                        "A migrated history field is unexpectedly plaintext.");
                }
                return UserDataProtection.UnprotectString(value!);
            }
            catch
            {
                return fallback;
            }
        }

        private static string? UnprotectOrNull(string value)
        {
            try
            {
                if (!UserDataProtection.IsProtected(value))
                {
                    return null;
                }
                return UserDataProtection.UnprotectString(value);
            }
            catch
            {
                return null;
            }
        }

        private static bool UnprotectBooleanOrFalse(string? value)
        {
            try
            {
                if (!UserDataProtection.IsProtected(value))
                {
                    return false;
                }
                return string.Equals(
                    UserDataProtection.UnprotectString(value ?? ""),
                    "1",
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static long? UnprotectFileSizeOrNull(string? value)
        {
            try
            {
                if (!UserDataProtection.IsProtected(value))
                {
                    return null;
                }
                string plaintext = UserDataProtection.UnprotectString(value ?? "");
                return long.TryParse(
                           plaintext,
                           NumberStyles.None,
                           CultureInfo.InvariantCulture,
                           out long parsed) &&
                       parsed >= 0 && parsed <= FileTransferService.MaxFileSizeBytes
                    ? parsed
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static int ValidateLimit(int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "The history load limit must be positive.");
            }

            return Math.Min(limit, MaximumLoadLimit);
        }

        private static object DbValue(string? value) => value == null ? DBNull.Value : value;

        private static string EmptyFallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static void ValidateLength(
            string? value,
            string parameterName,
            int maximumLength,
            bool required)
        {
            if (required && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("The value is required.", parameterName);
            }

            if (value?.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"The value exceeds the {maximumLength} character limit.",
                    parameterName);
            }
        }

        private const string SelectColumns =
            "Id, MessageId, MessageType, ConversationId, SenderId, SenderName, " +
            "ReceiverId, ReceiverName, Message, SendTime, IsMine, IsGroup, " +
            "FileId, FileName, LocalFilePath, FileSize";

        // CASE expressions keep corrupt SQLite types and attacker-sized cells inside
        // SQLite. The managed reader receives NULL placeholders instead of allocating
        // an unbounded BLOB/string or throwing halfway through a conversation.
        private const string BoundedSelectColumns =
            "CASE WHEN typeof(Id) = 'integer' AND Id BETWEEN 1 AND 2147483647 THEN Id END AS Id, " +
            "CASE WHEN typeof(MessageId) = 'text' AND length(MessageId) <= 16384 THEN MessageId END AS MessageId, " +
            "CASE WHEN typeof(MessageType) = 'text' AND length(MessageType) <= 4096 THEN MessageType END AS MessageType, " +
            "CASE WHEN typeof(ConversationId) = 'text' AND length(ConversationId) <= 32768 THEN ConversationId END AS ConversationId, " +
            "CASE WHEN typeof(SenderId) = 'text' AND length(SenderId) <= 32768 THEN SenderId END AS SenderId, " +
            "CASE WHEN typeof(SenderName) = 'text' AND length(SenderName) <= 16384 THEN SenderName END AS SenderName, " +
            "CASE WHEN typeof(ReceiverId) = 'text' AND length(ReceiverId) <= 32768 THEN ReceiverId END AS ReceiverId, " +
            "CASE WHEN typeof(ReceiverName) = 'text' AND length(ReceiverName) <= 16384 THEN ReceiverName END AS ReceiverName, " +
            "CASE WHEN typeof(Message) = 'text' AND length(Message) <= 8388608 THEN Message END AS Message, " +
            "CASE WHEN typeof(SendTime) = 'text' AND length(SendTime) <= 64 THEN SendTime END AS SendTime, " +
            "CASE WHEN typeof(IsMine) = 'text' AND length(IsMine) <= 4096 THEN IsMine END AS IsMine, " +
            "CASE WHEN typeof(IsGroup) = 'text' AND length(IsGroup) <= 4096 THEN IsGroup END AS IsGroup, " +
            "CASE WHEN FileId IS NULL OR (typeof(FileId) = 'text' AND length(FileId) <= 8192) THEN FileId END AS FileId, " +
            "CASE WHEN FileName IS NULL OR (typeof(FileName) = 'text' AND length(FileName) <= 32768) THEN FileName END AS FileName, " +
            "CASE WHEN LocalFilePath IS NULL OR (typeof(LocalFilePath) = 'text' AND length(LocalFilePath) <= 524288) THEN LocalFilePath END AS LocalFilePath, " +
            "CASE WHEN FileSize IS NULL OR (typeof(FileSize) = 'text' AND length(FileSize) <= 4096) THEN FileSize END AS FileSize";
    }
}
