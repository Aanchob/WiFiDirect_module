using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, List<ChatMessageItem>> _chatItemsByConversation =
            new(StringComparer.OrdinalIgnoreCase);
        private int _unreadGroupMessageCount;

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
                    AddChatMessage($"自分: {body}", "group");
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

                message.ReceiverId = string.IsNullOrWhiteSpace(peer.PeerId)
                    ? PeerIdentityService.GetConnectionId(peer)
                    : peer.PeerId;
                message.ReceiverName = peer.DisplayName;
                await connection.SendAsync(message);
                AddChatMessage($"自分: {body}", PeerIdentityService.GetConnectionId(peer));
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

        private void AddChatMessage(string message, string conversationId)
        {
            AddChatMessageItem(new ChatMessageItem
            {
                ConversationId = conversationId,
                Text = message
            });
        }

        private void AddFileChatMessage(
            string message,
            string fileName,
            string localFilePath,
            string conversationId)
        {
            AddChatMessageItem(new ChatMessageItem
            {
                ConversationId = conversationId,
                Text = message,
                FileName = fileName,
                LocalFilePath = localFilePath
            });
        }

        private void AddChatMessageItem(ChatMessageItem item)
        {
            string conversationId = string.IsNullOrWhiteSpace(item.ConversationId)
                ? "unknown"
                : item.ConversationId;
            if (!_chatItemsByConversation.TryGetValue(conversationId, out List<ChatMessageItem>? items))
            {
                items = new List<ChatMessageItem>();
                _chatItemsByConversation[conversationId] = items;
            }

            items.Add(item);
            if (!string.Equals(GetSelectedConversationId(), conversationId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(conversationId, "group", StringComparison.OrdinalIgnoreCase))
                {
                    _unreadGroupMessageCount++;
                    UpdateSendButtonState();
                }

                return;
            }

            MessageList.Items.Add(item);
            MessageList.ScrollIntoView(item);
        }

        private string GetSelectedConversationId()
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                return "";
            }

            return peer.IsGroupChat ? "group" : PeerIdentityService.GetConnectionId(peer);
        }

        private string GetConversationIdForMessage(
            ChatMessage message,
            ChatConnection connection)
        {
            if (message.IsGroup)
            {
                return "group";
            }

            PeerInfo? peer = FindPeerForConnection(connection);
            if (!string.IsNullOrWhiteSpace(message.SenderId))
            {
                peer = FindPeerByPeerId(message.SenderId) ?? peer;
            }
            if (peer != null)
            {
                return PeerIdentityService.GetConnectionId(peer);
            }

            if (!string.IsNullOrWhiteSpace(connection.ShortSessionId))
            {
                return connection.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(connection.PeerId))
            {
                return connection.PeerId;
            }

            return connection.RemoteIpAddress;
        }

        private void RefreshVisibleConversation()
        {
            MessageList.Items.Clear();
            string conversationId = GetSelectedConversationId();
            if (string.Equals(conversationId, "group", StringComparison.OrdinalIgnoreCase))
            {
                _unreadGroupMessageCount = 0;
            }

            if (string.IsNullOrWhiteSpace(conversationId) ||
                !_chatItemsByConversation.TryGetValue(conversationId, out List<ChatMessageItem>? items))
            {
                return;
            }

            foreach (ChatMessageItem item in items)
            {
                MessageList.Items.Add(item);
            }

            if (items.Count > 0)
            {
                MessageList.ScrollIntoView(items[^1]);
            }
        }
    }
}
