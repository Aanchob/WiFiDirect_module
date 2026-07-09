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

        private readonly Dictionary<string, IncomingFileSession> _incomingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _attachmentsDirectory;

        public FileTransferService()
        {
            _attachmentsDirectory = ResolveAttachmentsDirectory();
            EnsureAttachmentsDirectory();
        }

        public string AttachmentsDirectory => _attachmentsDirectory;

        public event Action<string>? LogReceived;
        public event Action<FileTransferProgress>? ProgressChanged;

        public void EnsureStorageReady()
        {
            EnsureAttachmentsDirectory();
            LogReceived?.Invoke($"Attachments directory ready: {_attachmentsDirectory}");
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
            if (string.IsNullOrWhiteSpace(message.FileId))
            {
                return Task.FromResult<FileTransferDisplayResult?>(null);
            }

            string fileName = SafeFileName(message.FileName);
            EnsureAttachmentsDirectory();
            string partFilePath = Path.Combine(_attachmentsDirectory, $"{message.FileId}.part");

            if (File.Exists(partFilePath))
            {
                File.Delete(partFilePath);
            }

            lock (_incomingFiles)
            {
                _incomingFiles[message.FileId] = new IncomingFileSession
                {
                    FileId = message.FileId,
                    FileName = fileName,
                    FileSize = message.FileSize ?? 0,
                    ExpectedChunkCount = message.ChunkCount ?? 0,
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
            if (string.IsNullOrWhiteSpace(message.FileId) ||
                string.IsNullOrWhiteSpace(message.ChunkBase64))
            {
                return null;
            }

            IncomingFileSession? session = GetSession(message.FileId);
            if (session == null)
            {
                LogReceived?.Invoke($"File chunk ignored because session was not found: {message.FileId}");
                return null;
            }

            int chunkIndex = message.ChunkIndex ?? -1;
            if (chunkIndex < 0)
            {
                LogReceived?.Invoke($"File chunk ignored because chunk index was missing: {message.FileId}");
                return null;
            }

            await session.Gate.WaitAsync();
            try
            {
                byte[] chunk = Convert.FromBase64String(message.ChunkBase64);
                await using var stream = new FileStream(session.PartFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Seek(chunkIndex * ChunkSize, SeekOrigin.Begin);
                await stream.WriteAsync(chunk);
                if (session.ReceivedChunkIndexes.Add(chunkIndex))
                {
                    session.ReceivedBytes += chunk.Length;
                }

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
            if (string.IsNullOrWhiteSpace(message.FileId))
            {
                return null;
            }

            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(message.FileId, out session);
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
