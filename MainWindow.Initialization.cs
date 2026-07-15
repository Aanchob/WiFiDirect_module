using System;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Database;
using direct_module.Services;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private int _fileStorageReady;

        private async Task InitializeLocalStorageAsync()
        {
            // Database initialization can migrate legacy attachments into the file
            // store. Complete it before storage maintenance/counting so readiness is
            // published from a stable post-migration snapshot. A DB failure is
            // isolated: file-only operation remains available after its own init.
            await InitializeChatHistoryAsync();
            await InitializeFileStorageAsync();
        }

        private async Task InitializeChatHistoryAsync()
        {
            try
            {
                (DatabaseService Database, ChatHistoryService History) initialized =
                    await Task.Run(() =>
                    {
                        var database = new DatabaseService();
                        var repository = new ChatRepository(database);
                        var history = new ChatHistoryService(
                            repository,
                            LocalHistoryPeerId,
                            Environment.MachineName);
                        return (database, history);
                    }, _windowLifetimeCancellation.Token);

                Volatile.Write(ref _chatHistoryService, initialized.History);
                _chatHistoryReady.TrySetResult(initialized.History);
                EnqueueLog("履歴DB初期化成功", LogLevel.Success);
                EnqueueLog($"履歴DBパス: {initialized.Database.DatabasePath}");
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
                _chatHistoryReady.TrySetCanceled(_windowLifetimeCancellation.Token);
            }
            catch (Exception ex)
            {
                _chatHistoryReady.TrySetResult(null);
                EnqueueLog($"履歴DB初期化失敗: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task InitializeFileStorageAsync()
        {
            try
            {
                await Task.Run(
                    _fileTransferService.EnsureStorageReady,
                    _windowLifetimeCancellation.Token);
                Volatile.Write(ref _fileStorageReady, 1);
                EnqueueLog($"添付ファイル一時保存先: {_fileTransferService.AttachmentsDirectory}");
                EnqueueLog($"添付ファイルDownloads保存先: {_fileTransferService.DownloadsDirectory}");
                DispatcherQueue.TryEnqueue(UpdateSendButtonState);
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                EnqueueLog($"添付ファイル保存先の初期化に失敗しました: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task<ChatHistoryService?> GetChatHistoryServiceWhenReadyAsync()
        {
            ChatHistoryService? available = Volatile.Read(ref _chatHistoryService);
            if (available != null)
            {
                return available;
            }

            try
            {
                return await _chatHistoryReady.Task.WaitAsync(
                    _windowLifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
                return null;
            }
        }

        private async Task<ChatHistoryService?> GetChatHistoryServiceWhenReadyForSaveAsync()
        {
            ChatHistoryService? available = Volatile.Read(ref _chatHistoryService);
            if (available != null)
            {
                return available;
            }

            try
            {
                // Pending saves are part of graceful shutdown. Do not cancel them
                // merely because the window lifetime token has been signaled.
                return await _chatHistoryReady.Task;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }
}
