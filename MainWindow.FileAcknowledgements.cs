using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Network;
using direct_module.Services;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private const int MaximumEarlyFileAcknowledgements = 128;
        private static readonly TimeSpan RelayAcknowledgementTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan FileCleanupTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, PendingFileAcknowledgementSet> _pendingFileAcknowledgements =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, FileTransferAcknowledgement> _earlyFileAcknowledgements =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<KeyValuePair<string, FileTransferAcknowledgement>>
            _earlyFileAcknowledgementOrder = new();
        private readonly ConcurrentDictionary<string, RelayFileTransferContext> _relayFileTransfers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<ChatConnection, SemaphoreSlim> _fileProcessingGates = new();
        private readonly ConcurrentDictionary<ChatConnection, ConcurrentDictionary<string, IncomingTransferRegistration>>
            _activeIncomingTransfers = new();

        private void TrackIncomingTransfer(
            ChatConnection connection,
            string fileId,
            string transferSourceId)
        {
            _activeIncomingTransfers
                .GetOrAdd(
                    connection,
                    _ => new ConcurrentDictionary<string, IncomingTransferRegistration>(StringComparer.Ordinal))
                [$"{NormalizeFileId(fileId)}\u001f{transferSourceId}"] =
                    new IncomingTransferRegistration(NormalizeFileId(fileId), transferSourceId);
        }

        private void UntrackIncomingTransfer(
            ChatConnection connection,
            string? fileId,
            string transferSourceId)
        {
            if (!Guid.TryParse(fileId, out Guid parsedFileId) ||
                !_activeIncomingTransfers.TryGetValue(
                    connection,
                    out ConcurrentDictionary<string, IncomingTransferRegistration>? transfers))
            {
                return;
            }

            transfers.TryRemove(
                $"{parsedFileId:N}\u001f{transferSourceId}",
                out _);
            if (transfers.IsEmpty)
            {
                _activeIncomingTransfers.TryRemove(connection, out _);
            }
        }

        private void AbortIncomingTransfersForDisconnectedConnection(ChatConnection connection)
        {
            if (!_activeIncomingTransfers.TryRemove(
                    connection,
                    out ConcurrentDictionary<string, IncomingTransferRegistration>? transfers) ||
                transfers.IsEmpty)
            {
                return;
            }

            StartBackgroundOperation(async () =>
            {
                foreach (IncomingTransferRegistration transfer in transfers.Values)
                {
                    using var cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _windowLifetimeCancellation.Token);
                    cleanupCancellation.CancelAfter(FileCleanupTimeout);
                    try
                    {
                        await _fileTransferService.AbortIncomingTransferAsync(
                            transfer.FileId,
                            transfer.TransferSourceId,
                            cleanupCancellation.Token);
                    }
                    catch (OperationCanceledException) when (
                        _windowLifetimeCancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        EnqueueLog(
                            $"切断後の未完了ファイル破棄に失敗しました: {ex.Message}",
                            LogLevel.Error);
                    }
                }
            }, "切断された相手の未完了ファイル破棄");
        }

        private readonly record struct IncomingTransferRegistration(
            string FileId,
            string TransferSourceId);

        private async Task<FileTransferAcknowledgement> WaitForFileAcknowledgementsAsync(
            string fileId,
            IReadOnlyCollection<ChatConnection> expectedConnections,
            CancellationToken cancellationToken)
        {
            string normalizedFileId = NormalizeFileId(fileId);
            string key = CreateAcknowledgementSetKey(normalizedFileId, LocalPeerId);
            var pending = new PendingFileAcknowledgementSet(
                normalizedFileId,
                LocalPeerId,
                expectedConnections);
            if (!_pendingFileAcknowledgements.TryAdd(key, pending))
            {
                throw new InvalidOperationException("An acknowledgement wait is already active for this file.");
            }

            try
            {
                // ACK can arrive immediately after file_end and before the transfer
                // service invokes this waiter. Consume only entries from the exact
                // stable peers captured when sending started.
                foreach (string expectedSender in pending.ExpectedSenders)
                {
                    string earlyKey = CreateEarlyAcknowledgementKey(
                        normalizedFileId,
                        LocalPeerId,
                        expectedSender);
                    if (_earlyFileAcknowledgements.TryRemove(
                            earlyKey,
                            out FileTransferAcknowledgement? early))
                    {
                        pending.TryRecord(early);
                    }
                }

                // Covers a disconnect in the narrow window between file_end being
                // sent and this waiter being registered.
                foreach (ChatConnection expectedConnection in expectedConnections)
                {
                    if (!expectedConnection.IsConnected || !expectedConnection.IsReady)
                    {
                        pending.TryFail(
                            expectedConnection,
                            "ファイル送信先との接続が切断されました。");
                    }
                }

                return await pending.Completion.WaitAsync(cancellationToken);
            }
            finally
            {
                _pendingFileAcknowledgements.TryRemove(key, out _);
            }
        }

        private void HandleFileAcknowledgement(ChatMessage message, ChatConnection sourceConnection)
        {
            if (!FileTransferService.TryParseAcknowledgement(
                    message,
                    out FileTransferAcknowledgement acknowledgement))
            {
                EnqueueLog("不正なファイルACKを受信しました。", LogLevel.Error);
                return;
            }

            if (message.HopCount != 0)
            {
                EnqueueLog("中継済みファイルACKを拒否しました。", LogLevel.Error);
                return;
            }

            string boundRemotePeerId = GetStableRemotePeerId(sourceConnection);
            if (string.IsNullOrWhiteSpace(boundRemotePeerId) ||
                !string.Equals(
                    boundRemotePeerId,
                    acknowledgement.AcknowledgementSenderId,
                    StringComparison.OrdinalIgnoreCase))
            {
                EnqueueLog("接続相手と送信者が一致しないファイルACKを拒否しました。", LogLevel.Error);
                return;
            }

            string normalizedFileId;
            try
            {
                normalizedFileId = NormalizeFileId(acknowledgement.FileId);
            }
            catch (InvalidOperationException)
            {
                EnqueueLog("FileIdが不正なファイルACKを拒否しました。", LogLevel.Error);
                return;
            }

            if (string.Equals(
                    acknowledgement.AcknowledgementTargetId,
                    LocalPeerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                string localKey = CreateAcknowledgementSetKey(normalizedFileId, LocalPeerId);
                if (_pendingFileAcknowledgements.TryGetValue(
                        localKey,
                        out PendingFileAcknowledgementSet? pending))
                {
                    if (!pending.TryRecord(acknowledgement))
                    {
                        EnqueueLog("送信時の宛先に含まれない相手からのファイルACKを拒否しました。", LogLevel.Error);
                    }
                    return;
                }

                StoreEarlyAcknowledgement(acknowledgement);
                return;
            }

            string relayKey = CreateAcknowledgementSetKey(
                normalizedFileId,
                acknowledgement.AcknowledgementTargetId);
            if (!_relayFileTransfers.TryGetValue(
                    relayKey,
                    out RelayFileTransferContext? relayContext) ||
                !relayContext.Acknowledgements.TryRecord(acknowledgement))
            {
                EnqueueLog("対応する中継転送がないファイルACKを無視しました。", LogLevel.Debug);
            }
        }

        private RelayFileTransferContext CreateRelayFileTransferContext(
            ChatMessage startMessage,
            ChatConnection originConnection)
        {
            string fileId = NormalizeFileId(startMessage.FileId);
            string originalSenderId = startMessage.SenderId.Trim();
            List<ChatConnection> recipients = SnapshotReadyFileRecipients(originConnection)
                .Where(connection => connection.IsInbound)
                .ToList();
            var context = new RelayFileTransferContext(
                startMessage,
                fileId,
                originalSenderId,
                originConnection,
                recipients);
            string key = CreateAcknowledgementSetKey(fileId, originalSenderId);
            if (!_relayFileTransfers.TryAdd(key, context))
            {
                throw new InvalidOperationException("The same relayed file transfer is already active.");
            }

            return context;
        }

        private bool TryGetRelayFileTransferContext(
            ChatMessage message,
            out RelayFileTransferContext? context)
        {
            context = null;
            if (!Guid.TryParse(message.FileId, out Guid fileId) ||
                string.IsNullOrWhiteSpace(message.SenderId))
            {
                return false;
            }

            return _relayFileTransfers.TryGetValue(
                CreateAcknowledgementSetKey(fileId.ToString("N"), message.SenderId),
                out context);
        }

        private static void ValidateRelayContinuation(
            ChatMessage message,
            ChatConnection sourceConnection,
            RelayFileTransferContext context)
        {
            if (!ReferenceEquals(context.OriginConnection, sourceConnection) ||
                !message.IsGroup ||
                message.HopCount != 0 ||
                !string.Equals(
                    message.ConversationId,
                    context.ConversationId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    message.SenderId,
                    context.OriginalSenderId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new System.IO.InvalidDataException(
                    "A relayed file continuation changed its validated start envelope.");
            }
        }

        private bool RemoveRelayFileTransferContext(RelayFileTransferContext context)
        {
            string key = CreateAcknowledgementSetKey(context.FileId, context.OriginalSenderId);
            return ((ICollection<KeyValuePair<string, RelayFileTransferContext>>)_relayFileTransfers)
                .Remove(new KeyValuePair<string, RelayFileTransferContext>(key, context));
        }

        private async Task CleanupFailedIncomingFileMessageAsync(
            ChatMessage message,
            ChatConnection sourceConnection,
            string transferSourceId,
            RelayFileTransferContext? relayContext,
            bool relayDeliveryCompleted)
        {
            if (!string.IsNullOrWhiteSpace(transferSourceId) &&
                Guid.TryParse(message.FileId, out Guid fileId))
            {
                using var cleanupCancellation = new CancellationTokenSource(FileCleanupTimeout);
                try
                {
                    await _fileTransferService.AbortIncomingTransferAsync(
                        fileId.ToString("N"),
                        transferSourceId,
                        cleanupCancellation.Token);
                }
                catch (Exception ex)
                {
                    EnqueueLog($"未完了ファイルの破棄に失敗しました: {ex.Message}", LogLevel.Error);
                }

                UntrackIncomingTransfer(sourceConnection, message.FileId, transferSourceId);
            }

            if (relayContext != null && !relayDeliveryCompleted)
            {
                await AbortRelayFileTransferAsync(relayContext);
            }
        }

        private async Task AbortRelayFileTransferAsync(RelayFileTransferContext context)
        {
            // Only the operation that atomically removes this exact context sends the
            // abort. A late cleanup for an older transfer can never remove a newer
            // context that reused the same sender/FileId key.
            if (!RemoveRelayFileTransferContext(context) ||
                _windowLifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            using var cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _windowLifetimeCancellation.Token);
            cleanupCancellation.CancelAfter(FileCleanupTimeout);
            try
            {
                await RelayFileMessageToSnapshotAsync(
                    context.CreateAbortMessage(),
                    context,
                    cleanupCancellation.Token);
            }
            catch (Exception ex)
            {
                EnqueueLog($"中継先の未完了ファイルを破棄できませんでした: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task RelayFileMessageToSnapshotAsync(
            ChatMessage message,
            RelayFileTransferContext context,
            CancellationToken cancellationToken)
        {
            ChatMessage relay = message.CreateRelayEnvelope(
                LocalPeerId,
                Environment.MachineName,
                GetLocalShortSessionId());

            await Task.WhenAll(context.Recipients.Select(async recipient =>
            {
                string recipientId = GetStableRemotePeerId(recipient);
                if (!recipient.IsConnected || !recipient.IsReady)
                {
                    context.Acknowledgements.TryFail(
                        recipient,
                        "ファイル中継先との接続が利用できません。");
                    return;
                }

                try
                {
                    await recipient.SendAsync(relay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    recipient.Close();
                    context.Acknowledgements.TryFail(
                        recipient,
                        "中継先へのファイル配信に失敗しました。");
                    EnqueueLog(
                        $"ファイル中継に失敗しました: Peer={recipientId}, Error={ex.Message}",
                        LogLevel.Error);
                }
            }));
        }

        private async Task<FileTransferAcknowledgement> WaitForRelayAcknowledgementsAsync(
            RelayFileTransferContext context,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RelayAcknowledgementTimeout);
            try
            {
                return await context.Acknowledgements.Completion.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                return context.Acknowledgements.CreateFailure(
                    "中継先からの受信確認がタイムアウトしました。");
            }
        }

        private void HandleFileAcknowledgementConnectionDisconnected(ChatConnection connection)
        {
            string remotePeerId = GetStableRemotePeerId(connection);
            if (string.IsNullOrWhiteSpace(remotePeerId)) return;

            foreach (PendingFileAcknowledgementSet pending in _pendingFileAcknowledgements.Values)
            {
                pending.TryFail(connection, "ファイル送信先との接続が切断されました。");
            }

            foreach (RelayFileTransferContext relay in _relayFileTransfers.Values)
            {
                if (ReferenceEquals(relay.OriginConnection, connection))
                {
                    StartBackgroundOperation(
                        () => AbortRelayFileTransferAsync(relay),
                        "切断された送信元のファイル中継終了");
                    continue;
                }

                relay.Acknowledgements.TryFail(
                    connection,
                    "ファイル中継先との接続が切断されました。");
            }
        }

        private void StoreEarlyAcknowledgement(FileTransferAcknowledgement acknowledgement)
        {
            string key = CreateEarlyAcknowledgementKey(
                acknowledgement.FileId,
                acknowledgement.AcknowledgementTargetId,
                acknowledgement.AcknowledgementSenderId);
            if (_earlyFileAcknowledgements.TryAdd(key, acknowledgement))
            {
                _earlyFileAcknowledgementOrder.Enqueue(
                    new KeyValuePair<string, FileTransferAcknowledgement>(key, acknowledgement));
            }

            while (_earlyFileAcknowledgementOrder.Count > MaximumEarlyFileAcknowledgements &&
                   _earlyFileAcknowledgementOrder.TryDequeue(
                       out KeyValuePair<string, FileTransferAcknowledgement> expired))
            {
                ((ICollection<KeyValuePair<string, FileTransferAcknowledgement>>)
                    _earlyFileAcknowledgements).Remove(expired);
            }
        }

        private List<ChatConnection> SnapshotReadyFileRecipients(
            ChatConnection? exceptConnection = null) =>
            _chatConnectionManager.Connections
                .Where(connection =>
                    !ReferenceEquals(connection, exceptConnection) &&
                    connection.IsConnected &&
                    connection.IsReady &&
                    !string.IsNullOrWhiteSpace(GetStableRemotePeerId(connection)))
                .GroupBy(GetStableRemotePeerId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

        private static string GetStableRemotePeerId(ChatConnection connection) =>
            connection.BoundRemotePeerId?.Trim() ?? "";

        private static string NormalizeFileId(string? fileId) =>
            Guid.TryParse(fileId, out Guid parsed)
                ? parsed.ToString("N")
                : throw new InvalidOperationException("A valid file transfer ID is required.");

        private static string CreateAcknowledgementSetKey(string fileId, string targetId) =>
            $"{NormalizeFileId(fileId)}\u001f{targetId.Trim()}";

        private static string CreateEarlyAcknowledgementKey(
            string fileId,
            string targetId,
            string senderId) =>
            $"{CreateAcknowledgementSetKey(fileId, targetId)}\u001f{senderId.Trim()}";

        private sealed class RelayFileTransferContext
        {
            public RelayFileTransferContext(
                ChatMessage startMessage,
                string fileId,
                string originalSenderId,
                ChatConnection originConnection,
                IReadOnlyList<ChatConnection> recipients)
            {
                FileId = fileId;
                OriginalSenderId = originalSenderId;
                OriginalSenderName = startMessage.SenderName;
                OriginalShortSessionId = startMessage.ShortSessionId;
                ConversationId = PeerIdentityService.NormalizeConversationId(
                    startMessage.ConversationId,
                    isGroup: true);
                FileName = startMessage.FileName;
                FileSize = startMessage.FileSize;
                ChunkCount = startMessage.ChunkCount;
                OriginConnection = originConnection;
                Recipients = recipients;
                Acknowledgements = new PendingFileAcknowledgementSet(
                    fileId,
                    originalSenderId,
                    recipients);
            }

            public string FileId { get; }
            public string OriginalSenderId { get; }
            public string OriginalSenderName { get; }
            public string OriginalShortSessionId { get; }
            public string ConversationId { get; }
            public string? FileName { get; }
            public long? FileSize { get; }
            public int? ChunkCount { get; }
            public ChatConnection OriginConnection { get; }
            public IReadOnlyList<ChatConnection> Recipients { get; }
            public PendingFileAcknowledgementSet Acknowledgements { get; }

            public ChatMessage CreateAbortMessage() => new()
            {
                Type = "file_abort",
                SenderId = OriginalSenderId,
                SenderName = OriginalSenderName,
                ShortSessionId = OriginalShortSessionId,
                IsGroup = true,
                ConversationId = ConversationId,
                FileId = FileId,
                FileName = FileName,
                FileSize = FileSize,
                ChunkCount = ChunkCount,
                Body = ""
            };
        }

        private sealed class PendingFileAcknowledgementSet
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, ChatConnection> _expectedConnections =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _receivedSenders = new(StringComparer.OrdinalIgnoreCase);
            private readonly TaskCompletionSource<FileTransferAcknowledgement> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingFileAcknowledgementSet(
                string fileId,
                string targetId,
                IEnumerable<ChatConnection> expectedConnections)
            {
                FileId = NormalizeFileId(fileId);
                TargetId = targetId.Trim();
                foreach (ChatConnection connection in expectedConnections)
                {
                    string sender = GetStableRemotePeerId(connection);
                    if (!string.IsNullOrWhiteSpace(sender))
                    {
                        _expectedConnections.TryAdd(sender, connection);
                    }
                }

                if (_expectedConnections.Count == 0)
                {
                    _completion.TrySetResult(CreateSuccess());
                }
            }

            public string FileId { get; }
            public string TargetId { get; }
            public IReadOnlyCollection<string> ExpectedSenders => _expectedConnections.Keys;
            public Task<FileTransferAcknowledgement> Completion => _completion.Task;

            public bool TryRecord(FileTransferAcknowledgement acknowledgement)
            {
                lock (_gate)
                {
                    if (!_expectedConnections.ContainsKey(acknowledgement.AcknowledgementSenderId))
                    {
                        return false;
                    }

                    if (!_receivedSenders.Add(acknowledgement.AcknowledgementSenderId))
                    {
                        return true;
                    }

                    if (!acknowledgement.IsSuccess)
                    {
                        _completion.TrySetResult(CreateFailure(
                            string.IsNullOrWhiteSpace(acknowledgement.ErrorMessage)
                                ? "ファイル受信先が転送を拒否しました。"
                                : acknowledgement.ErrorMessage));
                    }
                    else if (_receivedSenders.Count == _expectedConnections.Count)
                    {
                        _completion.TrySetResult(CreateSuccess());
                    }

                    return true;
                }
            }

            public bool TryFail(ChatConnection connection, string errorMessage)
            {
                lock (_gate)
                {
                    bool isExpectedConnection = _expectedConnections.Values.Any(
                        expected => ReferenceEquals(expected, connection));
                    if (!isExpectedConnection)
                    {
                        return false;
                    }

                    _completion.TrySetResult(CreateFailure(errorMessage));
                    return true;
                }
            }

            public FileTransferAcknowledgement CreateFailure(string errorMessage) => new()
            {
                FileId = FileId,
                AcknowledgementTargetId = TargetId,
                IsSuccess = false,
                ErrorMessage = errorMessage
            };

            private FileTransferAcknowledgement CreateSuccess() => new()
            {
                FileId = FileId,
                AcknowledgementTargetId = TargetId,
                IsSuccess = true
            };
        }
    }
}
