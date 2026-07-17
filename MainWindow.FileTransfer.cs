using System;
using System.Linq;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");

                IntPtr windowHandle = WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                if (!TryGetFileSendTarget(out bool isGroup, out string conversationId, out PeerInfo? peer, out ChatConnection? connection, out string errorMessage))
                {
                    AddLog(errorMessage, LogLevel.Error);
                    return;
                }

                string sendFilePath = await PreparePickedFileForSendAsync(file);
                AddLog($"ファイル送信準備完了: {file.Name} -> {sendFilePath}");
                AddFileChatMessage(
                    $"自分: [ファイル] {file.Name}",
                    file.Name,
                    sendFilePath,
                    conversationId);

                await _fileTransferService.SendFileAsync(
                    sendFilePath,
                    LocalPeerId,
                    Environment.MachineName,
                    GetLocalShortSessionId(),
                    isGroup,
                    conversationId,
                    message => SendNetworkMessageAsync(message, isGroup, connection));

                var historyMessage = new ChatMessage
                {
                    Type = "chat",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = $"[ファイル] {file.Name}",
                    IsGroup = isGroup,
                    ConversationId = conversationId
                };

                SaveChatMessageSafely(historyMessage, true, peer, connection);
            }
            catch (Exception ex)
            {
                AddLog($"ファイル送信に失敗しました: {ex.Message}", LogLevel.Error);
            }
        }

        private bool TryGetFileSendTarget(
            out bool isGroup,
            out string conversationId,
            out PeerInfo? peer,
            out ChatConnection? connection,
            out string errorMessage)
        {
            isGroup = IsGroupChatSelected();
            conversationId = isGroup ? "group" : "";
            peer = null;
            connection = null;
            errorMessage = "";

            if (isGroup)
            {
                bool hasReadyConnection = _chatConnectionManager.Connections
                    .Any(item => item.IsConnected && item.IsReady);
                if (!hasReadyConnection)
                {
                    errorMessage = "グループ送信できる接続がありません。";
                    return false;
                }

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
            connection = selectedConnection;
            conversationId = PeerIdentityService.GetConnectionId(selectedPeer);
            return true;
        }

        private static async System.Threading.Tasks.Task<string> PreparePickedFileForSendAsync(Windows.Storage.StorageFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Path) && System.IO.File.Exists(file.Path))
            {
                return file.Path;
            }

            Windows.Storage.StorageFolder folder =
                await Windows.Storage.ApplicationData.Current.LocalCacheFolder.CreateFolderAsync(
                    "outgoing",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);

            Windows.Storage.StorageFile copiedFile =
                await file.CopyAsync(folder, file.Name, Windows.Storage.NameCollisionOption.GenerateUniqueName);

            return copiedFile.Path;
        }

        private async void OpenAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessageItem item ||
                string.IsNullOrWhiteSpace(item.LocalFilePath) ||
                !System.IO.File.Exists(item.LocalFilePath))
            {
                AddLog("ファイルが見つかりません。", LogLevel.Error);
                return;
            }

            try
            {
                var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.LocalFilePath);
                await Windows.System.Launcher.LaunchFileAsync(storageFile);
            }
            catch (Exception ex)
            {
                AddLog($"ファイルを開けませんでした: {ex.Message}", LogLevel.Error);
            }
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

            try
            {
                string savedPath = _fileTransferService.SaveToDownloads(item.LocalFilePath, item.FileName);
                AddLog($"ファイルをDownloadsに保存しました: {savedPath}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                AddLog($"ファイルを保存できませんでした: {ex.Message}", LogLevel.Error);
            }
        }
    }
}
