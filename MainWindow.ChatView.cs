using System;
using System.Diagnostics;
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
        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
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

                bool isGroup = IsGroupChatSelected();
                ChatMessage message = _chatMessageFactory.CreateChat(body, isGroup);
                _chatConnectionManager.MarkMessageSeen(message.MessageId);

                if (isGroup)
                {
                    await SendNetworkMessageAsync(message, true, null);
                    AddChatMessage($"自分: {body}");
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

                ChatConnection? connection = GetSelectedPeerPreparedConnection();
                if (connection == null || !connection.IsConnected || !connection.IsReady)
                {
                    AddLog("選択中PeerのChatConnectionが見つかりません", LogLevel.Error);
                    UpdateSendButtonState();
                    return;
                }

                await connection.SendAsync(message);
                AddChatMessage($"自分: {body}");
                SaveChatMessageSafely(message, true, peer, connection);
                MessageTextBox.Text = "";
                AddLog($"SendMessage_Click完了 合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
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

        private async System.Threading.Tasks.Task SendNetworkMessageAsync(ChatMessage message, bool isGroup, ChatConnection? peerConnection)
        {
            await _chatMessageRouter.SendAsync(message, isGroup, _chatRole == ChatRole.Host, peerConnection);
        }

        private void AddChatMessage(string message)
        {
            AddChatMessageItem(new ChatMessageItem { Text = message });
        }

        private void AddFileChatMessage(string message, string fileName, string localFilePath)
        {
            AddChatMessageItem(new ChatMessageItem
            {
                Text = message,
                FileName = fileName,
                LocalFilePath = localFilePath
            });
        }

        private void AddChatMessageItem(ChatMessageItem item)
        {
            MessageList.Items.Add(item);
            MessageList.ScrollIntoView(item);
        }
    }
}
