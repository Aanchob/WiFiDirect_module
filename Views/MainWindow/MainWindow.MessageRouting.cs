using direct_module.Discovery;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void OnChatMessageReceived(ChatMessage message, ChatConnection sourceConnection)
        {
            RunSafelyInBackground(
                () => OnChatMessageReceivedAsync(message, sourceConnection),
                "チャットメッセージ受信処理");
        }

        private async System.Threading.Tasks.Task OnChatMessageReceivedAsync(
            ChatMessage message,
            ChatConnection sourceConnection)
        {
            string messageType = string.IsNullOrWhiteSpace(message.Type)
                ? "chat"
                : message.Type;

            switch (messageType.ToLowerInvariant())
            {
                case "hello":
                    EnqueueAsyncSafely(
                        () => HandleHelloMessageAsync(message, sourceConnection),
                        "HELLO受信処理");
                    return;

                case "chat":
                    break;

                case "peer_list":
                    EnqueueAsyncSafely(
                        () => HandleParticipantListAsync(message),
                        "参加者一覧更新");
                    return;

                case "ping":
                    EnqueueAsyncSafely(
                        () => HandlePingMessageAsync(message, sourceConnection),
                        "PING受信処理");
                    return;

                case "pong":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HandlePongMessage(message, sourceConnection);
                    });
                    return;

                case "system":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddLog($"システムメッセージを受信: {message.Body}");
                    });
                    return;

                case "file_start":
                case "file_chunk":
                case "file_end":
                    RunSafelyInBackground(
                        () => ProcessFileTransferMessageAsync(message, sourceConnection),
                        "ファイル受信処理");
                    return;

                default:
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddLog($"不明なChatMessage Typeを受信: {messageType}", LogLevel.Error);
                    });
                    return;
            }

            if (_chatRole == ChatRole.Host &&
                !message.IsGroup &&
                !IsMessageForLocalPeer(message))
            {
                await RelayDirectMessageAsync(message, sourceConnection);
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                PeerInfo? messagePeer = !string.IsNullOrWhiteSpace(message.SenderId)
                    ? FindPeerByPeerId(message.SenderId)
                    : null;
                AddChatMessage(
                    $"{message.SenderName}: {message.Body}",
                    GetConversationIdForMessage(message, sourceConnection));
                SaveChatMessageSafely(
                    message,
                    false,
                    messagePeer ?? FindPeerForConnection(sourceConnection),
                    sourceConnection);
                AddLog($"TCP受信メッセージ: {message.Body}", LogLevel.Success);
                AddConnectedPeerDisplay(sourceConnection);
            });

            if (_chatRole == ChatRole.Host && message.IsGroup)
            {
                try
                {
                    EnqueueLog($"Host転送開始: From={message.SenderName}, MessageId={message.MessageId}");
                    await _chatConnectionManager.BroadcastExceptAsync(message, sourceConnection);
                    EnqueueLog("Host転送完了");
                }
                catch (Exception ex)
                {
                    EnqueueLog($"Host転送失敗: MessageId={message.MessageId}, {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                }
            }
        }

        private bool IsMessageForLocalPeer(ChatMessage message)
        {
            return string.IsNullOrWhiteSpace(message.ReceiverId) ||
                   string.Equals(message.ReceiverId, LocalPeerId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message.ReceiverId, GetLocalShortSessionId(), StringComparison.OrdinalIgnoreCase);
        }

        private async System.Threading.Tasks.Task RelayDirectMessageAsync(
            ChatMessage message,
            ChatConnection sourceConnection)
        {
            ChatConnection? target = _chatConnectionManager.FindByIdentity(message.ReceiverId);
            if (target == null || !target.IsConnected || !target.IsReady || ReferenceEquals(target, sourceConnection))
            {
                EnqueueLog(
                    $"個別メッセージの中継先が見つかりません: Receiver={message.ReceiverName}/{message.ReceiverId}",
                    LogLevel.Error);
                return;
            }

            await target.SendAsync(message);
            EnqueueLog(
                $"個別メッセージをHost経由で中継しました: From={message.SenderName}, To={message.ReceiverName}",
                LogLevel.Debug);
        }

        private System.Threading.Tasks.Task HandleParticipantListAsync(ChatMessage message)
        {
            if (_chatRole == ChatRole.Host || message.Participants == null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var activePeerIds = message.Participants
                .Where(participant => !string.IsNullOrWhiteSpace(participant.PeerId))
                .Select(participant => participant.PeerId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (PeerInfo removedPeer in _peerRegistryService.RemoveRelayPeersExcept(activePeerIds))
            {
                bool wasSelected = ReferenceEquals(PeerList.SelectedItem, removedPeer);
                PeerList.Items.Remove(removedPeer);
                if (wasSelected)
                {
                    PeerList.SelectedItem = PeerList.Items
                        .Cast<PeerInfo>()
                        .FirstOrDefault(peer => peer.IsGroupChat);
                }
            }

            foreach (ChatParticipant participant in message.Participants)
            {
                if (string.IsNullOrWhiteSpace(participant.PeerId) ||
                    string.Equals(participant.PeerId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ChatConnection? directConnection = _chatConnectionManager.FindByIdentity(participant.PeerId);
                PeerInfo? existing = FindPeerByPeerId(participant.PeerId);
                if (directConnection != null)
                {
                    if (existing != null && !string.IsNullOrWhiteSpace(participant.PeerName))
                    {
                        existing.DisplayName = participant.PeerName;
                        RefreshPeerDisplay(existing);
                    }

                    continue;
                }

                var relayPeer = new PeerInfo
                {
                    DisplayName = string.IsNullOrWhiteSpace(participant.PeerName)
                        ? participant.PeerId
                        : participant.PeerName,
                    PeerId = participant.PeerId,
                    ShortSessionId = participant.ShortSessionId,
                    MatchKey = participant.ShortSessionId,
                    IsRelayPeer = true,
                    IsConnected = true,
                    IsTcpConnected = true,
                    IsHelloVerified = true,
                    IsChatReady = true,
                    MatchState = PeerMatchState.Confirmed,
                    MatchScore = 100,
                    MatchReason = "Host参加者一覧",
                    StatusText = "Host経由で個別チャット可能"
                };
                PeerInfo registeredPeer = AddOrMergePeer(relayPeer);
                registeredPeer.IsRelayPeer = true;
                registeredPeer.IsConnected = true;
                registeredPeer.IsTcpConnected = true;
                registeredPeer.IsHelloVerified = true;
                registeredPeer.IsChatReady = true;
                registeredPeer.StatusText = "Host経由で個別チャット可能";
                RefreshPeerDisplay(registeredPeer);
            }

            UpdatePeerCount();
            UpdateSendButtonState();
            AddLog("Hostから参加者一覧を受信しました", LogLevel.Success);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
