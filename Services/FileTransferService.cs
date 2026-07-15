using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Network;

namespace direct_module.Services
{
    public sealed class FileTransferProgress
    {
        public string FileId { get; init; } = "";
        public string FileName { get; init; } = "";
        public double Percent { get; init; }
        public bool IsComplete { get; init; }
        public string? LocalFilePath { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed class FileTransferDisplayResult
    {
        public string Message { get; init; } = "";
        public string FileId { get; init; } = "";
        public string FileName { get; init; } = "";
        public long FileSize { get; init; }
        public string LocalFilePath { get; init; } = "";
    }

    public sealed class FileTransferAcknowledgement
    {
        public string FileId { get; init; } = "";
        public string AcknowledgementTargetId { get; init; } = "";
        public string AcknowledgementSenderId { get; init; } = "";
        public bool IsSuccess { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    public sealed class FileTransferSendResult
    {
        public string FileId { get; init; } = "";
        public string FileName { get; init; } = "";
        public long FileSize { get; init; }
        public bool IsAcknowledged { get; init; }
    }

    public sealed class FileTransferService
    {
        public const long MaxFileSizeBytes = 50L * 1024 * 1024;
        public const int TransferChunkSize = 128 * 1024;
        public const int MaxIncomingTransfers = 32;
        public const int MaxIncomingTransfersPerSource = 4;
        public const long MaxReservedIncomingBytes = 200L * 1024 * 1024;
        public const long MaxReservedIncomingBytesPerSource = 100L * 1024 * 1024;
        public const int MaxIncomingStartsPerSourcePerMinute = 30;
        public const int MaxIncomingStartsGloballyPerMinute = 120;
        public const int MaxRetainedAttachments = 5_000;
        public const long MaxRetainedAttachmentBytes = 2L * 1024 * 1024 * 1024;
        public const int MaxOutgoingCacheFiles = 256;
        public const long MaxOutgoingCacheBytes = 1L * 1024 * 1024 * 1024;

        private const int MaxFileNameLength = 180;
        private const int MaxSequentialNameAttempts = 32;
        private const int MaxUniqueNameAttempts = 64;
        private const int MaxMaintenanceFilesPerPass = 10_000;
        private const int MaxSourceIdentityLength = 512;
        private const int MaxEncodedChunkLength = ((TransferChunkSize + 2) / 3) * 4;
        private const string PartialFilePrefix = ".incoming-";
        private const long MinimumFreeDiskReserve = 32L * 1024 * 1024;
        private static readonly TimeSpan IncomingTransferTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AttachmentRetention = TimeSpan.FromDays(30);
        private static readonly TimeSpan PartialFileRetention = TimeSpan.FromDays(1);
        private static readonly TimeSpan OutgoingCacheRetention = TimeSpan.FromDays(1);
        private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FailedSendTerminationTimeout = TimeSpan.FromSeconds(3);
        private static readonly Guid DownloadsKnownFolderId =
            new("374DE290-123F-4565-9164-39C4925E467B");
        private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly Dictionary<string, IncomingFileSession> _incomingFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<DateTime>> _incomingStartTimesBySource = new(StringComparer.Ordinal);
        private readonly Queue<DateTime> _globalIncomingStartTimes = new();
        private readonly string _attachmentsDirectory;
        private readonly string _downloadsDirectory;
        private readonly string _outgoingCacheDirectory;
        private readonly object _storageInitializationGate = new();
        private readonly object _outgoingCacheGate = new();
        private int _retainedAttachmentCount = MaxRetainedAttachments;
        private long _retainedAttachmentBytes = MaxRetainedAttachmentBytes;
        private int _storageReady;

        public FileTransferService()
            : this(
                ResolveAttachmentsDirectory(),
                ResolveDownloadsDirectory(),
                AppStoragePathService.ResolveOutgoingCacheDirectory())
        {
        }

        public FileTransferService(
            string attachmentsDirectory,
            string downloadsDirectory,
            string outgoingCacheDirectory)
        {
            _attachmentsDirectory = ValidateStorageDirectory(attachmentsDirectory, nameof(attachmentsDirectory));
            _downloadsDirectory = ValidateStorageDirectory(downloadsDirectory, nameof(downloadsDirectory));
            _outgoingCacheDirectory = ValidateStorageDirectory(outgoingCacheDirectory, nameof(outgoingCacheDirectory));
            if (string.Equals(_attachmentsDirectory, _downloadsDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_attachmentsDirectory, _outgoingCacheDirectory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_downloadsDirectory, _outgoingCacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Attachments, Downloads, and outgoing cache directories must be distinct.");
            }
        }

        public string AttachmentsDirectory => _attachmentsDirectory;

        public string DownloadsDirectory => _downloadsDirectory;

        public event Action<string>? LogReceived;
        public event Action<FileTransferProgress>? ProgressChanged;

        private void ReportLog(string message)
        {
            Delegate[] handlers = LogReceived?.GetInvocationList() ?? Array.Empty<Delegate>();
            foreach (Action<string> handler in handlers.Cast<Action<string>>())
            {
                try
                {
                    handler(message);
                }
                catch
                {
                    // Diagnostic subscribers must not alter transfer state.
                }
            }
        }

        private void ReportProgress(FileTransferProgress progress)
        {
            Delegate[] handlers = ProgressChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
            foreach (Action<FileTransferProgress> handler in handlers.Cast<Action<FileTransferProgress>>())
            {
                try
                {
                    handler(progress);
                }
                catch
                {
                    // UI subscribers must not abort or corrupt an otherwise valid transfer.
                }
            }
        }

        public void EnsureStorageReady()
        {
            if (Volatile.Read(ref _storageReady) != 0)
            {
                return;
            }
            lock (_storageInitializationGate)
            {
                if (Volatile.Read(ref _storageReady) != 0)
                {
                    return;
                }
                EnsureAttachmentsDirectory();
                CleanupExpiredIncomingSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
                CleanupStoredFiles();
                CleanupOutgoingCache();
                Volatile.Write(ref _storageReady, 1);
                ReportLog($"Attachments directory: {_attachmentsDirectory}");
                ReportLog($"Downloads directory: {_downloadsDirectory}");
            }
        }

        public async Task<string> SaveToDownloadsAsync(
            string localFilePath,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            {
                throw new FileNotFoundException("Attachment file was not found.", localFilePath);
            }

            Directory.CreateDirectory(_downloadsDirectory);
            string destinationPath = GetUniqueFilePath(_downloadsDirectory, SafeFileName(fileName));
            ValidateLocalSourceFile(localFilePath);
            bool destinationCreated = false;
            try
            {
                await using var source = new FileStream(
                    localFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    TransferChunkSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    TransferChunkSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                destinationCreated = true;
                byte[] buffer = new byte[TransferChunkSize];
                try
                {
                    long total = 0;
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        total = checked(total + read);
                        if (total > MaxFileSizeBytes)
                        {
                            throw new InvalidDataException("The attachment grew beyond the 50 MB limit while it was being saved.");
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
                await destination.FlushAsync(cancellationToken);
                ReportLog($"Attachment saved to Downloads: {destinationPath}");
                return destinationPath;
            }
            catch
            {
                if (destinationCreated)
                {
                    TryDeleteFile(destinationPath);
                }
                throw;
            }
        }

        public async Task<FileTransferSendResult> SendFileConfirmedAsync(
            string filePath,
            string senderId,
            string senderName,
            string shortSessionId,
            bool isGroup,
            string conversationId,
            Func<ChatMessage, CancellationToken, Task> sendAsync,
            Func<string, CancellationToken, Task<FileTransferAcknowledgement>> waitForAcknowledgementAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(waitForAcknowledgementAsync);
            FileTransferSendResult sent = await SendFileCoreAsync(
                filePath,
                senderId,
                senderName,
                shortSessionId,
                isGroup,
                conversationId,
                sendAsync,
                cancellationToken);

            using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acknowledgementTimeout.CancelAfter(AcknowledgementTimeout);

            FileTransferAcknowledgement acknowledgement;
            try
            {
                acknowledgement = await waitForAcknowledgementAsync(sent.FileId, acknowledgementTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The receiver did not acknowledge file {sent.FileId} within {AcknowledgementTimeout.TotalSeconds:F0} seconds.");
            }

            if (!TryNormalizeFileId(acknowledgement.FileId, out string acknowledgedFileId) ||
                !string.Equals(acknowledgedFileId, sent.FileId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The receiver acknowledged a different file transfer.");
            }

            if (!acknowledgement.IsSuccess)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(acknowledgement.ErrorMessage)
                        ? "The receiver rejected the file."
                        : acknowledgement.ErrorMessage);
            }

            ReportProgress(new FileTransferProgress
            {
                FileId = sent.FileId,
                FileName = sent.FileName,
                Percent = 100,
                IsComplete = true,
                LocalFilePath = filePath
            });

            return new FileTransferSendResult
            {
                FileId = sent.FileId,
                FileName = sent.FileName,
                FileSize = sent.FileSize,
                IsAcknowledged = true
            };
        }

        private async Task<FileTransferSendResult> SendFileCoreAsync(
            string filePath,
            string senderId,
            string senderName,
            string shortSessionId,
            bool isGroup,
            string conversationId,
            Func<ChatMessage, CancellationToken, Task> sendAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sendAsync);
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File was not found.", filePath);
            }

            ValidateLocalSourceFile(filePath);
            CleanupOutgoingCache(filePath);

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 0 || fileInfo.Length > MaxFileSizeBytes)
            {
                throw new InvalidOperationException("File size exceeds the 50 MB limit.");
            }

            string fileId = Guid.NewGuid().ToString("N");
            string fileName = SafeFileName(fileInfo.Name);
            long fileSize = fileInfo.Length;
            int chunkCount = GetExpectedChunkCount(fileSize);
            bool startDispatchAttempted = false;
            bool endDispatchCompleted = false;
            try
            {
                ReportLog($"File send started: {fileName} ({fileSize / 1024.0:F1} KB)");
                startDispatchAttempted = true;
                await sendAsync(
                    CreateControlMessage("file_start"),
                    cancellationToken).ConfigureAwait(false);

                await using (var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    TransferChunkSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[TransferChunkSize];
                    try
                    {
                        for (int index = 0; index < chunkCount; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int expectedLength = GetExpectedChunkLength(fileSize, chunkCount, index);
                            int bytesRead = await ReadExactlyUpToAsync(
                                stream,
                                buffer,
                                expectedLength,
                                cancellationToken).ConfigureAwait(false);
                            if (bytesRead != expectedLength)
                            {
                                throw new EndOfStreamException("The file changed or ended while it was being sent.");
                            }

                            string chunkBase64 = expectedLength == 0
                                ? ""
                                : Convert.ToBase64String(buffer, 0, expectedLength);

                            await sendAsync(
                                new ChatMessage
                                {
                                    Type = "file_chunk",
                                    SenderId = senderId,
                                    SenderName = senderName,
                                    ShortSessionId = shortSessionId,
                                    IsGroup = isGroup,
                                    ConversationId = conversationId,
                                    FileId = fileId,
                                    FileName = fileName,
                                    FileSize = fileSize,
                                    ChunkIndex = index,
                                    ChunkCount = chunkCount,
                                    ChunkBase64 = chunkBase64,
                                    Body = ""
                                },
                                cancellationToken).ConfigureAwait(false);

                            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, expectedLength));
                            ReportProgress(new FileTransferProgress
                            {
                                FileId = fileId,
                                FileName = fileName,
                                Percent = (index + 1) * 100.0 / chunkCount
                            });
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(buffer);
                    }

                    if (stream.Length != fileSize)
                    {
                        throw new IOException("The file size changed while it was being sent.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                await sendAsync(
                    CreateControlMessage("file_end"),
                    cancellationToken).ConfigureAwait(false);
                endDispatchCompleted = true;

                ReportLog($"File data sent: {fileName}");
                return new FileTransferSendResult
                {
                    FileId = fileId,
                    FileName = fileName,
                    FileSize = fileSize,
                    IsAcknowledged = false
                };
            }
            catch (Exception)
            {
                if (startDispatchAttempted && !endDispatchCompleted)
                {
                    await TryTerminateFailedSendAsync(
                        sendAsync,
                        CreateControlMessage("file_abort")).ConfigureAwait(false);
                }
                throw;
            }

            ChatMessage CreateControlMessage(string type) => new()
            {
                Type = type,
                SenderId = senderId,
                SenderName = senderName,
                ShortSessionId = shortSessionId,
                IsGroup = isGroup,
                ConversationId = conversationId,
                FileId = fileId,
                FileName = fileName,
                FileSize = fileSize,
                ChunkCount = chunkCount,
                Body = ""
            };
        }

        private async Task TryTerminateFailedSendAsync(
            Func<ChatMessage, CancellationToken, Task> sendAsync,
            ChatMessage terminationMessage)
        {
            // This cleanup token is intentionally independent of the caller. A
            // canceled send or window shutdown still gets one bounded chance to
            // release already-created remote partial sessions and reservations.
            try
            {
                using var cleanup = new CancellationTokenSource(FailedSendTerminationTimeout);
                await sendAsync(terminationMessage, cleanup.Token)
                    .WaitAsync(cleanup.Token)
                    .ConfigureAwait(false);
                ReportLog($"Failed file send abort dispatched: {terminationMessage.FileId}");
            }
            catch (Exception ex)
            {
                // Cleanup must never replace the original send/cancellation error.
                ReportLog(
                    $"Failed file send abort could not be dispatched to all recipients: {ex.GetType().Name}");
            }
        }

        public async Task<FileTransferDisplayResult?> HandleFileStartAsync(
            ChatMessage message,
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _storageReady) == 0)
            {
                throw new InvalidOperationException(
                    "Attachment storage has not completed initialization.");
            }
            await CleanupExpiredIncomingSessionsAsync(cancellationToken).ConfigureAwait(false);

            ValidatedTransfer transfer = ValidateTransferStart(message, transferSourceId);
            EnsureAttachmentsDirectory();
            string sessionKey = CreateSessionKey(transfer.SourceHash, transfer.FileId);
            string partFileName = $"{PartialFilePrefix}{transfer.SourceHash[..16]}-{transfer.FileId}-{Guid.NewGuid():N}.part";
            string partFilePath = GetContainedPath(_attachmentsDirectory, partFileName);
            bool sessionAdded = false;
            bool partFileCreated = false;

            try
            {
                lock (_incomingFiles)
                {
                    if (_incomingFiles.ContainsKey(sessionKey))
                    {
                        throw new InvalidDataException("A transfer with the same source and FileId is already active.");
                    }

                    EnsureIncomingCapacity(transfer);
                    RecordIncomingStart(transfer.SourceHash, DateTime.UtcNow);

                    using (new FileStream(
                        partFilePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 1,
                        FileOptions.None))
                    {
                    }
                    partFileCreated = true;

                    _incomingFiles.Add(sessionKey, new IncomingFileSession
                    {
                        SessionKey = sessionKey,
                        SourceHash = transfer.SourceHash,
                        FileId = transfer.FileId,
                        FileName = transfer.FileName,
                        WireFileName = transfer.WireFileName,
                        FileSize = transfer.FileSize,
                        ExpectedChunkCount = transfer.ChunkCount,
                        SenderId = transfer.SenderId,
                        SenderName = transfer.SenderName,
                        ShortSessionId = transfer.ShortSessionId,
                        IsGroup = transfer.IsGroup,
                        ConversationId = transfer.ConversationId,
                        RelaySenderId = transfer.RelaySenderId,
                        RelaySenderName = transfer.RelaySenderName,
                        RelayShortSessionId = transfer.RelayShortSessionId,
                        HopCount = transfer.HopCount,
                        PartFilePath = partFilePath,
                        LastActivityUtc = DateTime.UtcNow
                    });
                    sessionAdded = true;
                }
            }
            catch
            {
                if (!sessionAdded && partFileCreated)
                {
                    TryDeleteFile(partFilePath);
                }
                throw;
            }

            ReportLog($"File receive started: {transfer.FileName}");
            return new FileTransferDisplayResult
            {
                Message = $"受信開始: {transfer.FileName}",
                FileId = transfer.FileId,
                FileName = transfer.FileName,
                FileSize = transfer.FileSize
            };
        }

        public async Task<string?> HandleFileChunkAsync(
            ChatMessage message,
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();

            IncomingFileSession session = GetRequiredSession(message.FileId, transferSourceId);
            try
            {
                ValidateTransferEnvelope(message, "file_chunk", session);
                int chunkIndex = message.ChunkIndex ?? -1;
            if (chunkIndex < 0 || chunkIndex >= session.ExpectedChunkCount)
            {
                throw new InvalidDataException("The file chunk index is outside the declared transfer range.");
            }

            if (message.ChunkCount.HasValue && message.ChunkCount.Value != session.ExpectedChunkCount)
            {
                throw new InvalidDataException("The file chunk count changed during the transfer.");
            }

            if (message.FileSize.HasValue && message.FileSize.Value != session.FileSize)
            {
                throw new InvalidDataException("The file size changed during the transfer.");
            }

            int expectedLength = GetExpectedChunkLength(session.FileSize, session.ExpectedChunkCount, chunkIndex);
            int expectedEncodedLength = ((expectedLength + 2) / 3) * 4;
            if (message.ChunkBase64 == null ||
                message.ChunkBase64.Length > MaxEncodedChunkLength ||
                message.ChunkBase64.Length != expectedEncodedLength)
            {
                throw new InvalidDataException("The encoded file chunk has an invalid size.");
            }

            byte[] chunk;
            try
            {
                chunk = message.ChunkBase64.Length == 0
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(message.ChunkBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The file chunk is not valid Base64.", ex);
            }

            try
            {
                if (chunk.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        $"The decoded file chunk has an invalid size ({chunk.Length}/{expectedLength}).");
                }

                await session.Gate.WaitAsync(cancellationToken);
                try
                {
                    if (session.IsClosed)
                    {
                        throw new InvalidDataException("The file transfer is already closed.");
                    }

                    if (session.ReceivedChunkIndexes.Contains(chunkIndex))
                    {
                        session.LastActivityUtc = DateTime.UtcNow;
                        return null;
                    }

                    long offset = checked((long)chunkIndex * TransferChunkSize);
                    long endOffset = checked(offset + chunk.Length);
                    if (endOffset > session.FileSize)
                    {
                        throw new InvalidDataException("The file chunk would write beyond the declared file size.");
                    }

                    await using var stream = new FileStream(
                        session.PartFilePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Read,
                        TransferChunkSize,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    stream.Seek(offset, SeekOrigin.Begin);
                    await stream.WriteAsync(chunk.AsMemory(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);

                    session.ReceivedChunkIndexes.Add(chunkIndex);
                    session.ReceivedBytes = checked(session.ReceivedBytes + chunk.Length);
                    session.LastActivityUtc = DateTime.UtcNow;

                    double percent = session.FileSize > 0
                        ? Math.Min(100, session.ReceivedBytes * 100.0 / session.FileSize)
                        : 100;
                    ReportProgress(new FileTransferProgress
                    {
                        FileId = session.FileId,
                        FileName = session.FileName,
                        Percent = percent
                    });
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
            }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await AbortSessionAfterFailureAsync(session).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<FileTransferDisplayResult?> HandleFileEndAsync(
            ChatMessage message,
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            IncomingFileSession session = GetRequiredSession(message.FileId, transferSourceId);
            bool moved = false;

            await session.Gate.WaitAsync(cancellationToken);
            try
            {
                ValidateTransferEnvelope(message, "file_end", session);
                if (session.IsClosed)
                {
                    throw new InvalidDataException("The file transfer is already closed.");
                }
                session.IsClosed = true;

                if (session.ReceivedChunkIndexes.Count != session.ExpectedChunkCount)
                {
                    throw new InvalidDataException(
                        $"File receive incomplete: {session.FileName} chunks={session.ReceivedChunkIndexes.Count}/{session.ExpectedChunkCount}");
                }

                if (session.ReceivedBytes != session.FileSize)
                {
                    throw new InvalidDataException(
                        $"File receive size mismatch: {session.FileName} bytes={session.ReceivedBytes}/{session.FileSize}");
                }

                if (!File.Exists(session.PartFilePath) || new FileInfo(session.PartFilePath).Length != session.FileSize)
                {
                    throw new InvalidDataException("The partial file length does not match the declared file size.");
                }

                string finalPath;
                lock (_incomingFiles)
                {
                    // Keep the filesystem claim and retained count atomic with
                    // maintenance recounts and new capacity reservations.
                    finalPath = MoveToUniqueFilePath(
                        session.PartFilePath,
                        _attachmentsDirectory,
                        session.FileName);
                    moved = true;
                    _retainedAttachmentCount = checked(_retainedAttachmentCount + 1);
                    _retainedAttachmentBytes = checked(_retainedAttachmentBytes + session.FileSize);
                }

                ReportProgress(new FileTransferProgress
                {
                    FileId = session.FileId,
                    FileName = session.FileName,
                    Percent = 100,
                    IsComplete = true,
                    LocalFilePath = finalPath
                });

                ReportLog($"File receive completed: {session.FileName} -> {finalPath}");
                return new FileTransferDisplayResult
                {
                    Message = $"受信完了: {session.FileName}",
                    FileId = session.FileId,
                    FileName = session.FileName,
                    FileSize = session.FileSize,
                    LocalFilePath = finalPath
                };
            }
            finally
            {
                RemoveSession(session);
                session.Gate.Release();
                if (!moved)
                {
                    TryDeleteFile(session.PartFilePath);
                }
            }
        }

        /// <summary>
        /// Idempotently aborts the exact active transfer identified by the authenticated
        /// transport source and FileId. A repeated abort, or an abort for a transfer that
        /// already completed, is a successful no-op and never removes a completed file.
        /// </summary>
        public async Task<bool> HandleFileAbortAsync(
            ChatMessage message,
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!string.Equals(message.Type, "file_abort", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A transfer abort must use the file_abort message type.");
            }
            if (!TryNormalizeFileId(message.FileId, out string normalizedFileId))
            {
                throw new InvalidDataException("FileId must be a GUID.");
            }

            string sourceHash = GetSourceHash(transferSourceId);
            string key = CreateSessionKey(sourceHash, normalizedFileId);
            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(key, out session);
            }
            if (session == null)
            {
                return false;
            }

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.IsClosed)
                {
                    return false;
                }

                // An attacker cannot use a guessed FileId to terminate a transfer in
                // another conversation or relay context. Every start-bound field must
                // still match when the session exists.
                ValidateTransferEnvelope(message, "file_abort", session);
                lock (_incomingFiles)
                {
                    if (!_incomingFiles.TryGetValue(key, out IncomingFileSession? current) ||
                        !ReferenceEquals(current, session))
                    {
                        return false;
                    }
                    _incomingFiles.Remove(key);
                }

                session.IsClosed = true;
                TryDeleteFile(session.PartFilePath);
                ReportLog($"File receive aborted by sender: {session.FileName}");
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        public async Task<bool> AbortIncomingTransferAsync(
            string fileId,
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeFileId(fileId, out string normalizedFileId))
            {
                return false;
            }

            string sourceHash = GetSourceHash(transferSourceId);
            string key = CreateSessionKey(sourceHash, normalizedFileId);
            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                if (!_incomingFiles.Remove(key, out session))
                {
                    return false;
                }
            }

            // Ownership has been removed from the session map. From this point on,
            // cleanup must finish even if the caller disconnects/cancels, otherwise
            // an unreachable partial file is leaked until retention maintenance.
            await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                session.IsClosed = true;
                TryDeleteFile(session.PartFilePath);
                ReportLog($"File receive aborted: {session.FileName}");
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        public async Task<int> AbortIncomingTransfersForSourceAsync(
            string transferSourceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourceHash = GetSourceHash(transferSourceId);
            List<IncomingFileSession> sessions;
            lock (_incomingFiles)
            {
                sessions = _incomingFiles.Values
                    .Where(session => string.Equals(session.SourceHash, sourceHash, StringComparison.Ordinal))
                    .ToList();
                foreach (IncomingFileSession session in sessions)
                {
                    _incomingFiles.Remove(session.SessionKey);
                }
            }

            foreach (IncomingFileSession session in sessions)
            {
                await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    session.IsClosed = true;
                    TryDeleteFile(session.PartFilePath);
                }
                finally
                {
                    session.Gate.Release();
                }
            }

            return sessions.Count;
        }

        public async Task<int> AbortAllIncomingTransfersAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<IncomingFileSession> sessions;
            lock (_incomingFiles)
            {
                sessions = _incomingFiles.Values.ToList();
                _incomingFiles.Clear();
            }

            foreach (IncomingFileSession session in sessions)
            {
                await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    session.IsClosed = true;
                    TryDeleteFile(session.PartFilePath);
                }
                finally
                {
                    session.Gate.Release();
                }
            }

            return sessions.Count;
        }

        public static ChatMessage CreateAcknowledgement(
            ChatMessage transferMessage,
            string senderId,
            string senderName,
            string shortSessionId,
            bool success,
            string? errorMessage = null)
        {
            ArgumentNullException.ThrowIfNull(transferMessage);
            if (!TryNormalizeFileId(transferMessage.FileId, out string fileId))
            {
                throw new InvalidDataException("A file acknowledgement requires a valid FileId.");
            }

            if (!TryNormalizeAcknowledgementIdentity(transferMessage.SenderId, out string targetId) ||
                !TryNormalizeAcknowledgementIdentity(senderId, out string acknowledgementSenderId))
            {
                throw new InvalidDataException("A file acknowledgement requires valid target and sender identities.");
            }

            string safeError = SanitizeAcknowledgementError(errorMessage);
            return new ChatMessage
            {
                Type = "file_ack",
                SenderId = acknowledgementSenderId,
                SenderName = senderName,
                ShortSessionId = shortSessionId,
                IsGroup = transferMessage.IsGroup,
                ConversationId = transferMessage.ConversationId,
                FileId = fileId,
                AcknowledgementTargetId = targetId,
                AcknowledgementSenderId = acknowledgementSenderId,
                Body = success ? "ok" : $"error:{safeError}"
            };
        }

        public static bool TryParseAcknowledgement(
            ChatMessage message,
            out FileTransferAcknowledgement acknowledgement)
        {
            acknowledgement = new FileTransferAcknowledgement();
            if (message == null ||
                !string.Equals(message.Type, "file_ack", StringComparison.OrdinalIgnoreCase) ||
                !TryNormalizeFileId(message.FileId, out string fileId) ||
                !TryNormalizeAcknowledgementIdentity(message.AcknowledgementTargetId, out string targetId) ||
                !TryNormalizeAcknowledgementIdentity(message.AcknowledgementSenderId, out string acknowledgementSenderId) ||
                !TryNormalizeAcknowledgementIdentity(message.SenderId, out string wireSenderId) ||
                !string.Equals(acknowledgementSenderId, wireSenderId, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(message.Body, "ok", StringComparison.OrdinalIgnoreCase))
            {
                acknowledgement = new FileTransferAcknowledgement
                {
                    FileId = fileId,
                    AcknowledgementTargetId = targetId,
                    AcknowledgementSenderId = acknowledgementSenderId,
                    IsSuccess = true
                };
                return true;
            }

            const string errorPrefix = "error:";
            if (message.Body?.StartsWith(errorPrefix, StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            acknowledgement = new FileTransferAcknowledgement
            {
                FileId = fileId,
                AcknowledgementTargetId = targetId,
                AcknowledgementSenderId = acknowledgementSenderId,
                IsSuccess = false,
                ErrorMessage = SanitizeAcknowledgementError(message.Body[errorPrefix.Length..])
            };
            return true;
        }

        private ValidatedTransfer ValidateTransferStart(ChatMessage message, string transferSourceId)
        {
            if (!string.Equals(message.Type, "file_start", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A file transfer must begin with file_start.");
            }

            if (!TryNormalizeFileId(message.FileId, out string fileId))
            {
                throw new InvalidDataException("FileId must be a GUID.");
            }

            long fileSize = message.FileSize ?? -1;
            if (fileSize < 0 || fileSize > MaxFileSizeBytes)
            {
                throw new InvalidDataException("The declared file size is outside the 0-50 MB range.");
            }

            int expectedChunkCount = GetExpectedChunkCount(fileSize);
            if (message.ChunkCount != expectedChunkCount)
            {
                throw new InvalidDataException(
                    $"The declared chunk count is invalid ({message.ChunkCount}/{expectedChunkCount}).");
            }

            if (!TryNormalizeAcknowledgementIdentity(message.SenderId, out string senderId))
            {
                throw new InvalidDataException("The file-transfer sender identity is invalid.");
            }
            string wireFileName = message.FileName ?? "";
            if (string.IsNullOrWhiteSpace(wireFileName) || wireFileName.Length > 512)
            {
                throw new InvalidDataException("A bounded file name is required for every transfer.");
            }
            if ((message.ConversationId?.Length ?? 0) > 512 ||
                message.HopCount is < 0 or > 1)
            {
                throw new InvalidDataException("The file-transfer envelope is invalid.");
            }

            return new ValidatedTransfer
            {
                SourceHash = GetSourceHash(transferSourceId),
                FileId = fileId,
                FileName = SafeFileName(wireFileName),
                WireFileName = wireFileName,
                FileSize = fileSize,
                ChunkCount = expectedChunkCount,
                SenderId = senderId,
                SenderName = message.SenderName ?? "",
                ShortSessionId = message.ShortSessionId ?? "",
                IsGroup = message.IsGroup,
                ConversationId = message.ConversationId ?? "",
                RelaySenderId = message.RelaySenderId ?? "",
                RelaySenderName = message.RelaySenderName ?? "",
                RelayShortSessionId = message.RelayShortSessionId ?? "",
                HopCount = message.HopCount
            };
        }

        private static void ValidateTransferEnvelope(
            ChatMessage message,
            string expectedType,
            IncomingFileSession session)
        {
            if (!string.Equals(message.Type, expectedType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(message.SenderId, session.SenderId, StringComparison.Ordinal) ||
                !string.Equals(message.SenderName ?? "", session.SenderName, StringComparison.Ordinal) ||
                !string.Equals(message.ShortSessionId ?? "", session.ShortSessionId, StringComparison.Ordinal) ||
                message.IsGroup != session.IsGroup ||
                !string.Equals(message.ConversationId ?? "", session.ConversationId, StringComparison.Ordinal) ||
                !string.Equals(message.FileName, session.WireFileName, StringComparison.Ordinal) ||
                message.FileSize != session.FileSize ||
                message.ChunkCount != session.ExpectedChunkCount ||
                !string.Equals(message.RelaySenderId ?? "", session.RelaySenderId, StringComparison.Ordinal) ||
                !string.Equals(message.RelaySenderName ?? "", session.RelaySenderName, StringComparison.Ordinal) ||
                !string.Equals(message.RelayShortSessionId ?? "", session.RelayShortSessionId, StringComparison.Ordinal) ||
                message.HopCount != session.HopCount)
            {
                throw new InvalidDataException(
                    "The file-transfer envelope changed after file_start or omitted required metadata.");
            }
        }

        private IncomingFileSession GetRequiredSession(string? fileId, string transferSourceId)
        {
            if (!TryNormalizeFileId(fileId, out string normalizedFileId))
            {
                throw new InvalidDataException("FileId must be a GUID.");
            }

            string key = CreateSessionKey(GetSourceHash(transferSourceId), normalizedFileId);
            lock (_incomingFiles)
            {
                if (!_incomingFiles.TryGetValue(key, out IncomingFileSession? session))
                {
                    throw new InvalidDataException("No matching file transfer exists for this source and FileId.");
                }

                return session;
            }
        }

        private void RemoveSession(IncomingFileSession session)
        {
            lock (_incomingFiles)
            {
                if (_incomingFiles.TryGetValue(session.SessionKey, out IncomingFileSession? current) &&
                    ReferenceEquals(current, session))
                {
                    _incomingFiles.Remove(session.SessionKey);
                }
            }
        }

        private async Task CleanupExpiredIncomingSessionsAsync(CancellationToken cancellationToken)
        {
            DateTime cutoff = DateTime.UtcNow - IncomingTransferTimeout;
            List<IncomingFileSession> expired;
            lock (_incomingFiles)
            {
                expired = _incomingFiles.Values
                    .Where(session => session.LastActivityUtc < cutoff)
                    .ToList();
            }

            foreach (IncomingFileSession session in expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await session.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    if (session.LastActivityUtc >= cutoff)
                    {
                        continue;
                    }

                    bool removed = false;
                    lock (_incomingFiles)
                    {
                        if (_incomingFiles.TryGetValue(session.SessionKey, out IncomingFileSession? current) &&
                            ReferenceEquals(current, session))
                        {
                            _incomingFiles.Remove(session.SessionKey);
                            removed = true;
                        }
                    }

                    if (removed)
                    {
                        session.IsClosed = true;
                        TryDeleteFile(session.PartFilePath);
                        ReportLog($"Expired incoming transfer removed: {session.FileName}");
                    }
                }
                finally
                {
                    session.Gate.Release();
                }
            }
        }

        private async Task AbortSessionAfterFailureAsync(IncomingFileSession session)
        {
            RemoveSession(session);
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                session.IsClosed = true;
                TryDeleteFile(session.PartFilePath);
            }
            finally
            {
                session.Gate.Release();
            }
        }

        private void EnsureIncomingCapacity(ValidatedTransfer transfer)
        {
            if (Volatile.Read(ref _storageReady) == 0)
            {
                throw new InvalidOperationException(
                    "Attachment storage has not completed initialization.");
            }

            if (_incomingFiles.Count >= MaxIncomingTransfers)
            {
                throw new InvalidOperationException(
                    $"No more than {MaxIncomingTransfers} incoming transfers may be active.");
            }

            int sourceCount = 0;
            long sourceReservedBytes = 0;
            long totalReservedBytes = 0;
            foreach (IncomingFileSession session in _incomingFiles.Values)
            {
                totalReservedBytes = checked(totalReservedBytes + session.FileSize);
                if (string.Equals(session.SourceHash, transfer.SourceHash, StringComparison.Ordinal))
                {
                    sourceCount++;
                    sourceReservedBytes = checked(sourceReservedBytes + session.FileSize);
                }
            }

            if (sourceCount >= MaxIncomingTransfersPerSource)
            {
                throw new InvalidOperationException(
                    $"No more than {MaxIncomingTransfersPerSource} incoming transfers may be active for one peer.");
            }

            long newSourceReservedBytes = checked(sourceReservedBytes + transfer.FileSize);
            long newTotalReservedBytes = checked(totalReservedBytes + transfer.FileSize);
            if (newSourceReservedBytes > MaxReservedIncomingBytesPerSource ||
                newTotalReservedBytes > MaxReservedIncomingBytes)
            {
                throw new IOException("The incoming transfer reservation limit has been reached.");
            }

            if (Volatile.Read(ref _retainedAttachmentCount) + _incomingFiles.Count >= MaxRetainedAttachments)
            {
                RefreshRetainedAttachmentCount();
                if (Volatile.Read(ref _retainedAttachmentCount) + _incomingFiles.Count >= MaxRetainedAttachments)
                {
                    throw new IOException(
                        $"The retained attachment limit ({MaxRetainedAttachments}) has been reached.");
                }
            }

            if (Volatile.Read(ref _retainedAttachmentBytes) >
                MaxRetainedAttachmentBytes - newTotalReservedBytes)
            {
                RefreshRetainedAttachmentCount();
                if (Volatile.Read(ref _retainedAttachmentBytes) >
                    MaxRetainedAttachmentBytes - newTotalReservedBytes)
                {
                    throw new IOException(
                        $"The retained attachment byte limit ({MaxRetainedAttachmentBytes} bytes) has been reached.");
                }
            }

            try
            {
                string? root = Path.GetPathRoot(_attachmentsDirectory);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    long requiredFreeBytes = checked(newTotalReservedBytes + MinimumFreeDiskReserve);
                    if (new DriveInfo(root).AvailableFreeSpace < requiredFreeBytes)
                    {
                        throw new IOException("There is not enough free disk space for the incoming transfer.");
                    }
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Free disk space could not be verified for the incoming transfer.",
                    ex);
            }
        }

        private void RecordIncomingStart(string sourceHash, DateTime utcNow)
        {
            DateTime cutoff = utcNow - TimeSpan.FromMinutes(1);
            PruneStartTimes(_globalIncomingStartTimes, cutoff);
            if (_globalIncomingStartTimes.Count >= MaxIncomingStartsGloballyPerMinute)
            {
                throw new InvalidOperationException("The global incoming file-start rate limit was exceeded.");
            }

            if (!_incomingStartTimesBySource.TryGetValue(sourceHash, out Queue<DateTime>? sourceStarts))
            {
                sourceStarts = new Queue<DateTime>();
                _incomingStartTimesBySource[sourceHash] = sourceStarts;
            }
            PruneStartTimes(sourceStarts, cutoff);
            if (sourceStarts.Count >= MaxIncomingStartsPerSourcePerMinute)
            {
                throw new InvalidOperationException("The incoming file-start rate limit for this peer was exceeded.");
            }

            sourceStarts.Enqueue(utcNow);
            _globalIncomingStartTimes.Enqueue(utcNow);

            if (_incomingStartTimesBySource.Count > 512)
            {
                foreach (string expiredSource in _incomingStartTimesBySource
                    .Where(pair =>
                    {
                        PruneStartTimes(pair.Value, cutoff);
                        return pair.Value.Count == 0;
                    })
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    _incomingStartTimesBySource.Remove(expiredSource);
                }
            }
        }

        private static void PruneStartTimes(Queue<DateTime> timestamps, DateTime cutoff)
        {
            while (timestamps.Count > 0 && timestamps.Peek() <= cutoff)
            {
                timestamps.Dequeue();
            }
        }

        private void EnsureAttachmentsDirectory()
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            if (!Directory.Exists(_attachmentsDirectory))
            {
                throw new DirectoryNotFoundException($"Attachments directory was not created: {_attachmentsDirectory}");
            }
            if (IsReparsePoint(_attachmentsDirectory))
            {
                throw new InvalidDataException(
                    "The managed attachments directory cannot be a reparse point.");
            }
        }

        private static string ResolveAttachmentsDirectory() =>
            Path.Combine(AppStoragePathService.ResolveAppDataDirectory(), "Attachments");

        private static string ValidateStorageDirectory(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("An absolute storage directory is required.", parameterName);
            }

            return Path.GetFullPath(path);
        }

        private static string ResolveDownloadsDirectory()
        {
            IntPtr pathPointer = IntPtr.Zero;
            try
            {
                Guid downloadsFolderId = DownloadsKnownFolderId;
                int result = SHGetKnownFolderPath(
                    ref downloadsFolderId,
                    flags: 0,
                    token: IntPtr.Zero,
                    out pathPointer);
                if (result == 0 && pathPointer != IntPtr.Zero)
                {
                    string? knownDownloads = Marshal.PtrToStringUni(pathPointer);
                    if (!string.IsNullOrWhiteSpace(knownDownloads))
                    {
                        return knownDownloads;
                    }
                }
            }
            catch
            {
                // Fall back when shell known-folder resolution is unavailable.
            }
            finally
            {
                if (pathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = string.IsNullOrWhiteSpace(userProfile)
                ? ""
                : Path.Combine(userProfile, "Downloads");
            if (!string.IsNullOrWhiteSpace(downloads))
            {
                return downloads;
            }

            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
            {
                return documents;
            }

            throw new DirectoryNotFoundException("The Downloads folder could not be resolved.");
        }

        internal static string CopyLegacyAttachmentIdempotently(
            string sourcePath,
            string sourceName,
            string attachmentsDirectory)
        {
            string normalizedDirectory = ValidateStorageDirectory(
                attachmentsDirectory,
                nameof(attachmentsDirectory));
            Directory.CreateDirectory(normalizedDirectory);
            if (IsReparsePoint(normalizedDirectory))
            {
                throw new InvalidDataException(
                    "The attachment migration destination cannot be a reparse point.");
            }
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists || sourceInfo.Length < 0 || sourceInfo.Length > MaxFileSizeBytes)
            {
                throw new InvalidDataException("The legacy attachment is missing or exceeds the 50 MB limit.");
            }
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A legacy attachment cannot be a reparse point.");
            }

            EnsureMigrationDiskSpace(normalizedDirectory, sourceInfo.Length);
            string temporaryPath = GetContainedPath(
                normalizedDirectory,
                $".migration-{Guid.NewGuid():N}.tmp");
            byte[] buffer = new byte[TransferChunkSize];
            try
            {
                byte[] contentHash;
                long copied = 0;
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    TransferChunkSize,
                    FileOptions.SequentialScan))
                using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    TransferChunkSize,
                    FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    while (true)
                    {
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        copied = checked(copied + read);
                        if (copied > MaxFileSizeBytes)
                        {
                            throw new InvalidDataException(
                                "A legacy attachment grew beyond the 50 MB migration limit.");
                        }

                        destination.Write(buffer, 0, read);
                        hasher.AppendData(buffer, 0, read);
                    }

                    if (copied != sourceInfo.Length)
                    {
                        throw new IOException(
                            "The legacy attachment changed while it was being migrated.");
                    }

                    destination.Flush(flushToDisk: true);
                    contentHash = hasher.GetHashAndReset();
                }

                string hashText = Convert.ToHexString(contentHash).ToLowerInvariant();
                CryptographicOperations.ZeroMemory(contentHash);
                if (!FileMatchesHash(sourcePath, copied, hashText))
                {
                    throw new IOException(
                        "The legacy attachment changed while its migration copy was being verified.");
                }
                string destinationName = BuildMigratedFileName(SafeFileName(sourceName), hashText);
                string destinationPath = GetContainedPath(normalizedDirectory, destinationName);
                if (File.Exists(destinationPath))
                {
                    if (!FileMatchesHash(destinationPath, copied, hashText))
                    {
                        throw new IOException(
                            "A legacy attachment destination exists with unexpected content.");
                    }

                    return destinationPath;
                }

                EnsureRetainedMigrationCapacity(
                    normalizedDirectory,
                    temporaryPath,
                    copied);

                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                }
                catch (IOException) when (
                    File.Exists(destinationPath) &&
                    FileMatchesHash(destinationPath, copied, hashText))
                {
                    // A concurrent migration completed the same content first.
                }

                return destinationPath;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                TryDeleteFile(temporaryPath);
            }
        }

        private static string BuildMigratedFileName(string safeName, string hashText)
        {
            string extension = Path.GetExtension(safeName);
            string suffix = "_migrated-" + hashText;
            int maxExtensionLength = Math.Max(0, MaxFileNameLength - suffix.Length - 1);
            extension = TakeRunesWithinUtf16Length(extension, maxExtensionLength);
            string stem = Path.GetFileNameWithoutExtension(safeName);
            int stemBudget = Math.Max(1, MaxFileNameLength - suffix.Length - extension.Length);
            stem = TakeRunesWithinUtf16Length(stem, stemBudget);
            if (stem.Length == 0)
            {
                stem = "_";
            }

            return stem + suffix + extension;
        }

        private static bool FileMatchesHash(string path, long expectedLength, string expectedHash)
        {
            var info = new FileInfo(path);
            if (!info.Exists || IsReparsePoint(path) ||
                info.Length != expectedLength || info.Length > MaxFileSizeBytes)
            {
                return false;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                TransferChunkSize,
                FileOptions.SequentialScan);
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureMigrationDiskSpace(string attachmentsDirectory, long fileSize)
        {
            string? root = Path.GetPathRoot(attachmentsDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            long required = checked(fileSize + MinimumFreeDiskReserve);
            if (new DriveInfo(root).AvailableFreeSpace < required)
            {
                throw new IOException("There is not enough disk space to migrate a legacy attachment.");
            }
        }

        private static void EnsureRetainedMigrationCapacity(
            string attachmentsDirectory,
            string currentTemporaryPath,
            long incomingLength)
        {
            int count = 0;
            long bytes = 0;
            foreach (string path in Directory.EnumerateFiles(attachmentsDirectory))
            {
                if (string.Equals(path, currentTemporaryPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (IsReparsePoint(path))
                {
                    throw new InvalidDataException(
                        "Attachment retention capacity cannot be verified through a reparse point.");
                }

                var info = new FileInfo(path);
                if (info.Length < 0 || bytes > MaxRetainedAttachmentBytes - info.Length)
                {
                    throw new IOException("The retained attachment byte limit has been reached.");
                }
                bytes += info.Length;
                count++;
                if (count >= MaxRetainedAttachments || bytes >= MaxRetainedAttachmentBytes)
                {
                    throw new IOException("The retained attachment capacity has been reached.");
                }
            }

            if (count >= MaxRetainedAttachments ||
                incomingLength < 0 ||
                bytes > MaxRetainedAttachmentBytes - incomingLength)
            {
                throw new IOException("The retained attachment capacity has been reached.");
            }
        }

        private void CleanupStoredFiles()
        {
            if (!Directory.Exists(_attachmentsDirectory))
            {
                return;
            }

            HashSet<string> activePaths;
            lock (_incomingFiles)
            {
                activePaths = _incomingFiles.Values
                    .Select(session => session.PartFilePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            DateTime now = DateTime.UtcNow;
            IReadOnlyList<string> storedFiles = EnumerateMaintenanceFiles(
                _attachmentsDirectory,
                out _);
            foreach (string filePath in storedFiles)
            {
                try
                {
                    if (activePaths.Contains(filePath))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(filePath);
                    if (IsReparsePoint(filePath))
                    {
                        ReportLog($"Attachment cleanup skipped reparse point: {fileInfo.Name}");
                        continue;
                    }
                    bool isManagedTemporary = IsManagedPartialFile(fileInfo.Name) ||
                                              IsManagedMigrationTempFile(fileInfo.Name);
                    TimeSpan retention = isManagedTemporary ? PartialFileRetention : AttachmentRetention;
                    if (now - fileInfo.LastWriteTimeUtc > retention)
                    {
                        fileInfo.Delete();
                    }
                }
                catch (Exception ex)
                {
                    ReportLog($"Attachment cleanup skipped: {Path.GetFileName(filePath)} ({ex.GetType().Name})");
                }
            }
            RefreshRetainedAttachmentCount();
        }

        private void CleanupOutgoingCache(string? protectedFilePath = null)
        {
            lock (_outgoingCacheGate)
            {
                if (!Directory.Exists(_outgoingCacheDirectory))
                {
                    return;
                }
                if (IsReparsePoint(_outgoingCacheDirectory))
                {
                    throw new InvalidDataException(
                        "The managed outgoing cache directory cannot be a reparse point.");
                }

                string protectedPath = "";
                if (!string.IsNullOrWhiteSpace(protectedFilePath))
                {
                    try
                    {
                        protectedPath = Path.GetFullPath(protectedFilePath);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                        PathTooLongException)
                    {
                        throw new InvalidDataException(
                            "The protected outgoing snapshot path is invalid.",
                            ex);
                    }
                }

                DateTime cutoff = DateTime.UtcNow - OutgoingCacheRetention;
                IReadOnlyList<string> cachedFiles = EnumerateMaintenanceFiles(
                    _outgoingCacheDirectory,
                    out bool completed);
                if (!completed)
                {
                    throw new IOException(
                        "The outgoing cache is too large or could not be enumerated safely.");
                }

                var retained = new List<FileInfo>(cachedFiles.Count);
                foreach (string filePath in cachedFiles)
                {
                    var info = new FileInfo(filePath);
                    if (!info.Exists)
                    {
                        continue;
                    }
                    if (IsReparsePoint(filePath))
                    {
                        throw new InvalidDataException(
                            "The managed outgoing cache cannot contain reparse points.");
                    }

                    bool isProtected = string.Equals(
                        filePath,
                        protectedPath,
                        StringComparison.OrdinalIgnoreCase);
                    if (!isProtected && info.LastWriteTimeUtc < cutoff)
                    {
                        try
                        {
                            info.Delete();
                            continue;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            ReportLog(
                                $"Outgoing cache expiry cleanup deferred: {info.Name} ({ex.GetType().Name})");
                        }
                    }
                    retained.Add(info);
                }

                long totalBytes = 0;
                foreach (FileInfo info in retained)
                {
                    if (info.Length < 0)
                    {
                        throw new IOException("An outgoing cache file has an invalid length.");
                    }
                    totalBytes = totalBytes > long.MaxValue - info.Length
                        ? long.MaxValue
                        : totalBytes + info.Length;
                }

                foreach (FileInfo candidate in retained
                    .OrderBy(info => info.LastWriteTimeUtc)
                    .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList())
                {
                    if (retained.Count <= MaxOutgoingCacheFiles &&
                        totalBytes <= MaxOutgoingCacheBytes)
                    {
                        break;
                    }
                    if (string.Equals(
                            candidate.FullName,
                            protectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        long length = candidate.Length;
                        candidate.Delete();
                        retained.Remove(candidate);
                        totalBytes = Math.Max(0, totalBytes - length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ReportLog(
                            $"Outgoing cache quota cleanup deferred: {candidate.Name} ({ex.GetType().Name})");
                    }
                }

                if (retained.Count > MaxOutgoingCacheFiles ||
                    totalBytes > MaxOutgoingCacheBytes)
                {
                    throw new IOException(
                        "The outgoing cache quota is exhausted and old snapshots could not be removed.");
                }
            }
        }

        private IReadOnlyList<string> EnumerateMaintenanceFiles(
            string directory,
            out bool completed)
        {
            var files = new List<string>();
            completed = false;
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(directory))
                {
                    if (files.Count >= MaxMaintenanceFilesPerPass)
                    {
                        ReportLog(
                            $"Maintenance scan limited to {MaxMaintenanceFilesPerPass} files: {directory}");
                        return files;
                    }

                    files.Add(filePath);
                }

                completed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                DirectoryNotFoundException or PathTooLongException)
            {
                ReportLog($"Maintenance scan skipped: {directory} ({ex.GetType().Name})");
            }

            return files;
        }

        private void RefreshRetainedAttachmentCount()
        {
            lock (_incomingFiles)
            {
                int count = 0;
                long bytes = 0;
                HashSet<string> activePaths = _incomingFiles.Values
                    .Select(session => session.PartFilePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string path in Directory.EnumerateFiles(_attachmentsDirectory))
                    {
                        if (activePaths.Contains(path))
                        {
                            continue;
                        }
                        if (IsReparsePoint(path))
                        {
                            // A link can target an unbounded external tree/file. Do not
                            // follow it and fail closed for new inbound persistence.
                            count = MaxRetainedAttachments;
                            bytes = MaxRetainedAttachmentBytes;
                            break;
                        }
                        var info = new FileInfo(path);
                        if (info.Length < 0 || bytes > MaxRetainedAttachmentBytes - info.Length)
                        {
                            count = MaxRetainedAttachments;
                            bytes = MaxRetainedAttachmentBytes;
                            break;
                        }
                        bytes += info.Length;
                        count++;
                        if (count >= MaxRetainedAttachments ||
                            bytes >= MaxRetainedAttachmentBytes)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    DirectoryNotFoundException or PathTooLongException)
                {
                    // Fail closed for new inbound files when storage cannot be counted.
                    count = MaxRetainedAttachments;
                    bytes = MaxRetainedAttachmentBytes;
                    ReportLog($"Attachment count unavailable: {ex.GetType().Name}");
                }

                Volatile.Write(ref _retainedAttachmentCount, count);
                Volatile.Write(ref _retainedAttachmentBytes, bytes);
            }
        }

        private static int GetExpectedChunkCount(long fileSize)
        {
            if (fileSize < 0 || fileSize > MaxFileSizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSize));
            }

            return fileSize == 0
                ? 1
                : checked((int)((fileSize + TransferChunkSize - 1) / TransferChunkSize));
        }

        private static int GetExpectedChunkLength(long fileSize, int chunkCount, int chunkIndex)
        {
            if (chunkIndex < 0 || chunkIndex >= chunkCount)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIndex));
            }

            if (fileSize == 0)
            {
                return 0;
            }

            long offset = checked((long)chunkIndex * TransferChunkSize);
            return checked((int)Math.Min(TransferChunkSize, fileSize - offset));
        }

        private static async Task<int> ReadExactlyUpToAsync(
            Stream stream,
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, count - totalRead),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static void ValidateLocalSourceFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 0 || info.Length > MaxFileSizeBytes)
            {
                throw new InvalidDataException("The attachment is outside the 0-50 MB range.");
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The attachment source cannot be a reparse point.");
            }
        }

        private static string GetSourceHash(string transferSourceId)
        {
            string normalized = transferSourceId?.Trim() ?? "";
            if (normalized.Length == 0 ||
                normalized.Length > MaxSourceIdentityLength ||
                !string.Equals(transferSourceId, normalized, StringComparison.Ordinal) ||
                normalized.Any(char.IsControl))
            {
                throw new InvalidDataException("The transfer source identity is missing or too long.");
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string CreateSessionKey(string sourceHash, string fileId) => $"{sourceHash}:{fileId}";

        private static bool TryNormalizeFileId(string? fileId, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(fileId) || !Guid.TryParse(fileId, out Guid parsed))
            {
                return false;
            }

            normalized = parsed.ToString("N");
            return true;
        }

        private static bool TryNormalizeAcknowledgementIdentity(
            string? identity,
            out string normalized)
        {
            normalized = identity?.Trim() ?? "";
            return normalized.Length > 0 &&
                   normalized.Length <= MaxSourceIdentityLength &&
                   string.Equals(identity, normalized, StringComparison.Ordinal) &&
                   !normalized.Any(char.IsControl);
        }

        private static string SafeFileName(string? fileName)
        {
            string value = string.IsNullOrWhiteSpace(fileName)
                ? "received_file"
                : Path.GetFileName(fileName);

            var builder = new StringBuilder(value.Length);
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (rune.Value <= char.MaxValue &&
                    (Path.GetInvalidFileNameChars().Contains((char)rune.Value) ||
                     char.IsControl((char)rune.Value) ||
                     IsBidirectionalControl(rune.Value)))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(rune.ToString());
                }
            }

            value = builder.ToString().Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');
            if (value.Length == 0 || value is "." or "..")
            {
                value = "received_file";
            }

            if (value.Length > MaxFileNameLength)
            {
                string extension = Path.GetExtension(value);
                string stem = Path.GetFileNameWithoutExtension(value);
                int extensionBudget = Math.Min(extension.Length, MaxFileNameLength - 1);
                extension = TakeRunesWithinUtf16Length(extension, extensionBudget);
                stem = TakeRunesWithinUtf16Length(stem, MaxFileNameLength - extension.Length);
                if (stem.Length == 0)
                {
                    stem = "_";
                    extension = TakeRunesWithinUtf16Length(extension, MaxFileNameLength - 1);
                }

                value = stem + extension;
            }

            string baseName = Path.GetFileNameWithoutExtension(value).TrimEnd('.', ' ');
            if (ReservedWindowsNames.Contains(baseName))
            {
                value = "_" + TakeRunesWithinUtf16Length(value, MaxFileNameLength - 1);
            }

            // These namespaces are reserved for service-owned temporary files.
            // Without this guard, a remote sender could choose a temp-looking final
            // name and receive shorter retention treatment in maintenance.
            if (IsManagedPartialFile(value) || IsManagedMigrationTempFile(value))
            {
                value = "_" + TakeRunesWithinUtf16Length(value, MaxFileNameLength - 1);
            }

            return TakeRunesWithinUtf16Length(value, MaxFileNameLength);
        }

        private static string TakeRunesWithinUtf16Length(string value, int maximumLength)
        {
            var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
            foreach (Rune rune in value.EnumerateRunes())
            {
                if (builder.Length + rune.Utf16SequenceLength > maximumLength)
                {
                    break;
                }

                builder.Append(rune.ToString());
            }

            return builder.ToString();
        }

        private static bool IsBidirectionalControl(int value) =>
            value is '\u061C' or '\u200E' or '\u200F' or
            >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069';

        private static string GetContainedPath(string directory, string fileName)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, fileName));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The resolved attachment path escaped its storage directory.");
            }

            return candidate;
        }

        private static string GetUniqueFilePath(string directory, string fileName)
        {
            for (int attempt = 0; attempt < MaxUniqueNameAttempts; attempt++)
            {
                string candidate = GetUniqueCandidatePath(directory, fileName, attempt);
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("A unique attachment name could not be allocated.");
        }

        private static string MoveToUniqueFilePath(
            string sourcePath,
            string directory,
            string fileName)
        {
            for (int attempt = 0; attempt < MaxUniqueNameAttempts; attempt++)
            {
                string destinationPath = GetUniqueCandidatePath(directory, fileName, attempt);
                try
                {
                    File.Move(sourcePath, destinationPath, overwrite: false);
                    return destinationPath;
                }
                catch (IOException) when (File.Exists(sourcePath) && File.Exists(destinationPath))
                {
                    // Another writer claimed the name after our existence check.
                }
            }

            throw new IOException("A unique attachment name could not be claimed.");
        }

        private static string GetUniqueCandidatePath(
            string directory,
            string fileName,
            int attempt)
        {
            if (attempt == 0)
            {
                return GetContainedPath(directory, fileName);
            }

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            string suffix = attempt <= MaxSequentialNameAttempts
                ? $"_{attempt}"
                : $"_{Guid.NewGuid():N}";
            extension = TakeRunesWithinUtf16Length(
                extension,
                Math.Max(0, MaxFileNameLength - suffix.Length - 1));
            int nameBudget = Math.Max(1, MaxFileNameLength - extension.Length - suffix.Length);
            string candidateName =
                TakeRunesWithinUtf16Length(name, nameBudget) + suffix + extension;
            return GetContainedPath(directory, candidateName);
        }

        private static bool IsManagedPartialFile(string fileName)
        {
            if (!fileName.StartsWith(PartialFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string payload = fileName[PartialFilePrefix.Length..^".part".Length];
            string[] segments = payload.Split('-');
            return segments.Length == 3 &&
                   segments[0].Length == 16 && segments[0].All(Uri.IsHexDigit) &&
                   segments[1].Length == 32 && Guid.TryParseExact(segments[1], "N", out _) &&
                   segments[2].Length == 32 && Guid.TryParseExact(segments[2], "N", out _);
        }

        private static bool IsManagedMigrationTempFile(string fileName)
        {
            const string prefix = ".migration-";
            const string suffix = ".tmp";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string identifier = fileName[prefix.Length..^suffix.Length];
            return identifier.Length == 32 && Guid.TryParseExact(identifier, "N", out _);
        }

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static string SanitizeAcknowledgementError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "unspecified error";
            }

            var builder = new StringBuilder(Math.Min(error.Length, 256));
            foreach (Rune rune in error.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    continue;
                }

                if (builder.Length + rune.Utf16SequenceLength > 256)
                {
                    break;
                }

                builder.Append(rune.ToString());
            }

            string value = builder.ToString().Trim();
            return value.Length == 0 ? "unspecified error" : value;
        }

        private static void TryDeleteFile(string path)
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
                // Cleanup is best effort; retention cleanup retries stale managed partials.
            }
        }

        private sealed class ValidatedTransfer
        {
            public string SourceHash { get; init; } = "";
            public string FileId { get; init; } = "";
            public string FileName { get; init; } = "";
            public string WireFileName { get; init; } = "";
            public long FileSize { get; init; }
            public int ChunkCount { get; init; }
            public string SenderId { get; init; } = "";
            public string SenderName { get; init; } = "";
            public string ShortSessionId { get; init; } = "";
            public bool IsGroup { get; init; }
            public string ConversationId { get; init; } = "";
            public string RelaySenderId { get; init; } = "";
            public string RelaySenderName { get; init; } = "";
            public string RelayShortSessionId { get; init; } = "";
            public int HopCount { get; init; }
        }

        private sealed class IncomingFileSession
        {
            public string SessionKey { get; init; } = "";
            public string SourceHash { get; init; } = "";
            public string FileId { get; init; } = "";
            public string FileName { get; init; } = "";
            public string WireFileName { get; init; } = "";
            public long FileSize { get; init; }
            public int ExpectedChunkCount { get; init; }
            public string SenderId { get; init; } = "";
            public string SenderName { get; init; } = "";
            public string ShortSessionId { get; init; } = "";
            public bool IsGroup { get; init; }
            public string ConversationId { get; init; } = "";
            public string RelaySenderId { get; init; } = "";
            public string RelaySenderName { get; init; } = "";
            public string RelayShortSessionId { get; init; } = "";
            public int HopCount { get; init; }
            public string PartFilePath { get; init; } = "";
            public long ReceivedBytes { get; set; }
            public DateTime LastActivityUtc { get; set; }
            public volatile bool IsClosed;
            public HashSet<int> ReceivedChunkIndexes { get; } = new();
            public SemaphoreSlim Gate { get; } = new(1, 1);
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Shell32.dll")]
        private static extern int SHGetKnownFolderPath(
            ref Guid folderId,
            uint flags,
            IntPtr token,
            out IntPtr path);
    }
}
