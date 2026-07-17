using System;
using System.Collections.Generic;
using System.IO;
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
    }

    public sealed class FileTransferDisplayResult
    {
        public string Message { get; init; } = "";
        public string FileName { get; init; } = "";
        public string LocalFilePath { get; init; } = "";
    }

    public sealed class FileTransferService
    {
        private const long MaxFileSize = 50 * 1024 * 1024;
        private const int ChunkSize = 128 * 1024;
        private static readonly TimeSpan AttachmentRetention = TimeSpan.FromDays(30);
        private static readonly TimeSpan PartialFileRetention = TimeSpan.FromDays(1);

        private readonly Dictionary<string, IncomingFileSession> _incomingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _attachmentsDirectory;
        private readonly string _downloadsDirectory;

        public FileTransferService()
        {
            _attachmentsDirectory = ResolveAttachmentsDirectory();
            _downloadsDirectory = ResolveDownloadsDirectory();
            EnsureAttachmentsDirectory();
        }

        public string AttachmentsDirectory => _attachmentsDirectory;

        public string DownloadsDirectory => _downloadsDirectory;

        public event Action<string>? LogReceived;
        public event Action<FileTransferProgress>? ProgressChanged;

        public void EnsureStorageReady()
        {
            EnsureAttachmentsDirectory();
            LogReceived?.Invoke($"Attachments directory resolved: {_attachmentsDirectory}");
            LogReceived?.Invoke($"Attachments directory exists: {Directory.Exists(_attachmentsDirectory)}");
            CleanupTemporaryFiles();
        }

        public string SaveToDownloads(string localFilePath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            {
                throw new FileNotFoundException("Attachment file was not found.", localFilePath);
            }

            Directory.CreateDirectory(_downloadsDirectory);
            string safeName = SafeFileName(fileName);
            string destinationPath = GetUniqueFilePath(_downloadsDirectory, safeName);
            File.Copy(localFilePath, destinationPath);
            LogReceived?.Invoke($"Attachment saved to Downloads: {destinationPath}");
            return destinationPath;
        }

        public async Task SendFileAsync(
            string filePath,
            string senderId,
            string senderName,
            string shortSessionId,
            bool isGroup,
            string conversationId,
            Func<ChatMessage, Task> sendAsync)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File was not found.", filePath);
            }

            EnsureAttachmentsDirectory();

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSize)
            {
                throw new InvalidOperationException("File size exceeds the 50 MB limit.");
            }

            string fileId = Guid.NewGuid().ToString("N");
            string fileName = Path.GetFileName(filePath);
            long fileSize = fileInfo.Length;
            int chunkCount = Math.Max(1, (int)Math.Ceiling(fileSize / (double)ChunkSize));

            LogReceived?.Invoke($"File send started: {fileName} ({fileSize / 1024.0:F1} KB)");
            await sendAsync(CreateControlMessage("file_start"));

            byte[] buffer = new byte[ChunkSize];
            await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (int index = 0; index < chunkCount; index++)
                {
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize));
                    byte[] chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                    await sendAsync(new ChatMessage
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
                        ChunkBase64 = Convert.ToBase64String(chunk),
                        Body = ""
                    });

                    ProgressChanged?.Invoke(new FileTransferProgress
                    {
                        FileId = fileId,
                        FileName = fileName,
                        Percent = (index + 1) * 100.0 / chunkCount
                    });
                }
            }

            await sendAsync(CreateControlMessage("file_end"));

            ProgressChanged?.Invoke(new FileTransferProgress
            {
                FileId = fileId,
                FileName = fileName,
                Percent = 100,
                IsComplete = true,
                LocalFilePath = filePath
            });

            LogReceived?.Invoke($"File send completed: {fileName}");

            ChatMessage CreateControlMessage(string type)
            {
                return new ChatMessage
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
        }

        public Task<FileTransferDisplayResult?> HandleFileStartAsync(ChatMessage message)
        {
            if (!TryNormalizeFileId(message.FileId, out string fileId))
            {
                throw new InvalidDataException("FileId is invalid.");
            }

            long fileSize = message.FileSize ?? -1;
            int chunkCount = message.ChunkCount ?? -1;
            int expectedChunkCount = fileSize >= 0
                ? Math.Max(1, (int)Math.Ceiling(fileSize / (double)ChunkSize))
                : -1;
            if (fileSize < 0 || fileSize > MaxFileSize || chunkCount != expectedChunkCount)
            {
                throw new InvalidDataException(
                    $"File metadata is invalid. Size={fileSize}, Chunks={chunkCount}");
            }

            string fileName = SafeFileName(message.FileName);
            EnsureAttachmentsDirectory();
            string partFilePath = Path.Combine(_attachmentsDirectory, $"{fileId}.part");

            lock (_incomingFiles)
            {
                if (_incomingFiles.ContainsKey(fileId))
                {
                    throw new InvalidDataException($"File session already exists: {fileId}");
                }

                if (File.Exists(partFilePath))
                {
                    File.Delete(partFilePath);
                }

                _incomingFiles[fileId] = new IncomingFileSession
                {
                    FileId = fileId,
                    FileName = fileName,
                    FileSize = fileSize,
                    ExpectedChunkCount = chunkCount,
                    PartFilePath = partFilePath
                };
            }

            LogReceived?.Invoke($"File receive started: {fileName}");
            return Task.FromResult<FileTransferDisplayResult?>(new FileTransferDisplayResult
            {
                Message = $"受信開始: {fileName}",
                FileName = fileName
            });
        }

        public async Task<string?> HandleFileChunkAsync(ChatMessage message)
        {
            if (!TryNormalizeFileId(message.FileId, out string fileId) ||
                message.ChunkBase64 == null)
            {
                return null;
            }

            IncomingFileSession? session = GetSession(fileId);
            if (session == null)
            {
                LogReceived?.Invoke($"File chunk ignored because session was not found: {message.FileId}");
                return null;
            }

            int chunkIndex = message.ChunkIndex ?? -1;
            if (chunkIndex < 0 || chunkIndex >= session.ExpectedChunkCount)
            {
                throw new InvalidDataException(
                    $"File chunk index is invalid: {chunkIndex}/{session.ExpectedChunkCount}");
            }

            await session.Gate.WaitAsync();
            try
            {
                byte[] chunk = Convert.FromBase64String(message.ChunkBase64);
                long chunkOffset = (long)chunkIndex * ChunkSize;
                int expectedLength = (int)Math.Min(ChunkSize, session.FileSize - chunkOffset);
                if (chunk.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        $"File chunk size is invalid: Index={chunkIndex}, Bytes={chunk.Length}, Expected={expectedLength}");
                }

                if (session.ReceivedChunkIndexes.Contains(chunkIndex))
                {
                    LogReceived?.Invoke($"Duplicate file chunk ignored: {fileId}/{chunkIndex}");
                    return null;
                }

                await using var stream = new FileStream(session.PartFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Seek(chunkOffset, SeekOrigin.Begin);
                await stream.WriteAsync(chunk);
                session.ReceivedChunkIndexes.Add(chunkIndex);
                session.ReceivedBytes += chunk.Length;

                double percent = session.FileSize > 0
                    ? Math.Min(100, session.ReceivedBytes * 100.0 / session.FileSize)
                    : 0;

                ProgressChanged?.Invoke(new FileTransferProgress
                {
                    FileId = session.FileId,
                    FileName = session.FileName,
                    Percent = percent
                });

                return null;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        public async Task<FileTransferDisplayResult?> HandleFileEndAsync(ChatMessage message)
        {
            if (!TryNormalizeFileId(message.FileId, out string fileId))
            {
                return null;
            }

            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(fileId, out session);
            }

            if (session == null)
            {
                LogReceived?.Invoke($"File end ignored because session was not found: {message.FileId}");
                return null;
            }

            EnsureAttachmentsDirectory();
            await WaitForExpectedChunksAsync(session);

            await session.Gate.WaitAsync();
            try
            {
                if (session.ExpectedChunkCount > 0 &&
                    session.ReceivedChunkIndexes.Count < session.ExpectedChunkCount)
                {
                    throw new InvalidDataException(
                        $"File receive incomplete: {session.FileName} chunks={session.ReceivedChunkIndexes.Count}/{session.ExpectedChunkCount}");
                }

                if (session.FileSize > 0 && session.ReceivedBytes != session.FileSize)
                {
                    throw new InvalidDataException(
                        $"File receive size mismatch: {session.FileName} bytes={session.ReceivedBytes}/{session.FileSize}");
                }

                string finalPath = GetUniqueFilePath(_attachmentsDirectory, session.FileName);
                File.Move(session.PartFilePath, finalPath);

                ProgressChanged?.Invoke(new FileTransferProgress
                {
                    FileId = session.FileId,
                    FileName = session.FileName,
                    Percent = 100,
                    IsComplete = true,
                    LocalFilePath = finalPath
                });

                LogReceived?.Invoke($"File receive completed: {session.FileName} -> {finalPath}");
                return new FileTransferDisplayResult
                {
                    Message = $"受信完了: {session.FileName}",
                    FileName = session.FileName,
                    LocalFilePath = finalPath
                };
            }
            finally
            {
                try
                {
                    lock (_incomingFiles)
                    {
                        _incomingFiles.Remove(session.FileId);
                    }
                }
                finally
                {
                    session.Gate.Release();
                }
            }
        }

        private IncomingFileSession? GetSession(string fileId)
        {
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(fileId, out IncomingFileSession? session);
                return session;
            }
        }

        private void EnsureAttachmentsDirectory()
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            if (!Directory.Exists(_attachmentsDirectory))
            {
                throw new DirectoryNotFoundException($"Attachments directory was not created: {_attachmentsDirectory}");
            }
        }

        private static string ResolveAttachmentsDirectory()
        {
            return Path.Combine(AppStoragePathService.ResolveAppDataDirectory(), "Attachments");
        }

        private static string ResolveDownloadsDirectory()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsDirectory = string.IsNullOrWhiteSpace(userProfile)
                ? ""
                : Path.Combine(userProfile, "Downloads");

            if (string.IsNullOrWhiteSpace(downloadsDirectory))
            {
                downloadsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            if (string.IsNullOrWhiteSpace(downloadsDirectory))
            {
                downloadsDirectory = AppContext.BaseDirectory;
            }

            return downloadsDirectory;
        }

        private void CleanupTemporaryFiles()
        {
            if (!Directory.Exists(_attachmentsDirectory))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            int deleted = 0;

            foreach (string filePath in Directory.EnumerateFiles(_attachmentsDirectory))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    TimeSpan age = now - fileInfo.LastWriteTimeUtc;
                    bool isPartial = string.Equals(fileInfo.Extension, ".part", StringComparison.OrdinalIgnoreCase);
                    TimeSpan retention = isPartial ? PartialFileRetention : AttachmentRetention;

                    if (age <= retention)
                    {
                        continue;
                    }

                    fileInfo.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"Attachment cleanup skipped: {Path.GetFileName(filePath)} ({ex.GetType().Name})");
                }
            }

            if (deleted > 0)
            {
                LogReceived?.Invoke($"Attachment cleanup completed: {deleted} file(s) deleted");
            }
        }

        private static async Task WaitForExpectedChunksAsync(IncomingFileSession session)
        {
            if (session.ExpectedChunkCount <= 0)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!timeout.IsCancellationRequested)
            {
                await session.Gate.WaitAsync();
                try
                {
                    if (session.ReceivedChunkIndexes.Count >= session.ExpectedChunkCount)
                    {
                        return;
                    }
                }
                finally
                {
                    session.Gate.Release();
                }

                try
                {
                    await Task.Delay(100, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static string SafeFileName(string? fileName)
        {
            string value = string.IsNullOrWhiteSpace(fileName)
                ? "received_file"
                : Path.GetFileName(fileName);

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }

        private static bool TryNormalizeFileId(string? fileId, out string normalized)
        {
            normalized = "";
            if (!Guid.TryParseExact(fileId, "N", out Guid parsed))
            {
                return false;
            }

            normalized = parsed.ToString("N");
            return true;
        }

        private static string GetUniqueFilePath(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return path;
            }

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 1;

            do
            {
                path = Path.Combine(directory, $"{name}_{index}{extension}");
                index++;
            }
            while (File.Exists(path));

            return path;
        }

        private sealed class IncomingFileSession
        {
            public string FileId { get; init; } = "";
            public string FileName { get; init; } = "";
            public long FileSize { get; init; }
            public int ExpectedChunkCount { get; init; }
            public string PartFilePath { get; init; } = "";
            public long ReceivedBytes { get; set; }
            public HashSet<int> ReceivedChunkIndexes { get; } = new();
            public SemaphoreSlim Gate { get; } = new(1, 1);
        }
    }
}
