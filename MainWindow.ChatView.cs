using System;
using System.Diagnostics;
using System.Text;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;
            StartBackgroundOperation(SendMessageAsync, "メッセージ送信");
        }

        private async System.Threading.Tasks.Task SendMessageAsync()
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            var totalWatch = Stopwatch.StartNew();
            SendMessageButton.IsEnabled = false;

            try
            {
                string body = MessageTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(body))
                {
                    AddLog("送信内容が空です");
                    return;
                }

                if (body.Length > 16 * 1024 || Encoding.UTF8.GetByteCount(body) > 64 * 1024)
                {
                    AddLog("メッセージが大きすぎます。16,384文字かつUTF-8で64 KiB以内にしてください。", LogLevel.Error);
                    return;
                }

                bool isGroup = IsGroupChatSelected();
                ChatMessage message = _chatMessageFactory.CreateChat(body, isGroup);
                _chatConnectionManager.MarkMessageSeen(message.MessageId);

                if (isGroup)
                {
                    message.ConversationId = GetConversationIdForPeer(PeerList.SelectedItem as PeerInfo);
                    if (!await SendNetworkMessageAsync(message, true, null))
                    {
                        return;
                    }
                    AddChatMessage(body, message.ConversationId, isMine: true, senderName: Environment.MachineName, messageId: message.MessageId);
                    SaveChatMessageSafely(message, true, null, null);
                    MessageTextBox.Text = "";
                    return;
                }

                if (PeerList.SelectedItem is not PeerInfo peer || !PeerConnectionStateService.IsChatReady(peer))
                {
                    AddLog("送信先Peerがチャット準備完了ではありません", LogLevel.Error);
                    UpdateSendButtonState();
                    return;
                }

                message.ConversationId = GetConversationIdForPeer(peer);

                ChatConnection? connection = GetSelectedPeerPreparedConnection();
                if (connection == null || !connection.IsConnected || !connection.IsReady)
                {
                    AddLog("選択中PeerのChatConnectionが見つかりません", LogLevel.Error);
                    UpdateSendButtonState();
                    return;
                }

                await connection.SendAsync(message, _windowLifetimeCancellation.Token);
                AddChatMessage(body, message.ConversationId, isMine: true, senderName: Environment.MachineName, messageId: message.MessageId);
                SaveChatMessageSafely(message, true, peer, connection);
                MessageTextBox.Text = "";
                AddLog($"SendMessage_Click完了 合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog("メッセージ送信エラー", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                UpdateSendButtonState();
            }
        }

        private void MessageTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            if (SendMessageButton.IsEnabled)
            {
                SendMessage_Click(SendMessageButton, new RoutedEventArgs());
            }
        }

        private bool IsGroupChatSelected()
        {
            return PeerList.SelectedItem is PeerInfo peer && peer.IsGroupChat;
        }

        private async System.Threading.Tasks.Task<bool> SendNetworkMessageAsync(
            ChatMessage message,
            bool isGroup,
            ChatConnection? peerConnection)
        {
            if (!isGroup)
            {
                if (peerConnection == null || !peerConnection.IsConnected || !peerConnection.IsReady)
                {
                    throw new InvalidOperationException("The destination connection is not ready.");
                }

                await peerConnection.SendAsync(message, _windowLifetimeCancellation.Token);
                return true;
            }

            BroadcastResult result = await _chatConnectionManager.BroadcastAsync(
                message,
                _windowLifetimeCancellation.Token);
            if (!result.AnySucceeded)
            {
                AddLog("グループメッセージをどの相手にも配信できませんでした。", LogLevel.Error);
                return false;
            }

            if (!result.AllSucceeded)
            {
                AddLog(
                    $"グループメッセージは一部の相手へ配信できませんでした: {string.Join(", ", result.FailedPeerIds)}",
                    LogLevel.Error);
            }

            return true;
        }

        private void AddChatMessage(
            string message,
            string conversationId,
            bool isMine,
            string senderName,
            string messageId)
        {
            AddChatMessageItem(new ChatMessageItem
            {
                Text = message,
                MessageId = messageId,
                ConversationId = conversationId,
                IsMine = isMine,
                SenderName = SanitizeUntrustedDisplayText(senderName)
            });
        }

        private void AddFileChatMessage(
            string message,
            string fileName,
            string localFilePath,
            string conversationId,
            bool isMine,
            string senderName,
            string messageId)
        {
            AddChatMessageItem(new ChatMessageItem
            {
                Text = message,
                MessageId = messageId,
                FileName = fileName,
                LocalFilePath = localFilePath,
                ConversationId = conversationId,
                IsMine = isMine,
                SenderName = SanitizeUntrustedDisplayText(senderName)
            });
        }

        private void AddChatMessageItem(ChatMessageItem item)
        {
            AddConversationItem(item);
        }
    }
}
