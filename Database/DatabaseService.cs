using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using direct_module.Services;
using Microsoft.Data.Sqlite;

namespace direct_module.Database
{
    public sealed class HistoryProtectionUnavailableException : CryptographicException
    {
        public HistoryProtectionUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public sealed class DatabaseService
    {
        public const int MaxMessagesPerConversation = 5_000;
        public static readonly TimeSpan MessageRetention = TimeSpan.FromDays(365);

        private const int SensitiveMigrationBatchSize = 256;
        private const int MaxLegacyAttachmentMappingsPerRun = 10_000;
        private const int MaxStoredAttachmentPathCharacters = 128 * 1024;
        private const long MaxLegacyAttachmentMigrationBytesPerRun = 500L * 1024 * 1024;
        private const string LegacyAttachmentPathMarker = "LegacyAttachmentPathsMigratedV2";
        private const string HistoryLookupKeyMetadataName = "HistoryLookupHmacKeyV1";
        private const string HistoryLookupMigrationMarker = "HistoryLookupMetadataProtectedV1";
        private const int HistoryLookupKeyBytes = 32;
        private const int HistoryLookupTokenCharacters = 64;
        private const int MaxStoredMessageIdentifierCharacters = 16 * 1024;
        private const int MaxStoredConversationIdentifierCharacters = 32 * 1024;
        private static readonly TimeSpan DatabaseInitializationLockTimeout = TimeSpan.FromMinutes(2);
        private const string MessageLookupDomain = "message-id";
        private const string ConversationLookupDomain = "conversation-id";
        private const string SensitiveHistoryProjection =
            "CASE WHEN typeof(MessageType) = 'text' AND length(MessageType) <= 4096 " +
            "THEN MessageType ELSE 'unreadable' END, " +
            "CASE WHEN typeof(SenderId) = 'text' AND length(SenderId) <= 32768 " +
            "THEN SenderId ELSE '' END, " +
            "CASE WHEN typeof(SenderName) = 'text' AND length(SenderName) <= 16384 " +
            "THEN SenderName ELSE '[unreadable history]' END, " +
            "CASE WHEN typeof(ReceiverId) = 'text' AND length(ReceiverId) <= 32768 " +
            "THEN ReceiverId ELSE '' END, " +
            "CASE WHEN typeof(ReceiverName) = 'text' AND length(ReceiverName) <= 16384 " +
            "THEN ReceiverName ELSE '[unreadable history]' END, " +
            "CASE WHEN typeof(Message) = 'text' AND length(Message) <= 8388608 " +
            "THEN Message ELSE '[unreadable history]' END, " +
            "CASE WHEN typeof(IsMine) = 'text' AND length(IsMine) <= 4096 THEN IsMine " +
            "WHEN typeof(IsMine) = 'integer' THEN CAST(IsMine AS TEXT) ELSE '0' END, " +
            "CASE WHEN typeof(IsGroup) = 'text' AND length(IsGroup) <= 4096 THEN IsGroup " +
            "WHEN typeof(IsGroup) = 'integer' THEN CAST(IsGroup AS TEXT) ELSE '0' END, " +
            "CASE WHEN FileId IS NULL THEN NULL WHEN typeof(FileId) = 'text' " +
            "AND length(FileId) <= 8192 THEN FileId END, " +
            "CASE WHEN FileName IS NULL THEN NULL WHEN typeof(FileName) = 'text' " +
            "AND length(FileName) <= 32768 THEN FileName END, " +
            "CASE WHEN LocalFilePath IS NULL THEN NULL WHEN typeof(LocalFilePath) = 'text' " +
            "AND length(LocalFilePath) <= 524288 THEN LocalFilePath END, " +
            "CASE WHEN FileSize IS NULL THEN NULL " +
            "WHEN typeof(FileSize) = 'text' AND length(FileSize) <= 4096 THEN FileSize " +
            "WHEN typeof(FileSize) = 'integer' THEN CAST(FileSize AS TEXT) END";
        private static readonly byte[] HistoryLookupContext =
            Encoding.UTF8.GetBytes("Aanchob.WiFiDirect_module.history-lookup.v1\0");
        private static readonly object InitializationGate = new();
        private readonly string _connectionString;
        private string _protectedHistoryLookupKey = "";

        public string DatabasePath { get; }

        public DatabaseService()
            : this(ResolveDatabasePath(), migrateLegacyDatabase: true)
        {
        }

        public DatabaseService(string databasePath)
            : this(databasePath, migrateLegacyDatabase: false)
        {
        }

        private DatabaseService(string databasePath, bool migrateLegacyDatabase)
        {
            if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath))
            {
                throw new ArgumentException("An absolute database path is required.", nameof(databasePath));
            }

            string dbPath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            RejectManagedDatabaseReparsePath(dbPath);
            lock (InitializationGate)
            {
                using FileStream initializationLock = AcquireDatabaseInitializationLock(dbPath);
                if (migrateLegacyDatabase)
                {
                    MigrateLegacyDataIfNeeded(dbPath);
                }

                RejectManagedDatabaseReparsePath(dbPath);

                DatabasePath = dbPath;
                _connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                    Pooling = true,
                    DefaultTimeout = 5
                }.ToString();

                Initialize();
            }
            Debug.WriteLine($"SQLite DB Path = {dbPath}");
        }

        public SqliteConnection GetConnection() => new(_connectionString);

        internal (string MessageToken, string ConversationToken) CreateHistoryLookupTokens(
            string messageId,
            string conversationId)
        {
            byte[] key = GetHistoryLookupKeyBytes(_protectedHistoryLookupKey);
            try
            {
                return (
                    CreateHistoryLookupToken(MessageLookupDomain, messageId, key),
                    CreateHistoryLookupToken(ConversationLookupDomain, conversationId, key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        internal string CreateConversationLookupToken(string conversationId)
        {
            byte[] key = GetHistoryLookupKeyBytes(_protectedHistoryLookupKey);
            try
            {
                return CreateHistoryLookupToken(ConversationLookupDomain, conversationId, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        internal static bool HistoryLookupTokensEqual(string first, string second)
        {
            if (first?.Length != HistoryLookupTokenCharacters ||
                second?.Length != HistoryLookupTokenCharacters)
            {
                return false;
            }

            byte[]? firstBytes = null;
            byte[]? secondBytes = null;
            try
            {
                firstBytes = Convert.FromHexString(first);
                secondBytes = Convert.FromHexString(second);
                return CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
            }
            catch (FormatException)
            {
                return false;
            }
            finally
            {
                if (firstBytes != null)
                {
                    CryptographicOperations.ZeroMemory(firstBytes);
                }
                if (secondBytes != null)
                {
                    CryptographicOperations.ZeroMemory(secondBytes);
                }
            }
        }

        private void Initialize()
        {
            using var connection = GetConnection();
            connection.Open();

            ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
            ExecuteNonQuery(connection, "PRAGMA secure_delete=ON;");
            ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");

            ExecuteNonQuery(connection,
                """
                CREATE TABLE IF NOT EXISTS AppMetadata
                (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ChatMessages
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MessageId TEXT NOT NULL,
                    MessageType TEXT NOT NULL DEFAULT 'chat',
                    ConversationId TEXT NOT NULL,
                    SenderId TEXT NOT NULL,
                    SenderName TEXT NOT NULL,
                    ReceiverId TEXT NOT NULL,
                    ReceiverName TEXT NOT NULL,
                    Message TEXT NOT NULL,
                    SendTime TEXT NOT NULL,
                    IsMine INTEGER NOT NULL,
                    IsGroup INTEGER NOT NULL DEFAULT 0,
                    FileId TEXT NULL,
                    FileName TEXT NULL,
                    LocalFilePath TEXT NULL,
                    FileSize INTEGER NULL
                );
                """);

            EnsureColumn(connection, "ChatMessages", "MessageId", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "ChatMessages", "MessageType", "TEXT NOT NULL DEFAULT 'chat'");
            EnsureColumn(connection, "ChatMessages", "IsGroup", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "ChatMessages", "FileId", "TEXT NULL");
            EnsureColumn(connection, "ChatMessages", "FileName", "TEXT NULL");
            EnsureColumn(connection, "ChatMessages", "LocalFilePath", "TEXT NULL");
            EnsureColumn(connection, "ChatMessages", "FileSize", "INTEGER NULL");
            EnsureColumn(connection, "ChatMessages", "MessageLookupKey", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "ChatMessages", "ConversationLookupKey", "TEXT NOT NULL DEFAULT ''");

            ExecuteNonQuery(connection,
                "UPDATE ChatMessages " +
                "SET MessageId = 'legacy:' || Id || ':' || lower(hex(randomblob(16))) " +
                "WHERE substr(MessageId, 1, 9) <> 'dpapi:v1:' AND " +
                "(typeof(MessageId) <> 'text' OR trim(MessageId) = '' OR length(MessageId) > 128 " +
                "OR Id NOT IN " +
                "(SELECT MIN(Id) FROM ChatMessages " +
                " WHERE typeof(MessageId) = 'text' AND substr(MessageId, 1, 9) <> 'dpapi:v1:' " +
                " AND trim(MessageId) <> '' AND length(MessageId) <= 128 " +
                " GROUP BY MessageId));" +
                "UPDATE ChatMessages SET IsGroup = 1 " +
                "WHERE substr(ConversationId, 1, 9) <> 'dpapi:v1:' " +
                "AND (lower(ConversationId) = 'group' OR lower(ConversationId) LIKE 'group:%');");

            NormalizeLegacySendTimes(connection);
            RepairInvalidSendTimes(connection);

            _protectedHistoryLookupKey = GetOrCreateHistoryLookupKey(connection);
            bool protectedLookupMetadata = ProtectHistoryLookupMetadata(
                connection,
                _protectedHistoryLookupKey);

            ExecuteNonQuery(connection,
                """
                DROP INDEX IF EXISTS UX_ChatMessages_MessageId;
                DROP INDEX IF EXISTS UX_ChatMessages_MessageLookupKey;
                DROP INDEX IF EXISTS IX_ChatMessages_Conversation_SendTime;
                CREATE UNIQUE INDEX IF NOT EXISTS UX_ChatMessages_MessageLookupKeyV2
                    ON ChatMessages(MessageLookupKey)
                    WHERE MessageLookupKey <> '';
                CREATE INDEX IF NOT EXISTS IX_ChatMessages_ConversationLookup_SendTime
                    ON ChatMessages(ConversationLookupKey, SendTime DESC, Id DESC);
                CREATE INDEX IF NOT EXISTS IX_ChatMessages_SendTime
                    ON ChatMessages(SendTime DESC, Id DESC);
                """);

            PruneHistory(connection);
            bool protectedLegacyValues = ProtectLegacySensitiveValues(connection);
            bool protectedLegacyUsers = ProtectLegacyUsers(connection);
            CreateHistoryProtectionTriggers(connection);
            if (protectedLookupMetadata || protectedLegacyValues || protectedLegacyUsers)
            {
                TryVacuumAfterLegacyProtection(connection);
            }
            ExecuteNonQuery(connection, "PRAGMA optimize;");
            // PRAGMA optimize may update planner statistics, so it must precede the
            // final checkpoint or it can immediately repopulate the WAL we just scrubbed.
            TryTruncateWriteAheadLog(connection);
            Debug.WriteLine("SQLite initialization and migration completed.");
        }

        private static string GetOrCreateHistoryLookupKey(SqliteConnection connection)
        {
            string? protectedKey;
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = @Key LIMIT 1;";
                read.Parameters.AddWithValue("@Key", HistoryLookupKeyMetadataName);
                protectedKey = read.ExecuteScalar()?.ToString();
            }

            if (protectedKey != null)
            {
                byte[] validated = GetHistoryLookupKeyBytes(protectedKey);
                CryptographicOperations.ZeroMemory(validated);
                return protectedKey;
            }

            using (SqliteCommand protectedState = connection.CreateCommand())
            {
                protectedState.CommandText =
                    """
                    SELECT 1
                    FROM ChatMessages
                    WHERE substr(MessageId, 1, 9) = 'dpapi:v1:'
                       OR substr(ConversationId, 1, 9) = 'dpapi:v1:'
                       OR MessageLookupKey <> ''
                       OR ConversationLookupKey <> ''
                    LIMIT 1;
                    """;
                if (protectedState.ExecuteScalar() != null)
                {
                    throw new HistoryProtectionUnavailableException(
                        "The protected history lookup key is missing. A replacement key will not be generated because it would silently orphan existing history indexes.");
                }
            }

            byte[] generated = RandomNumberGenerator.GetBytes(HistoryLookupKeyBytes);
            try
            {
                string candidate = UserDataProtection.ProtectBytesToString(generated);
                using SqliteTransaction transaction = connection.BeginTransaction();
                using (SqliteCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT OR IGNORE INTO AppMetadata(Key, Value) VALUES (@Key, @Value);";
                    insert.Parameters.AddWithValue("@Key", HistoryLookupKeyMetadataName);
                    insert.Parameters.AddWithValue("@Value", candidate);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();

                using SqliteCommand winner = connection.CreateCommand();
                winner.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = @Key LIMIT 1;";
                winner.Parameters.AddWithValue("@Key", HistoryLookupKeyMetadataName);
                protectedKey = winner.ExecuteScalar()?.ToString();
                if (string.IsNullOrEmpty(protectedKey))
                {
                    throw new HistoryProtectionUnavailableException(
                        "The protected history lookup key could not be persisted.");
                }

                byte[] validated = GetHistoryLookupKeyBytes(protectedKey);
                CryptographicOperations.ZeroMemory(validated);
                return protectedKey;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }
        }

        private static byte[] GetHistoryLookupKeyBytes(string protectedKey)
        {
            try
            {
                if (!UserDataProtection.IsProtected(protectedKey))
                {
                    throw new CryptographicException("The history lookup key is not DPAPI protected.");
                }

                byte[] key = UserDataProtection.UnprotectBytesFromString(protectedKey);
                if (key.Length == HistoryLookupKeyBytes)
                {
                    return key;
                }

                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException("The history lookup key has an invalid length.");
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or
                DecoderFallbackException)
            {
                throw new HistoryProtectionUnavailableException(
                    "The protected history lookup key is corrupt or unavailable. History remains fail-closed.",
                    ex);
            }
        }

        private static bool ProtectHistoryLookupMetadata(
            SqliteConnection connection,
            string protectedKey)
        {
            byte[] key = GetHistoryLookupKeyBytes(protectedKey);
            bool changed = false;
            try
            {
                long lastId = 0;
                while (true)
                {
                    var rows = new List<HistoryLookupRow>(SensitiveMigrationBatchSize);
                    using (SqliteCommand select = connection.CreateCommand())
                    {
                        select.CommandText =
                            """
                            SELECT Id,
                                   CASE WHEN typeof(MessageId) = 'text'
                                                  AND length(MessageId) <= @MaxMessageIdLength
                                        THEN MessageId END,
                                   CASE WHEN typeof(ConversationId) = 'text'
                                                  AND length(ConversationId) <= @MaxConversationIdLength
                                        THEN ConversationId END,
                                   CASE WHEN typeof(MessageLookupKey) = 'text'
                                                  AND length(MessageLookupKey) <= @MaxTokenLength
                                        THEN MessageLookupKey END,
                                   CASE WHEN typeof(ConversationLookupKey) = 'text'
                                                  AND length(ConversationLookupKey) <= @MaxTokenLength
                                        THEN ConversationLookupKey END
                            FROM ChatMessages
                            WHERE Id > @LastId
                            ORDER BY Id
                            LIMIT @BatchSize;
                            """;
                        select.Parameters.AddWithValue("@LastId", lastId);
                        select.Parameters.AddWithValue("@BatchSize", SensitiveMigrationBatchSize);
                        select.Parameters.AddWithValue(
                            "@MaxMessageIdLength",
                            MaxStoredMessageIdentifierCharacters);
                        select.Parameters.AddWithValue(
                            "@MaxConversationIdLength",
                            MaxStoredConversationIdentifierCharacters);
                        select.Parameters.AddWithValue("@MaxTokenLength", HistoryLookupTokenCharacters);
                        using SqliteDataReader reader = select.ExecuteReader();
                        while (reader.Read())
                        {
                            try
                            {
                                rows.Add(new HistoryLookupRow(
                                    reader.GetInt64(0),
                                    reader.IsDBNull(1) ? null : reader.GetString(1),
                                    reader.IsDBNull(2) ? null : reader.GetString(2),
                                    reader.IsDBNull(3) ? null : reader.GetString(3),
                                    reader.IsDBNull(4) ? null : reader.GetString(4)));
                            }
                            catch (Exception ex) when (ex is InvalidCastException or OverflowException)
                            {
                                throw new HistoryProtectionUnavailableException(
                                    "A history lookup field has an invalid SQLite storage type.",
                                    ex);
                            }
                        }
                    }

                    if (rows.Count == 0)
                    {
                        break;
                    }

                    var updates = new List<HistoryLookupUpdate>(rows.Count);
                    var claimedMessageTokens = new HashSet<string>(StringComparer.Ordinal);
                    foreach (HistoryLookupRow row in rows)
                    {
                        bool recoveredMessageId = !TryUnprotectHistoryIdentifier(
                            row.StoredMessageId,
                            out string decodedMessageId);
                        bool recoveredConversationId = !TryUnprotectHistoryIdentifier(
                            row.StoredConversationId,
                            out string decodedConversationId);
                        if (recoveredMessageId)
                        {
                            decodedMessageId = $"legacy:message:{row.Id}:{Guid.NewGuid():N}";
                        }
                        if (recoveredConversationId)
                        {
                            decodedConversationId = $"legacy:conversation:{row.Id}";
                        }
                        string messageId = NormalizeHistoryIdentifier(
                            decodedMessageId,
                            MessageLookupDomain,
                            $"legacy:message:{row.Id}:{Guid.NewGuid():N}");
                        string conversationId = NormalizeHistoryIdentifier(
                            decodedConversationId,
                            ConversationLookupDomain,
                            $"legacy:conversation:{row.Id}");

                        string expectedMessageToken = CreateHistoryLookupToken(
                            MessageLookupDomain,
                            messageId,
                            key);
                        string expectedConversationToken = CreateHistoryLookupToken(
                            ConversationLookupDomain,
                            conversationId,
                            key);
                        ValidateExistingLookupToken(
                            row.MessageLookupKey,
                            expectedMessageToken,
                            "message",
                            allowReplacement: recoveredMessageId ||
                                !string.Equals(decodedMessageId, messageId, StringComparison.Ordinal));
                        ValidateExistingLookupToken(
                            row.ConversationLookupKey,
                            expectedConversationToken,
                            "conversation",
                            allowReplacement: recoveredConversationId ||
                                !string.Equals(decodedConversationId, conversationId, StringComparison.Ordinal));

                        // A previous version may still write plaintext rows with an
                        // empty lookup token after this database's partial unique index
                        // already exists. Canonical GUID spellings can then converge on
                        // one HMAC token. Allocate a replacement identity for every row
                        // after the oldest one before issuing UPDATE, otherwise the
                        // existing unique index aborts migration before duplicate repair.
                        if (!claimedMessageTokens.Add(expectedMessageToken) ||
                            IsHistoryMessageTokenClaimedByEarlierRow(
                                connection,
                                row.Id,
                                expectedMessageToken))
                        {
                            (messageId, expectedMessageToken) =
                                AllocateUniqueHistoryMessageIdentity(
                                    connection,
                                    row.Id,
                                    key,
                                    claimedMessageTokens);
                            claimedMessageTokens.Add(expectedMessageToken);
                        }

                        string newStoredMessageId =
                            row.StoredMessageId != null &&
                            UserDataProtection.IsProtected(row.StoredMessageId) &&
                            string.Equals(
                                decodedMessageId,
                                messageId,
                                StringComparison.Ordinal)
                                ? row.StoredMessageId
                                : UserDataProtection.ProtectString(messageId);
                        string newStoredConversationId =
                            row.StoredConversationId != null &&
                            UserDataProtection.IsProtected(row.StoredConversationId) &&
                            string.Equals(
                                decodedConversationId,
                                conversationId,
                                StringComparison.Ordinal)
                                ? row.StoredConversationId
                                : UserDataProtection.ProtectString(conversationId);

                        if (!string.Equals(row.StoredMessageId, newStoredMessageId, StringComparison.Ordinal) ||
                            !string.Equals(row.StoredConversationId, newStoredConversationId, StringComparison.Ordinal) ||
                            !string.Equals(row.MessageLookupKey, expectedMessageToken, StringComparison.Ordinal) ||
                            !string.Equals(row.ConversationLookupKey, expectedConversationToken, StringComparison.Ordinal))
                        {
                            updates.Add(new HistoryLookupUpdate(
                                row,
                                newStoredMessageId,
                                newStoredConversationId,
                                expectedMessageToken,
                                expectedConversationToken));
                        }
                    }

                    if (updates.Count > 0)
                    {
                        using SqliteTransaction transaction = connection.BeginTransaction();
                        foreach (HistoryLookupUpdate update in updates)
                        {
                            using SqliteCommand command = connection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText =
                                """
                                UPDATE ChatMessages
                                SET MessageId = @MessageId,
                                    ConversationId = @ConversationId,
                                    MessageLookupKey = @MessageLookupKey,
                                    ConversationLookupKey = @ConversationLookupKey
                                WHERE Id = @Id
                                  AND ((@OldMessageId IS NULL AND
                                        (typeof(MessageId) <> 'text' OR length(MessageId) > @MaxMessageIdLength))
                                       OR MessageId = @OldMessageId)
                                  AND ((@OldConversationId IS NULL AND
                                        (typeof(ConversationId) <> 'text' OR length(ConversationId) > @MaxConversationIdLength))
                                       OR ConversationId = @OldConversationId)
                                  AND ((@OldMessageLookupKey IS NULL AND
                                        (typeof(MessageLookupKey) <> 'text' OR length(MessageLookupKey) > @MaxTokenLength))
                                       OR MessageLookupKey = @OldMessageLookupKey)
                                  AND ((@OldConversationLookupKey IS NULL AND
                                        (typeof(ConversationLookupKey) <> 'text' OR length(ConversationLookupKey) > @MaxTokenLength))
                                       OR ConversationLookupKey = @OldConversationLookupKey);
                                """;
                            command.Parameters.AddWithValue("@MessageId", update.StoredMessageId);
                            command.Parameters.AddWithValue("@ConversationId", update.StoredConversationId);
                            command.Parameters.AddWithValue("@MessageLookupKey", update.MessageLookupKey);
                            command.Parameters.AddWithValue("@ConversationLookupKey", update.ConversationLookupKey);
                            command.Parameters.AddWithValue("@Id", update.Original.Id);
                            command.Parameters.AddWithValue(
                                "@OldMessageId",
                                (object?)update.Original.StoredMessageId ?? DBNull.Value);
                            command.Parameters.AddWithValue(
                                "@OldConversationId",
                                (object?)update.Original.StoredConversationId ?? DBNull.Value);
                            command.Parameters.AddWithValue(
                                "@OldMessageLookupKey",
                                (object?)update.Original.MessageLookupKey ?? DBNull.Value);
                            command.Parameters.AddWithValue(
                                "@OldConversationLookupKey",
                                (object?)update.Original.ConversationLookupKey ?? DBNull.Value);
                            command.Parameters.AddWithValue(
                                "@MaxMessageIdLength",
                                MaxStoredMessageIdentifierCharacters);
                            command.Parameters.AddWithValue(
                                "@MaxConversationIdLength",
                                MaxStoredConversationIdentifierCharacters);
                            command.Parameters.AddWithValue("@MaxTokenLength", HistoryLookupTokenCharacters);
                            if (command.ExecuteNonQuery() != 1 &&
                                !HistoryLookupRowAlreadyConvergedOrRemoved(
                                    connection,
                                    transaction,
                                    update))
                            {
                                throw new HistoryProtectionUnavailableException(
                                    "A history lookup row changed concurrently; migration was rolled back.");
                            }
                        }
                        transaction.Commit();
                        changed = true;
                    }

                    lastId = rows[^1].Id;
                }

                if (RepairDuplicateHistoryMessageTokens(connection, key))
                {
                    changed = true;
                }
                WriteMetadataMarkerTransaction(connection, HistoryLookupMigrationMarker);
                return changed;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        private static bool RepairDuplicateHistoryMessageTokens(
            SqliteConnection connection,
            ReadOnlySpan<byte> key)
        {
            var duplicates = new List<(long Id, string Token)>();
            using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText =
                    """
                    SELECT Id, MessageLookupKey
                    FROM ChatMessages
                    WHERE MessageLookupKey IN
                    (
                        SELECT MessageLookupKey
                        FROM ChatMessages
                        GROUP BY MessageLookupKey
                        HAVING COUNT(*) > 1
                    )
                    ORDER BY MessageLookupKey, Id;
                    """;
                using SqliteDataReader reader = select.ExecuteReader();
                while (reader.Read())
                {
                    duplicates.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }
            if (duplicates.Count == 0)
            {
                return false;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            string? previousToken = null;
            foreach ((long id, string token) in duplicates)
            {
                if (!string.Equals(previousToken, token, StringComparison.Ordinal))
                {
                    previousToken = token;
                    continue;
                }

                string replacementId = "";
                string replacementToken = "";
                bool allocated = false;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    replacementId = $"legacy:duplicate:{id}:{Guid.NewGuid():N}";
                    replacementToken = CreateHistoryLookupToken(
                        MessageLookupDomain,
                        replacementId,
                        key);
                    using SqliteCommand exists = connection.CreateCommand();
                    exists.Transaction = transaction;
                    exists.CommandText =
                        "SELECT 1 FROM ChatMessages WHERE MessageLookupKey = @Token LIMIT 1;";
                    exists.Parameters.AddWithValue("@Token", replacementToken);
                    if (exists.ExecuteScalar() == null)
                    {
                        allocated = true;
                        break;
                    }
                }
                if (!allocated)
                {
                    throw new HistoryProtectionUnavailableException(
                        "A unique replacement history identity could not be allocated.");
                }
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE ChatMessages
                    SET MessageId = @MessageId,
                        MessageLookupKey = @MessageLookupKey
                    WHERE Id = @Id AND MessageLookupKey = @OldMessageLookupKey;
                    """;
                update.Parameters.AddWithValue(
                    "@MessageId",
                    UserDataProtection.ProtectString(replacementId));
                update.Parameters.AddWithValue("@MessageLookupKey", replacementToken);
                update.Parameters.AddWithValue("@Id", id);
                update.Parameters.AddWithValue("@OldMessageLookupKey", token);
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new HistoryProtectionUnavailableException(
                        "A duplicate history identity changed concurrently; repair was rolled back.");
                }
            }
            transaction.Commit();
            return true;
        }

        private static bool HistoryLookupRowAlreadyConvergedOrRemoved(
            SqliteConnection connection,
            SqliteTransaction transaction,
            HistoryLookupUpdate expected)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT MessageId, ConversationId,
                       MessageLookupKey, ConversationLookupKey
                FROM ChatMessages
                WHERE Id = @Id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@Id", expected.Original.Id);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                // Concurrent retention/deletion already made this row irrelevant.
                return true;
            }
            if (reader.IsDBNull(0) || reader.IsDBNull(1) ||
                reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                return false;
            }

            return string.Equals(reader.GetString(0), expected.StoredMessageId, StringComparison.Ordinal) &&
                   string.Equals(reader.GetString(1), expected.StoredConversationId, StringComparison.Ordinal) &&
                   HistoryLookupTokensEqual(reader.GetString(2), expected.MessageLookupKey) &&
                   HistoryLookupTokensEqual(reader.GetString(3), expected.ConversationLookupKey);
        }

        private static bool IsHistoryMessageTokenClaimedByEarlierRow(
            SqliteConnection connection,
            long rowId,
            string token)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM ChatMessages
                WHERE MessageLookupKey = @Token AND Id < @Id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@Token", token);
            command.Parameters.AddWithValue("@Id", rowId);
            return command.ExecuteScalar() != null;
        }

        private static (string MessageId, string Token) AllocateUniqueHistoryMessageIdentity(
            SqliteConnection connection,
            long rowId,
            ReadOnlySpan<byte> key,
            ISet<string> claimedTokens)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string messageId = $"legacy:duplicate:{rowId}:{Guid.NewGuid():N}";
                string token = CreateHistoryLookupToken(
                    MessageLookupDomain,
                    messageId,
                    key);
                if (claimedTokens.Contains(token))
                {
                    continue;
                }

                using SqliteCommand exists = connection.CreateCommand();
                exists.CommandText =
                    "SELECT 1 FROM ChatMessages WHERE MessageLookupKey = @Token LIMIT 1;";
                exists.Parameters.AddWithValue("@Token", token);
                if (exists.ExecuteScalar() == null)
                {
                    return (messageId, token);
                }
            }

            throw new HistoryProtectionUnavailableException(
                "A unique replacement history identity could not be allocated.");
        }

        private static bool TryUnprotectHistoryIdentifier(string? storedValue, out string value)
        {
            value = "";
            if (storedValue == null)
            {
                return false;
            }
            try
            {
                value = UserDataProtection.UnprotectString(storedValue);
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
            {
                return false;
            }
        }

        private static void ValidateExistingLookupToken(
            string? existingToken,
            string expectedToken,
            string fieldName,
            bool allowReplacement)
        {
            if (string.IsNullOrEmpty(existingToken) || allowReplacement)
            {
                return;
            }
            if (!HistoryLookupTokensEqual(existingToken, expectedToken))
            {
                throw new HistoryProtectionUnavailableException(
                    $"The protected history {fieldName} lookup token does not match its encrypted value or lookup key.");
            }
        }

        private static string NormalizeHistoryIdentifier(
            string value,
            string domain,
            string? invalidFallback = null)
        {
            try
            {
                string normalized = (value ?? "").Trim().Normalize(NormalizationForm.FormC);
                if (string.Equals(domain, MessageLookupDomain, StringComparison.Ordinal) &&
                    Guid.TryParse(normalized, out Guid messageId))
                {
                    normalized = messageId.ToString("N");
                }

                int maximumLength = string.Equals(domain, MessageLookupDomain, StringComparison.Ordinal)
                    ? 128
                    : 512;
                if (normalized.Length == 0 || normalized.Length > maximumLength ||
                    normalized.Any(char.IsControl))
                {
                    throw new InvalidDataException("The history identifier is invalid.");
                }
                return normalized;
            }
            catch (Exception ex) when (invalidFallback != null &&
                ex is ArgumentException or InvalidDataException)
            {
                return invalidFallback;
            }
        }

        private static string CreateHistoryLookupToken(
            string domain,
            string value,
            ReadOnlySpan<byte> key)
        {
            string normalized = NormalizeHistoryIdentifier(value, domain);
            byte[] domainBytes = Encoding.UTF8.GetBytes(domain);
            byte[] valueBytes = new UTF8Encoding(false, true).GetBytes(normalized);
            byte[] input = new byte[checked(
                HistoryLookupContext.Length + domainBytes.Length + 1 + valueBytes.Length)];
            try
            {
                int offset = 0;
                HistoryLookupContext.CopyTo(input, offset);
                offset += HistoryLookupContext.Length;
                domainBytes.CopyTo(input, offset);
                offset += domainBytes.Length;
                input[offset++] = 0;
                valueBytes.CopyTo(input, offset);

                byte[] token = HMACSHA256.HashData(key, input);
                try
                {
                    return Convert.ToHexString(token);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(token);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(domainBytes);
                CryptographicOperations.ZeroMemory(valueBytes);
                CryptographicOperations.ZeroMemory(input);
            }
        }

        private static bool ProtectLegacySensitiveValues(SqliteConnection connection)
        {
            using (SqliteCommand status = connection.CreateCommand())
            {
                status.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = 'SensitiveValuesProtectedV4' LIMIT 1;";
                if (string.Equals(status.ExecuteScalar()?.ToString(), "1", StringComparison.Ordinal))
                {
                    using SqliteCommand unprotected = connection.CreateCommand();
                    unprotected.CommandText =
                        """
                        SELECT 1
                        FROM ChatMessages
                        WHERE typeof(MessageType) <> 'text' OR substr(MessageType, 1, 9) <> 'dpapi:v1:'
                           OR typeof(SenderId) <> 'text' OR substr(SenderId, 1, 9) <> 'dpapi:v1:'
                           OR typeof(SenderName) <> 'text' OR substr(SenderName, 1, 9) <> 'dpapi:v1:'
                           OR typeof(ReceiverId) <> 'text' OR substr(ReceiverId, 1, 9) <> 'dpapi:v1:'
                           OR typeof(ReceiverName) <> 'text' OR substr(ReceiverName, 1, 9) <> 'dpapi:v1:'
                           OR typeof(Message) <> 'text' OR substr(Message, 1, 9) <> 'dpapi:v1:'
                           OR typeof(IsMine) <> 'text' OR substr(IsMine, 1, 9) <> 'dpapi:v1:'
                           OR typeof(IsGroup) <> 'text' OR substr(IsGroup, 1, 9) <> 'dpapi:v1:'
                           OR (FileId IS NOT NULL AND
                               (typeof(FileId) <> 'text' OR substr(FileId, 1, 9) <> 'dpapi:v1:'))
                           OR (FileName IS NOT NULL AND
                               (typeof(FileName) <> 'text' OR substr(FileName, 1, 9) <> 'dpapi:v1:'))
                           OR (LocalFilePath IS NOT NULL AND
                               (typeof(LocalFilePath) <> 'text' OR substr(LocalFilePath, 1, 9) <> 'dpapi:v1:'))
                           OR (FileSize IS NOT NULL AND
                               (typeof(FileSize) <> 'text' OR substr(FileSize, 1, 9) <> 'dpapi:v1:'))
                        LIMIT 1;
                        """;
                    if (unprotected.ExecuteScalar() == null)
                    {
                        return false;
                    }
                }
            }

            long lastId = 0;
            bool firstBatch = true;
            bool changed = false;
            while (true)
            {
                var rows = new List<SensitiveRow>(SensitiveMigrationBatchSize);
                using (SqliteCommand select = connection.CreateCommand())
                {
                    select.CommandText = firstBatch
                        ? "SELECT Id, " + SensitiveHistoryProjection +
                          " FROM ChatMessages ORDER BY Id LIMIT @BatchSize;"
                        : "SELECT Id, " + SensitiveHistoryProjection +
                          " FROM ChatMessages WHERE Id > @LastId ORDER BY Id LIMIT @BatchSize;";
                    if (!firstBatch)
                    {
                        select.Parameters.AddWithValue("@LastId", lastId);
                    }
                    select.Parameters.AddWithValue("@BatchSize", SensitiveMigrationBatchSize);
                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(new SensitiveRow(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetString(6),
                            reader.GetString(7),
                            reader.GetString(8),
                            reader.IsDBNull(9) ? null : reader.GetString(9),
                            reader.IsDBNull(10) ? null : reader.GetString(10),
                            reader.IsDBNull(11) ? null : reader.GetString(11),
                            reader.IsDBNull(12) ? null : reader.GetString(12)));
                    }
                }

                if (rows.Count == 0)
                {
                    WriteMetadataMarkerTransaction(connection, "SensitiveValuesProtectedV4");
                    return changed;
                }

                using SqliteTransaction transaction = connection.BeginTransaction();
                foreach (SensitiveRow row in rows)
                {
                    string protectedMessageType = EnsureProtected(row.MessageType);
                    string protectedSenderId = EnsureProtected(row.SenderId);
                    string protectedSenderName = EnsureProtected(row.SenderName);
                    string protectedReceiverId = EnsureProtected(row.ReceiverId);
                    string protectedReceiverName = EnsureProtected(row.ReceiverName);
                    string protectedMessage = EnsureProtected(row.Message);
                    string protectedIsMine = EnsureProtected(row.IsMine);
                    string protectedIsGroup = EnsureProtected(row.IsGroup);
                    string? protectedFileId = row.FileId == null ? null : EnsureProtected(row.FileId);
                    string? protectedFileName = row.FileName == null ? null : EnsureProtected(row.FileName);
                    string? protectedLocalPath = row.LocalFilePath == null ? null : EnsureProtected(row.LocalFilePath);
                    string? protectedFileSize = row.FileSize == null ? null : EnsureProtected(row.FileSize);
                    if (string.Equals(row.MessageType, protectedMessageType, StringComparison.Ordinal) &&
                        string.Equals(row.SenderId, protectedSenderId, StringComparison.Ordinal) &&
                        string.Equals(row.SenderName, protectedSenderName, StringComparison.Ordinal) &&
                        string.Equals(row.ReceiverId, protectedReceiverId, StringComparison.Ordinal) &&
                        string.Equals(row.ReceiverName, protectedReceiverName, StringComparison.Ordinal) &&
                        string.Equals(row.Message, protectedMessage, StringComparison.Ordinal) &&
                        string.Equals(row.IsMine, protectedIsMine, StringComparison.Ordinal) &&
                        string.Equals(row.IsGroup, protectedIsGroup, StringComparison.Ordinal) &&
                        string.Equals(row.FileId, protectedFileId, StringComparison.Ordinal) &&
                        string.Equals(row.FileName, protectedFileName, StringComparison.Ordinal) &&
                        string.Equals(row.LocalFilePath, protectedLocalPath, StringComparison.Ordinal) &&
                        string.Equals(row.FileSize, protectedFileSize, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE ChatMessages
                        SET MessageType = @MessageType,
                            SenderId = @SenderId,
                            SenderName = @SenderName,
                            ReceiverId = @ReceiverId,
                            ReceiverName = @ReceiverName,
                            Message = @Message,
                            IsMine = @IsMine,
                            IsGroup = @IsGroup,
                            FileId = @FileId,
                            FileName = @FileName,
                            LocalFilePath = @LocalFilePath,
                            FileSize = @FileSize
                        WHERE Id = @Id;
                        """;
                    update.Parameters.AddWithValue("@MessageType", protectedMessageType);
                    update.Parameters.AddWithValue("@SenderId", protectedSenderId);
                    update.Parameters.AddWithValue("@SenderName", protectedSenderName);
                    update.Parameters.AddWithValue("@ReceiverId", protectedReceiverId);
                    update.Parameters.AddWithValue("@ReceiverName", protectedReceiverName);
                    update.Parameters.AddWithValue("@Message", protectedMessage);
                    update.Parameters.AddWithValue("@IsMine", protectedIsMine);
                    update.Parameters.AddWithValue("@IsGroup", protectedIsGroup);
                    update.Parameters.AddWithValue("@FileId", (object?)protectedFileId ?? DBNull.Value);
                    update.Parameters.AddWithValue("@FileName", (object?)protectedFileName ?? DBNull.Value);
                    update.Parameters.AddWithValue("@LocalFilePath", (object?)protectedLocalPath ?? DBNull.Value);
                    update.Parameters.AddWithValue("@FileSize", (object?)protectedFileSize ?? DBNull.Value);
                    update.Parameters.AddWithValue("@Id", row.Id);
                    update.ExecuteNonQuery();
                    changed = true;
                }
                transaction.Commit();
                firstBatch = false;
                lastId = rows[^1].Id;
            }
        }

        private static void TryVacuumAfterLegacyProtection(SqliteConnection connection)
        {
            try
            {
                ExecuteNonQuery(connection, "VACUUM;");
            }
            catch (SqliteException ex)
            {
                // Protection is already committed. A later launch retries WAL
                // truncation, and secure_delete prevents new plaintext remnants.
                Debug.WriteLine($"SQLite post-protection VACUUM skipped: {ex.Message}");
            }
        }

        private static bool ProtectLegacyUsers(SqliteConnection connection)
        {
            if (!TableExists(connection, "Users") ||
                !ColumnExists(connection, "Users", "DeviceId") ||
                !ColumnExists(connection, "Users", "MachineName") ||
                !ColumnExists(connection, "Users", "DisplayName") ||
                !ColumnExists(connection, "Users", "CreatedAt"))
            {
                return false;
            }

            using (SqliteCommand status = connection.CreateCommand())
            {
                status.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = 'LegacyUsersProtectedV1' LIMIT 1;";
                if (string.Equals(status.ExecuteScalar()?.ToString(), "1", StringComparison.Ordinal))
                {
                    using SqliteCommand unprotected = connection.CreateCommand();
                    unprotected.CommandText =
                        """
                        SELECT 1
                        FROM Users
                        WHERE typeof(DeviceId) <> 'text' OR substr(DeviceId, 1, 9) <> 'dpapi:v1:'
                           OR typeof(MachineName) <> 'text' OR substr(MachineName, 1, 9) <> 'dpapi:v1:'
                           OR typeof(DisplayName) <> 'text' OR substr(DisplayName, 1, 9) <> 'dpapi:v1:'
                        LIMIT 1;
                        """;
                    if (unprotected.ExecuteScalar() == null)
                    {
                        return false;
                    }
                }
            }

            bool changed = false;
            long lastRowId = 0;
            bool firstBatch = true;
            while (true)
            {
                var rows = new List<LegacyUserRow>(SensitiveMigrationBatchSize);
                using (SqliteCommand select = connection.CreateCommand())
                {
                    select.CommandText = firstBatch
                        ?
                        """
                        SELECT rowid, DeviceId, MachineName, DisplayName
                        FROM Users
                        ORDER BY rowid
                        LIMIT @BatchSize;
                        """
                        :
                        """
                        SELECT rowid, DeviceId, MachineName, DisplayName
                        FROM Users
                        WHERE rowid > @LastRowId
                        ORDER BY rowid
                        LIMIT @BatchSize;
                        """;
                    if (!firstBatch)
                    {
                        select.Parameters.AddWithValue("@LastRowId", lastRowId);
                    }
                    select.Parameters.AddWithValue("@BatchSize", SensitiveMigrationBatchSize);
                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(new LegacyUserRow(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3)));
                    }
                }

                if (rows.Count == 0)
                {
                    WriteMetadataMarkerTransaction(connection, "LegacyUsersProtectedV1");
                    return changed;
                }

                using SqliteTransaction transaction = connection.BeginTransaction();
                foreach (LegacyUserRow row in rows)
                {
                    string deviceId = EnsureProtected(row.DeviceId);
                    string machineName = EnsureProtected(row.MachineName);
                    string displayName = EnsureProtected(row.DisplayName);
                    if (string.Equals(deviceId, row.DeviceId, StringComparison.Ordinal) &&
                        string.Equals(machineName, row.MachineName, StringComparison.Ordinal) &&
                        string.Equals(displayName, row.DisplayName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE Users
                        SET DeviceId = @DeviceId,
                            MachineName = @MachineName,
                            DisplayName = @DisplayName
                        WHERE rowid = @RowId;
                        """;
                    update.Parameters.AddWithValue("@DeviceId", deviceId);
                    update.Parameters.AddWithValue("@MachineName", machineName);
                    update.Parameters.AddWithValue("@DisplayName", displayName);
                    update.Parameters.AddWithValue("@RowId", row.RowId);
                    update.ExecuteNonQuery();
                    changed = true;
                }
                transaction.Commit();
                firstBatch = false;
                lastRowId = rows[^1].RowId;
            }
        }

        private static void TryTruncateWriteAheadLog(SqliteConnection connection)
        {
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using SqliteDataReader reader = command.ExecuteReader();
                if (reader.Read() && reader.GetInt32(0) != 0)
                {
                    Debug.WriteLine("SQLite WAL truncation deferred because another connection is active.");
                }
            }
            catch (SqliteException ex)
            {
                Debug.WriteLine($"SQLite WAL truncation deferred: {ex.Message}");
            }
        }

        private static void NormalizeLegacySendTimes(SqliteConnection connection)
        {
            using (SqliteCommand status = connection.CreateCommand())
            {
                status.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = 'SendTimesCanonicalUtcV1' LIMIT 1;";
                if (string.Equals(status.ExecuteScalar()?.ToString(), "1", StringComparison.Ordinal))
                {
                    return;
                }
            }

            DateTime now = DateTime.UtcNow;
            DateTime latest = now + HistoryTimestampPolicy.MaximumFutureSkew;
            long lastId = 0;
            bool firstBatch = true;
            while (true)
            {
                var rows = new List<LegacyTimestampRow>(SensitiveMigrationBatchSize);
                using (SqliteCommand select = connection.CreateCommand())
                {
                    select.CommandText = firstBatch
                        ?
                        """
                        SELECT Id, SendTime
                        FROM ChatMessages
                        ORDER BY Id
                        LIMIT @BatchSize;
                        """
                        :
                        """
                        SELECT Id, SendTime
                        FROM ChatMessages
                        WHERE Id > @LastId
                        ORDER BY Id
                        LIMIT @BatchSize;
                        """;
                    if (!firstBatch)
                    {
                        select.Parameters.AddWithValue("@LastId", lastId);
                    }
                    select.Parameters.AddWithValue("@BatchSize", SensitiveMigrationBatchSize);
                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(new LegacyTimestampRow(reader.GetInt64(0), reader.GetString(1)));
                    }
                }

                if (rows.Count == 0)
                {
                    WriteMetadataMarkerTransaction(connection, "SendTimesCanonicalUtcV1");
                    return;
                }

                using SqliteTransaction transaction = connection.BeginTransaction();
                foreach (LegacyTimestampRow row in rows)
                {
                    DateTime normalized = now;
                    if (DateTimeOffset.TryParse(
                            row.SendTime,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces,
                            out DateTimeOffset parsed))
                    {
                        DateTime candidate = parsed.UtcDateTime;
                        if (candidate >= DateTime.UnixEpoch && candidate <= latest)
                        {
                            normalized = candidate;
                        }
                    }

                    string canonical = normalized.ToString("O", CultureInfo.InvariantCulture);
                    if (string.Equals(row.SendTime, canonical, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        "UPDATE ChatMessages SET SendTime = @SendTime WHERE Id = @Id;";
                    update.Parameters.AddWithValue("@SendTime", canonical);
                    update.Parameters.AddWithValue("@Id", row.Id);
                    update.ExecuteNonQuery();
                }
                transaction.Commit();
                firstBatch = false;
                lastId = rows[^1].Id;
            }
        }

        private static void RepairInvalidSendTimes(SqliteConnection connection)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE ChatMessages
                SET SendTime = @Now
                WHERE typeof(SendTime) <> 'text'
                   OR length(SendTime) > 64
                   OR julianday(SendTime) IS NULL;
                """;
            command.Parameters.AddWithValue(
                "@Now",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        private static string EnsureProtected(string value)
        {
            if (UserDataProtection.IsProtected(value))
            {
                try
                {
                    _ = UserDataProtection.UnprotectString(value);
                    return value;
                }
                catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
                {
                    // A ciphertext from another Windows user or a damaged value
                    // must never be double-protected. Leave it unchanged so the
                    // history reader can surface its recovery placeholder.
                    return value;
                }
            }

            return UserDataProtection.ProtectString(value);
        }

        private static void PruneHistory(SqliteConnection connection)
        {
            using (SqliteCommand byAge = connection.CreateCommand())
            {
                byAge.CommandText =
                    "DELETE FROM ChatMessages WHERE julianday(SendTime) < julianday(@Cutoff);";
                byAge.Parameters.AddWithValue("@Cutoff", DateTime.UtcNow.Subtract(MessageRetention).ToString("O"));
                byAge.ExecuteNonQuery();
            }

            using SqliteCommand byCount = connection.CreateCommand();
            byCount.CommandText =
                """
                DELETE FROM ChatMessages
                WHERE Id IN
                (
                    SELECT Id
                    FROM
                    (
                        SELECT Id,
                               ROW_NUMBER() OVER
                               (
                                    PARTITION BY ConversationLookupKey
                                   ORDER BY SendTime DESC, Id DESC
                               ) AS RowNumber
                        FROM ChatMessages
                    )
                    WHERE RowNumber > @Maximum
                );
                """;
            byCount.Parameters.AddWithValue("@Maximum", MaxMessagesPerConversation);
            byCount.ExecuteNonQuery();
        }

        private static void EnsureColumn(
            SqliteConnection connection,
            string tableName,
            string columnName,
            string declaration)
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info([{tableName}]);";
            using SqliteDataReader reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            reader.Close();
            try
            {
                ExecuteNonQuery(connection, $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {declaration};");
            }
            catch (SqliteException) when (ColumnExists(connection, tableName, columnName))
            {
                // A second process completed the same additive migration first.
            }
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info([{tableName}]);";
            using SqliteDataReader reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = @Name LIMIT 1;";
            command.Parameters.AddWithValue("@Name", tableName);
            return command.ExecuteScalar() != null;
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void CreateHistoryProtectionTriggers(SqliteConnection connection)
        {
            const string invalidProtectedRow =
                "typeof(NEW.MessageId) <> 'text' OR substr(NEW.MessageId, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.MessageLookupKey) <> 'text' OR length(NEW.MessageLookupKey) <> 64 " +
                "OR NEW.MessageLookupKey GLOB '*[^0-9A-F]*' " +
                "OR typeof(NEW.MessageType) <> 'text' OR substr(NEW.MessageType, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.ConversationId) <> 'text' OR substr(NEW.ConversationId, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.ConversationLookupKey) <> 'text' OR length(NEW.ConversationLookupKey) <> 64 " +
                "OR NEW.ConversationLookupKey GLOB '*[^0-9A-F]*' " +
                "OR typeof(NEW.SenderId) <> 'text' OR substr(NEW.SenderId, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.SenderName) <> 'text' OR substr(NEW.SenderName, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.ReceiverId) <> 'text' OR substr(NEW.ReceiverId, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.ReceiverName) <> 'text' OR substr(NEW.ReceiverName, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.Message) <> 'text' OR substr(NEW.Message, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.SendTime) <> 'text' OR length(NEW.SendTime) > 64 OR julianday(NEW.SendTime) IS NULL " +
                "OR typeof(NEW.IsMine) <> 'text' OR substr(NEW.IsMine, 1, 9) <> 'dpapi:v1:' " +
                "OR typeof(NEW.IsGroup) <> 'text' OR substr(NEW.IsGroup, 1, 9) <> 'dpapi:v1:' " +
                "OR (NEW.FileId IS NOT NULL AND " +
                "(typeof(NEW.FileId) <> 'text' OR substr(NEW.FileId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.FileName IS NOT NULL AND " +
                "(typeof(NEW.FileName) <> 'text' OR substr(NEW.FileName, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.LocalFilePath IS NOT NULL AND " +
                "(typeof(NEW.LocalFilePath) <> 'text' OR substr(NEW.LocalFilePath, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.FileSize IS NOT NULL AND " +
                "(typeof(NEW.FileSize) <> 'text' OR substr(NEW.FileSize, 1, 9) <> 'dpapi:v1:'))";
            const string invalidProtectedUpdate =
                "(NEW.MessageId IS NOT OLD.MessageId AND " +
                "(typeof(NEW.MessageId) <> 'text' OR substr(NEW.MessageId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.MessageLookupKey IS NOT OLD.MessageLookupKey AND " +
                "(typeof(NEW.MessageLookupKey) <> 'text' OR length(NEW.MessageLookupKey) <> 64 " +
                "OR NEW.MessageLookupKey GLOB '*[^0-9A-F]*')) " +
                "OR (NEW.MessageType IS NOT OLD.MessageType AND " +
                "(typeof(NEW.MessageType) <> 'text' OR substr(NEW.MessageType, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.ConversationId IS NOT OLD.ConversationId AND " +
                "(typeof(NEW.ConversationId) <> 'text' OR substr(NEW.ConversationId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.ConversationLookupKey IS NOT OLD.ConversationLookupKey AND " +
                "(typeof(NEW.ConversationLookupKey) <> 'text' OR length(NEW.ConversationLookupKey) <> 64 " +
                "OR NEW.ConversationLookupKey GLOB '*[^0-9A-F]*')) " +
                "OR (NEW.SenderId IS NOT OLD.SenderId AND " +
                "(typeof(NEW.SenderId) <> 'text' OR substr(NEW.SenderId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.SenderName IS NOT OLD.SenderName AND " +
                "(typeof(NEW.SenderName) <> 'text' OR substr(NEW.SenderName, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.ReceiverId IS NOT OLD.ReceiverId AND " +
                "(typeof(NEW.ReceiverId) <> 'text' OR substr(NEW.ReceiverId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.ReceiverName IS NOT OLD.ReceiverName AND " +
                "(typeof(NEW.ReceiverName) <> 'text' OR substr(NEW.ReceiverName, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.Message IS NOT OLD.Message AND " +
                "(typeof(NEW.Message) <> 'text' OR substr(NEW.Message, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.SendTime IS NOT OLD.SendTime AND " +
                "(typeof(NEW.SendTime) <> 'text' OR length(NEW.SendTime) > 64 OR julianday(NEW.SendTime) IS NULL)) " +
                "OR (NEW.IsMine IS NOT OLD.IsMine AND " +
                "(typeof(NEW.IsMine) <> 'text' OR substr(NEW.IsMine, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.IsGroup IS NOT OLD.IsGroup AND " +
                "(typeof(NEW.IsGroup) <> 'text' OR substr(NEW.IsGroup, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.FileId IS NOT OLD.FileId AND NEW.FileId IS NOT NULL AND " +
                "(typeof(NEW.FileId) <> 'text' OR substr(NEW.FileId, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.FileName IS NOT OLD.FileName AND NEW.FileName IS NOT NULL AND " +
                "(typeof(NEW.FileName) <> 'text' OR substr(NEW.FileName, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.LocalFilePath IS NOT OLD.LocalFilePath AND NEW.LocalFilePath IS NOT NULL AND " +
                "(typeof(NEW.LocalFilePath) <> 'text' OR substr(NEW.LocalFilePath, 1, 9) <> 'dpapi:v1:')) " +
                "OR (NEW.FileSize IS NOT OLD.FileSize AND NEW.FileSize IS NOT NULL AND " +
                "(typeof(NEW.FileSize) <> 'text' OR substr(NEW.FileSize, 1, 9) <> 'dpapi:v1:'))";

            ExecuteNonQuery(connection,
                $"""
                DROP TRIGGER IF EXISTS TR_ChatMessages_RequireProtectedInsertV1;
                DROP TRIGGER IF EXISTS TR_ChatMessages_RequireProtectedUpdateV1;
                CREATE TRIGGER TR_ChatMessages_RequireProtectedInsertV1
                BEFORE INSERT ON ChatMessages
                WHEN {invalidProtectedRow}
                BEGIN
                    SELECT RAISE(ABORT, 'protected history fields required');
                END;
                CREATE TRIGGER TR_ChatMessages_RequireProtectedUpdateV1
                BEFORE UPDATE ON ChatMessages
                WHEN {invalidProtectedUpdate}
                BEGIN
                    SELECT RAISE(ABORT, 'protected history fields required');
                END;
                """);
        }

        private static FileStream AcquireDatabaseInitializationLock(string databasePath)
        {
            string lockPath = Path.GetFullPath(databasePath) + ".initialize.lock";
            long deadline = Environment.TickCount64 +
                (long)DatabaseInitializationLockTimeout.TotalMilliseconds;
            IOException? lastFailure = null;
            while (true)
            {
                try
                {
                    return new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                }
                catch (IOException ex) when (Environment.TickCount64 < deadline)
                {
                    lastFailure = ex;
                    Thread.Sleep(25);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        "The chat database initialization lock could not be acquired.",
                        ex);
                }

                if (Environment.TickCount64 >= deadline)
                {
                    throw new IOException(
                        "The chat database initialization lock could not be acquired.",
                        lastFailure);
                }
            }
        }

        private static void RejectManagedDatabaseReparsePath(string databasePath)
        {
            string fullPath = Path.GetFullPath(databasePath);
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("The managed chat database directory is invalid.");
            string ownedRoot = parent;
            string applicationRoot = Path.GetFullPath(AppStoragePathService.ResolveAppDataDirectory());
            string applicationPrefix =
                Path.TrimEndingDirectorySeparator(applicationRoot) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(applicationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // The publisher directory and descendants are application-owned.
                // LOCALAPPDATA and its OS-managed ancestors may legitimately be
                // redirected, so they are intentionally outside this check.
                ownedRoot = Directory.GetParent(applicationRoot)?.FullName ?? applicationRoot;
            }
            if (!IsPathFreeOfReparsePoints(ownedRoot, parent))
            {
                throw new InvalidDataException(
                    "The managed chat database directory cannot traverse an application-owned reparse point.");
            }

            if (File.Exists(fullPath) && IsReparsePoint(fullPath))
            {
                throw new InvalidDataException(
                    "The managed chat database cannot be a reparse point.");
            }
            if (Directory.Exists(fullPath))
            {
                throw new InvalidDataException(
                    "The managed chat database path is occupied by a directory.");
            }
        }

        private static string ResolveDatabasePath() =>
            Path.Combine(AppStoragePathService.ResolveAppDataDirectory(), "chat.db");

        /// <summary>
        /// Migrates the database snapshot and the attachment paths it contains as
        /// one restart-safe workflow. Attachment files are copied and verified
        /// before their database rows are changed; legacy files are never removed.
        /// </summary>
        private static void MigrateLegacyDataIfNeeded(
            string newDbPath,
            IReadOnlyList<string>? legacyDirectories = null,
            string? newAttachmentsDirectory = null)
        {
            legacyDirectories ??= AppStoragePathService.ResolveLegacyAppDataDirectories();
            string[] stableLegacyDirectories = legacyDirectories.ToArray();

            // A failed snapshot must remain fatal here. Otherwise Initialize would
            // create an empty database and permanently hide recoverable history.
            MigrateLegacyDatabaseIfNeeded(newDbPath, stableLegacyDirectories);
            if (!File.Exists(newDbPath))
            {
                return;
            }

            string attachmentsDirectory = newAttachmentsDirectory ?? Path.Combine(
                AppStoragePathService.ResolveAppDataDirectory(),
                "Attachments");
            try
            {
                MigrateLegacyAttachmentPaths(
                    newDbPath,
                    attachmentsDirectory,
                    stableLegacyDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or CryptographicException or DecoderFallbackException or
                SqliteException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // File copies are content-addressed and safe to leave behind. The
                // database transaction rolls back, no completion marker is written,
                // and a later launch retries the same mapping.
                Debug.WriteLine($"Legacy attachment path migration deferred: {ex.Message}");
            }
        }

        private static void MigrateLegacyAttachmentPaths(
            string databasePath,
            string attachmentsDirectory,
            IReadOnlyList<string> legacyDirectories)
        {
            if (string.IsNullOrWhiteSpace(attachmentsDirectory) ||
                !Path.IsPathFullyQualified(attachmentsDirectory))
            {
                throw new ArgumentException(
                    "An absolute attachment migration destination is required.",
                    nameof(attachmentsDirectory));
            }

            string targetDirectory = Path.GetFullPath(attachmentsDirectory);
            Directory.CreateDirectory(targetDirectory);
            string[] legacyAttachmentRoots = legacyDirectories
                .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Select(path => Path.GetFullPath(Path.Combine(path, "Attachments")))
                .Where(path => !string.Equals(path, targetDirectory, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;");
            ExecuteNonQuery(connection,
                """
                CREATE TABLE IF NOT EXISTS AppMetadata
                (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                """);

            if (!TableExists(connection, "ChatMessages") ||
                !ColumnExists(connection, "ChatMessages", "LocalFilePath"))
            {
                return;
            }

            using (SqliteCommand status = connection.CreateCommand())
            {
                status.CommandText =
                    "SELECT Value FROM AppMetadata WHERE Key = @Key LIMIT 1;";
                status.Parameters.AddWithValue("@Key", LegacyAttachmentPathMarker);
                if (string.Equals(status.ExecuteScalar()?.ToString(), "1", StringComparison.Ordinal))
                {
                    return;
                }
            }

            var mappings = new List<LegacyAttachmentPathMapping>();
            var copiedPaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            long consideredBytes = 0;
            long lastId = 0;
            bool firstBatch = true;
            bool migrationIncomplete = false;
            int uniqueLegacySources = 0;

            while (true)
            {
                var rows = new List<LegacyAttachmentPathRow>(SensitiveMigrationBatchSize);
                using (SqliteCommand select = connection.CreateCommand())
                {
                    select.CommandText = firstBatch
                        ?
                        """
                        SELECT Id,
                               CASE WHEN typeof(LocalFilePath) = 'text'
                                          AND length(LocalFilePath) <= @MaximumPathLength
                                    THEN LocalFilePath ELSE NULL END,
                               typeof(LocalFilePath),
                               length(LocalFilePath)
                        FROM ChatMessages
                        WHERE LocalFilePath IS NOT NULL
                        ORDER BY Id
                        LIMIT @BatchSize;
                        """
                        :
                        """
                        SELECT Id,
                               CASE WHEN typeof(LocalFilePath) = 'text'
                                          AND length(LocalFilePath) <= @MaximumPathLength
                                    THEN LocalFilePath ELSE NULL END,
                               typeof(LocalFilePath),
                               length(LocalFilePath)
                        FROM ChatMessages
                        WHERE LocalFilePath IS NOT NULL AND Id > @LastId
                        ORDER BY Id
                        LIMIT @BatchSize;
                        """;
                    select.Parameters.AddWithValue("@MaximumPathLength", MaxStoredAttachmentPathCharacters);
                    select.Parameters.AddWithValue("@BatchSize", SensitiveMigrationBatchSize);
                    if (!firstBatch)
                    {
                        select.Parameters.AddWithValue("@LastId", lastId);
                    }

                    using SqliteDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(new LegacyAttachmentPathRow(
                            reader.GetInt64(0),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2),
                            reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
                    }
                }

                if (rows.Count == 0)
                {
                    break;
                }

                firstBatch = false;
                lastId = rows[^1].Id;
                foreach (LegacyAttachmentPathRow row in rows)
                {
                    if (row.StoredPath == null)
                    {
                        migrationIncomplete = true;
                        Debug.WriteLine(
                            $"Legacy attachment path row {row.Id} was not text or exceeded the safety limit " +
                            $"({row.StorageType}, {row.StoredLength}).");
                        continue;
                    }

                    string logicalPath;
                    try
                    {
                        logicalPath = UserDataProtection.UnprotectString(row.StoredPath);
                    }
                    catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
                    {
                        migrationIncomplete = true;
                        Debug.WriteLine($"Legacy attachment path row {row.Id} could not be decrypted: {ex.Message}");
                        continue;
                    }

                    if (!TryResolveLegacyAttachmentPath(
                            logicalPath,
                            legacyAttachmentRoots,
                            out string sourcePath,
                            out bool rejectedUnsafePath))
                    {
                        if (rejectedUnsafePath)
                        {
                            Debug.WriteLine(
                                $"Legacy attachment path row {row.Id} traversed a reparse point and was rejected.");
                            if (mappings.Count >= MaxLegacyAttachmentMappingsPerRun)
                            {
                                migrationIncomplete = true;
                            }
                            else
                            {
                                mappings.Add(new LegacyAttachmentPathMapping(
                                    row.Id,
                                    row.StoredPath,
                                    null));
                            }
                        }
                        // Outgoing attachments can legitimately point to a user-selected
                        // location outside application storage and must not be copied.
                        continue;
                    }

                    if (!copiedPaths.TryGetValue(sourcePath, out string? destinationPath))
                    {
                        if (uniqueLegacySources >= MaxLegacyAttachmentMappingsPerRun)
                        {
                            migrationIncomplete = true;
                            continue;
                        }
                        uniqueLegacySources++;

                        try
                        {
                            var sourceInfo = new FileInfo(sourcePath);
                            if (!sourceInfo.Exists || sourceInfo.Length < 0 ||
                                sourceInfo.Length > FileTransferService.MaxFileSizeBytes)
                            {
                                throw new InvalidDataException(
                                    "The referenced legacy attachment is missing or exceeds the 50 MB limit.");
                            }
                            if (consideredBytes >
                                MaxLegacyAttachmentMigrationBytesPerRun - sourceInfo.Length)
                            {
                                migrationIncomplete = true;
                                copiedPaths[sourcePath] = null;
                                continue;
                            }

                            consideredBytes += sourceInfo.Length;
                            destinationPath = FileTransferService.CopyLegacyAttachmentIdempotently(
                                sourcePath,
                                sourceInfo.Name,
                                targetDirectory);
                            copiedPaths[sourcePath] = destinationPath;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                            InvalidDataException or CryptographicException or ArgumentException or
                            NotSupportedException)
                        {
                            migrationIncomplete = true;
                            copiedPaths[sourcePath] = null;
                            Debug.WriteLine(
                                $"Legacy attachment copy deferred for {sourcePath}: {ex.Message}");
                            continue;
                        }
                    }

                    if (destinationPath == null)
                    {
                        migrationIncomplete = true;
                        continue;
                    }

                    if (mappings.Count >= MaxLegacyAttachmentMappingsPerRun)
                    {
                        migrationIncomplete = true;
                        continue;
                    }

                    mappings.Add(new LegacyAttachmentPathMapping(
                        row.Id,
                        row.StoredPath,
                        UserDataProtection.ProtectString(destinationPath)));
                }
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (LegacyAttachmentPathMapping mapping in mappings)
            {
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE ChatMessages
                    SET LocalFilePath = @NewPath
                    WHERE Id = @Id AND LocalFilePath = @OldPath;
                    """;
                update.Parameters.AddWithValue(
                    "@NewPath",
                    (object?)mapping.NewStoredPath ?? DBNull.Value);
                update.Parameters.AddWithValue("@OldPath", mapping.OldStoredPath);
                update.Parameters.AddWithValue("@Id", mapping.Id);
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        "A legacy attachment path changed concurrently; migration will be retried.");
                }
            }

            if (!migrationIncomplete)
            {
                UpsertMetadata(connection, transaction, LegacyAttachmentPathMarker, "1");
            }
            transaction.Commit();
        }

        private static bool TryResolveLegacyAttachmentPath(
            string path,
            IReadOnlyList<string> legacyAttachmentRoots,
            out string resolvedPath,
            out bool rejectedUnsafePath)
        {
            resolvedPath = "";
            rejectedUnsafePath = false;
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
                !Path.IsPathFullyQualified(path))
            {
                return false;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            foreach (string legacyRoot in legacyAttachmentRoots)
            {
                string root = Path.TrimEndingDirectorySeparator(legacyRoot) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    if (PathEntryExists(candidate) &&
                        !IsPathFreeOfReparsePoints(legacyRoot, candidate))
                    {
                        rejectedUnsafePath = true;
                        return false;
                    }
                    resolvedPath = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool IsPathFreeOfReparsePoints(string rootPath, string candidatePath)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string candidate = Path.GetFullPath(candidatePath);
            string relative = Path.GetRelativePath(root, candidate);
            if (relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relative))
            {
                return false;
            }

            string current = root;
            if (PathEntryExists(current) && IsReparsePoint(current))
            {
                return false;
            }
            foreach (string segment in relative.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (PathEntryExists(current) && IsReparsePoint(current))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static bool PathEntryExists(string path) =>
            File.Exists(path) || Directory.Exists(path);

        private static void UpsertMetadata(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string key,
            string value)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO AppMetadata(Key, Value)
                VALUES (@Key, @Value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            command.Parameters.AddWithValue("@Key", key);
            command.Parameters.AddWithValue("@Value", value);
            command.ExecuteNonQuery();
        }

        private static void WriteMetadataMarkerTransaction(
            SqliteConnection connection,
            string key)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            UpsertMetadata(connection, transaction, key, "1");
            transaction.Commit();
        }

        private static void MigrateLegacyDatabaseIfNeeded(
            string newDbPath,
            IReadOnlyList<string>? legacyDirectories = null)
        {
            if (File.Exists(newDbPath))
            {
                return;
            }

            bool legacyDatabaseFound = false;
            Exception? lastFailure = null;
            legacyDirectories ??= AppStoragePathService.ResolveLegacyAppDataDirectories();
            foreach (string legacyDirectory in legacyDirectories)
            {
                string legacyDbPath = Path.Combine(legacyDirectory, "chat.db");
                if (!File.Exists(legacyDbPath))
                {
                    continue;
                }
                legacyDatabaseFound = true;
                if (IsReparsePoint(legacyDbPath))
                {
                    lastFailure = new InvalidDataException(
                        "A legacy chat database cannot be migrated through a reparse point.");
                    continue;
                }

                string temporaryPath = Path.Combine(
                    Path.GetDirectoryName(newDbPath)!,
                    $".chat-migration-{Guid.NewGuid():N}.tmp");
                try
                {
                    CopyDatabaseSnapshot(legacyDbPath, temporaryPath);
                    VerifyDatabase(temporaryPath);
                    try
                    {
                        File.Move(temporaryPath, newDbPath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(newDbPath))
                    {
                        // Another process completed the migration first.
                    }

                    Debug.WriteLine($"SQLite DB migrated: {legacyDbPath} -> {newDbPath}");
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    Debug.WriteLine($"SQLite DB migration skipped: {legacyDbPath} ({ex.Message})");
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }

            if (legacyDatabaseFound && !File.Exists(newDbPath))
            {
                throw new IOException(
                    "A legacy chat database exists but could not be migrated safely. " +
                    "The application will retry without creating an empty replacement database.",
                    lastFailure);
            }
        }

        private static void VerifyDatabase(string path)
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            string? result = command.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Legacy database integrity check failed: {result}");
            }
        }

        private static void CopyDatabaseSnapshot(string sourcePath, string destinationPath)
        {
            string sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            string destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using var source = new SqliteConnection(sourceConnectionString);
            using var destination = new SqliteConnection(destinationConnectionString);
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stale migration file is safe and can be removed on a later launch.
            }
        }

        private sealed record SensitiveRow(
            long Id,
            string MessageType,
            string SenderId,
            string SenderName,
            string ReceiverId,
            string ReceiverName,
            string Message,
            string IsMine,
            string IsGroup,
            string? FileId,
            string? FileName,
            string? LocalFilePath,
            string? FileSize);

        private sealed record LegacyUserRow(
            long RowId,
            string DeviceId,
            string MachineName,
            string DisplayName);

        private sealed record LegacyTimestampRow(long Id, string SendTime);

        private sealed record HistoryLookupRow(
            long Id,
            string? StoredMessageId,
            string? StoredConversationId,
            string? MessageLookupKey,
            string? ConversationLookupKey);

        private sealed record HistoryLookupUpdate(
            HistoryLookupRow Original,
            string StoredMessageId,
            string StoredConversationId,
            string MessageLookupKey,
            string ConversationLookupKey);

        private sealed record LegacyAttachmentPathRow(
            long Id,
            string? StoredPath,
            string StorageType,
            long StoredLength);

        private sealed record LegacyAttachmentPathMapping(
            long Id,
            string OldStoredPath,
            string? NewStoredPath);
    }
}
