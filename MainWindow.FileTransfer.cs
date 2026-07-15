using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private readonly SemaphoreSlim _outgoingFileSendGate = new(1, 1);

        private void AttachmentImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is FrameworkElement image)
            {
                image.Visibility = Visibility.Collapsed;
            }

            AddLog("添付画像のプレビューを安全にデコードできませんでした。", LogLevel.Error);
        }

        private void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            if (Volatile.Read(ref _fileStorageReady) == 0)
            {
                AddLog("ファイル保存先の初期化が完了していません。", LogLevel.Error);
                return;
            }

            if (!_outgoingFileSendGate.Wait(0))
            {
                AddLog("別のファイル送信が完了するまでお待ちください。", LogLevel.Error);
                return;
            }

            AttachFileButton.IsEnabled = false;
            if (!StartBackgroundOperation(SendPickedFileAndReleaseAsync, "ファイル送信"))
            {
                _outgoingFileSendGate.Release();
                UpdateSendButtonState();
            }
        }

        private async Task SendPickedFileAndReleaseAsync()
        {
            try
            {
                await SendPickedFileAsync();
            }
            finally
            {
                _outgoingFileSendGate.Release();
                UpdateSendButtonState();
            }
        }

        private async Task SendPickedFileAsync()
        {
            string preparedSnapshotPath = "";
            bool retainPreparedSnapshot = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");

                IntPtr windowHandle = WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

                var file = await picker.PickSingleFileAsync()
                    .AsTask(_windowLifetimeCancellation.Token);
                if (file == null)
                {
                    return;
                }

                Windows.Storage.FileProperties.BasicProperties properties =
                    await file.GetBasicPropertiesAsync()
                        .AsTask(_windowLifetimeCancellation.Token);
                if (properties.Size > FileTransferService.MaxFileSizeBytes)
                {
                    AddLog("送信できるファイルは50 MiB以下です。", LogLevel.Error);
                    return;
                }

                if (!TryGetFileSendTarget(
                        out bool isGroup,
                        out string conversationId,
                        out PeerInfo? peer,
                        out IReadOnlyList<ChatConnection> recipients,
                        out string errorMessage))
                {
                    AddLog(errorMessage, LogLevel.Error);
                    return;
                }

                preparedSnapshotPath = await PreparePickedFileForSendAsync(
                    file,
                    properties.Size,
                    _windowLifetimeCancellation.Token);
                AddLog($"ファイル送信準備完了: {file.Name} -> {preparedSnapshotPath}");

                FileTransferSendResult sendResult = await _fileTransferService.SendFileConfirmedAsync(
                    preparedSnapshotPath,
                    LocalPeerId,
                    Environment.MachineName,
                    GetLocalShortSessionId(),
                    isGroup,
                    conversationId,
                    (message, cancellationToken) => SendFileMessageToRecipientsAsync(
                        message,
                        recipients,
                        cancellationToken),
                    (fileId, cancellationToken) => WaitForFileAcknowledgementsAsync(
                        fileId,
                        recipients,
                        cancellationToken),
                    _windowLifetimeCancellation.Token);
                retainPreparedSnapshot = true;

                var historyMessage = new ChatMessage
                {
                    Type = "chat",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = $"[ファイル] {sendResult.FileName}",
                    IsGroup = isGroup,
                    ConversationId = conversationId
                };

                AddFileChatMessage(
                    $"[ファイル] {sendResult.FileName}",
                    sendResult.FileName,
                    preparedSnapshotPath,
                    conversationId,
                    isMine: true,
                    senderName: Environment.MachineName,
                    messageId: historyMessage.MessageId);

                SaveChatMessageSafely(
                    historyMessage,
                    true,
                    peer,
                    isGroup ? null : recipients[0],
                    new ChatHistoryAttachment
                    {
                        FileId = sendResult.FileId,
                        FileName = sendResult.FileName,
                        LocalFilePath = preparedSnapshotPath,
                        FileSize = sendResult.FileSize
                    });
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog($"ファイル送信に失敗しました: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                if (!retainPreparedSnapshot && !string.IsNullOrWhiteSpace(preparedSnapshotPath))
                {
                    TryDeletePreparedOutgoingSnapshot(preparedSnapshotPath);
                }
            }
        }

        private bool TryGetFileSendTarget(
            out bool isGroup,
            out string conversationId,
            out PeerInfo? peer,
            out IReadOnlyList<ChatConnection> recipients,
            out string errorMessage)
        {
            isGroup = IsGroupChatSelected();
            conversationId = isGroup ? PeerIdentityService.DefaultGroupConversationId : "";
            peer = null;
            recipients = Array.Empty<ChatConnection>();
            errorMessage = "";

            if (Volatile.Read(ref _fileStorageReady) == 0)
            {
                errorMessage = "ファイル保存先の初期化が完了していません。";
                return false;
            }

            if (isGroup)
            {
                List<ChatConnection> groupRecipients = SnapshotReadyFileRecipients();
                if (groupRecipients.Count == 0)
                {
                    errorMessage = "グループ送信できる接続がありません。";
                    return false;
                }

                recipients = groupRecipients;
                return true;
            }

            if (PeerList.SelectedItem is not PeerInfo selectedPeer ||
                !PeerConnectionStateService.IsChatReady(selectedPeer))
            {
                errorMessage = "ファイル送信先のPeerが準備できていません。";
                return false;
            }

            ChatConnection? selectedConnection = GetConnectionForPeer(selectedPeer);
            if (selectedConnection == null || !selectedConnection.IsConnected || !selectedConnection.IsReady)
            {
                errorMessage = "ファイル送信先の接続が準備できていません。";
                return false;
            }

            peer = selectedPeer;
            if (string.IsNullOrWhiteSpace(GetStableRemotePeerId(selectedConnection)))
            {
                errorMessage = "ファイル送信先の安定したPeer IDを確認できません。";
                return false;
            }

            recipients = new[] { selectedConnection };
            conversationId = PeerIdentityService.GetConnectionId(selectedPeer);
            return true;
        }

        private static async Task SendFileMessageToRecipientsAsync(
            ChatMessage message,
            IReadOnlyList<ChatConnection> recipients,
            CancellationToken cancellationToken)
        {
            if (recipients.Count == 0)
            {
                throw new InvalidOperationException("There are no file transfer recipients.");
            }

            await Task.WhenAll(recipients.Select(connection =>
                connection.SendAsync(message, cancellationToken)));
        }

        private static async Task<string> PreparePickedFileForSendAsync(
            Windows.Storage.StorageFile file,
            ulong declaredSize,
            CancellationToken cancellationToken)
        {
            // Always send an application-owned snapshot. Sending the picker path
            // directly lets another process change the file between the size check
            // and chunk reads, and leaves the history entry pointing at a source file
            // the user may subsequently move or delete. Resolve the same cache path
            // as FileTransferService so unpackaged runs do not depend on
            // ApplicationData.Current being available.
            string outgoingCacheDirectory = AppStoragePathService.ResolveOutgoingCacheDirectory();
            System.IO.Directory.CreateDirectory(outgoingCacheDirectory);
            Windows.Storage.StorageFolder folder =
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(outgoingCacheDirectory)
                    .AsTask(cancellationToken);

            string? rootPath = System.IO.Path.GetPathRoot(folder.Path);
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                const long minimumFreeSpaceAfterCopy = 32L * 1024 * 1024;
                long requiredSpace = checked((long)declaredSize + minimumFreeSpaceAfterCopy);
                if (new System.IO.DriveInfo(rootPath).AvailableFreeSpace < requiredSpace)
                {
                    throw new System.IO.IOException("送信用キャッシュの空き容量が不足しています。");
                }
            }

            Windows.Storage.StorageFile copiedFile =
                await file.CopyAsync(folder, file.Name, Windows.Storage.NameCollisionOption.GenerateUniqueName)
                    .AsTask(cancellationToken);

            Windows.Storage.FileProperties.BasicProperties copiedProperties =
                await copiedFile.GetBasicPropertiesAsync().AsTask(cancellationToken);
            if (copiedProperties.Size > FileTransferService.MaxFileSizeBytes)
            {
                try
                {
                    await copiedFile.DeleteAsync().AsTask(CancellationToken.None);
                }
                catch
                {
                    // The regular outgoing-cache maintenance pass will retry cleanup.
                }
                throw new InvalidOperationException("送信準備中にファイルが50 MiBを超えました。");
            }

            return copiedFile.Path;
        }

        private static void TryDeletePreparedOutgoingSnapshot(string snapshotPath)
        {
            try
            {
                string cacheRoot = System.IO.Path.GetFullPath(
                    AppStoragePathService.ResolveOutgoingCacheDirectory());
                string candidate = System.IO.Path.GetFullPath(snapshotPath);
                string relative = System.IO.Path.GetRelativePath(cacheRoot, candidate);
                if (System.IO.Path.IsPathFullyQualified(relative) ||
                    relative.Equals("..", StringComparison.Ordinal) ||
                    relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    return;
                }

                System.IO.File.Delete(candidate);
            }
            catch
            {
                // The bounded maintenance pass retries stale cache cleanup later.
            }
        }

        private void OpenAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessageItem item ||
                string.IsNullOrWhiteSpace(item.LocalFilePath) ||
                !System.IO.File.Exists(item.LocalFilePath))
            {
                AddLog("ファイルが見つかりません。", LogLevel.Error);
                return;
            }

            StartBackgroundOperation(async () =>
            {
                var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.LocalFilePath)
                    .AsTask(_windowLifetimeCancellation.Token);
                bool launched = await Windows.System.Launcher.LaunchFileAsync(storageFile)
                    .AsTask(_windowLifetimeCancellation.Token);
                if (!launched)
                {
                    AddLog("このファイルを開ける関連付けアプリがありません。", LogLevel.Error);
                }
            }, "添付ファイルを開く");
        }

        private void SaveAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessageItem item ||
                string.IsNullOrWhiteSpace(item.LocalFilePath) ||
                !System.IO.File.Exists(item.LocalFilePath))
            {
                AddLog("保存するファイルが見つかりません。", LogLevel.Error);
                return;
            }

            StartBackgroundOperation(async () =>
            {
                string savedPath = await _fileTransferService.SaveToDownloadsAsync(
                    item.LocalFilePath,
                    item.FileName,
                    _windowLifetimeCancellation.Token);
                AddLog($"ファイルをDownloadsに保存しました: {savedPath}", LogLevel.Success);
            }, "添付ファイルの保存");
        }
    }
}
