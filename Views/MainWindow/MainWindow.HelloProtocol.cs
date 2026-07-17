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
        private async System.Threading.Tasks.Task SendHelloAsync(ChatConnection connection)
        {
            try
            {
                var hello = new ChatMessage
                {
                    Type = "hello",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = ""
                };

                AddLog("TCP HELLO送信");
                await connection.SendAsync(hello);
            }
            catch (Exception ex)
            {
                AddLog("TCP HELLO送信失敗", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private void StartHelloTimeout(ChatConnection connection)
        {
            RunSafelyInBackground(
                () => EnforceHelloTimeoutAsync(connection),
                "HELLOタイムアウト監視");
        }

        private async System.Threading.Tasks.Task EnforceHelloTimeoutAsync(ChatConnection connection)
        {
            await System.Threading.Tasks.Task.Delay(HelloTimeout);
            if (!connection.IsConnected || connection.IsHelloVerified)
            {
                return;
            }

            EnqueueLog(
                $"HELLOタイムアウトにより接続を切断します: Peer={GetConnectionPeerName(connection)}",
                LogLevel.Error);
            connection.Close();
        }

        private async System.Threading.Tasks.Task HandleHelloMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            string shortSessionId = message.ShortSessionId ?? "";
            string originalPeerId = sourceConnection.PeerId;
            string originalShortSessionId = sourceConnection.ShortSessionId;
            PeerInfo? provisionalPeer = _peerRegistryService.FindProvisionalForConnection(
                sourceConnection.RemoteIpAddress,
                originalPeerId,
                originalShortSessionId);
            AddLog("SelectedItemには依存せずHELLO判定します", LogLevel.Debug);
            AddLog($"TCP HELLO受信: shortSessionId={shortSessionId}");

            if (!string.IsNullOrWhiteSpace(message.SenderName))
            {
                sourceConnection.PeerName = message.SenderName;
            }

            if (!string.IsNullOrWhiteSpace(message.SenderId))
            {
                sourceConnection.PeerId = message.SenderId;
            }

            if (!string.IsNullOrWhiteSpace(shortSessionId))
            {
                sourceConnection.ShortSessionId = shortSessionId;
            }

            AddLog($"PeerごとのHELLO確認開始: {message.SenderName} / {sourceConnection.RemoteIpAddress}");

            if (provisionalPeer != null && !IsHelloIdentityConfirmed(provisionalPeer, message))
            {
                PeerMergeService.RejectProvisional(provisionalPeer, "HELLO識別情報不一致");
                provisionalPeer.StatusText = "HELLO不一致";
                sourceConnection.IsHelloVerified = false;
                sourceConnection.IsReady = false;
                RefreshPeerDisplay(provisionalPeer);
                AddLog("HELLO不一致のため仮紐付け解除", LogLevel.Error);
                UpdateSendButtonState();
                sourceConnection.Close();
                return;
            }

            PeerInfo? matchedPeer = provisionalPeer ?? FindPeerForHello(message, sourceConnection);
            if (matchedPeer == null)
            {
                AddLog("事前に発見・許可されていないPeerからのHELLOを拒否します", LogLevel.Error);
                sourceConnection.IsHelloVerified = false;
                sourceConnection.IsReady = false;
                sourceConnection.Close();
                return;
            }

            if (IsHelloMismatch(matchedPeer, message))
            {
                PeerMergeService.RejectProvisional(matchedPeer, "HELLO ShortSessionId不一致");
                matchedPeer.StatusText = "HELLO不一致";
                sourceConnection.IsHelloVerified = false;
                sourceConnection.IsReady = false;
                RefreshPeerDisplay(matchedPeer);
                AddLog("PeerごとのHELLO確認失敗: ShortSessionIdまたはPeerId不一致", LogLevel.Error);
                AddLog("HELLO確認失敗: BLE Peerと接続先が不一致", LogLevel.Error);
                AddLog("HELLO不一致のため仮紐付け解除", LogLevel.Error);
                UpdateSendButtonState();
                sourceConnection.Close();
                return;
            }

            ApplyPendingIncomingWiFiDirectCandidate(matchedPeer, sourceConnection.RemoteIpAddress);
            ApplyHelloToPeer(matchedPeer, message, sourceConnection);
            SelectConnectedPeerIfNeeded(matchedPeer);
            if (_chatRole == ChatRole.Host)
            {
                await PublishParticipantListAsync();
            }
            AddLog($"HELLO確認後にPeerを正式統合: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog($"ChatConnectionとPeerInfoを紐付けました: {matchedPeer.DisplayName}");
            AddLog($"PeerごとのHELLO確認成功: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog("HELLO確認成功: BLE Peerと接続先が一致", LogLevel.Success);
            AddLog("HELLO確認後、チャット準備完了", LogLevel.Success);
            UpdateSendButtonState();
            await SendPingAfterHelloAsync(sourceConnection);
        }

        private void SelectConnectedPeerIfNeeded(PeerInfo connectedPeer)
        {
            if (_chatRole != ChatRole.Host ||
                PeerList.SelectedItem is PeerInfo selectedPeer && !selectedPeer.IsGroupChat)
            {
                return;
            }

            PeerList.SelectedItem = connectedPeer;
            PeerList.ScrollIntoView(connectedPeer);
            UpdateSelectedPeerDetails(connectedPeer);
            UpdateSendButtonState();
            AddLog($"接続された相手を自動選択しました: {connectedPeer.DisplayName}", LogLevel.Success);
        }

        private async System.Threading.Tasks.Task PublishParticipantListAsync()
        {
            var participants = new List<ChatParticipant>
            {
                new()
                {
                    PeerId = LocalPeerId,
                    PeerName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId()
                }
            };

            participants.AddRange(
                _chatConnectionManager.Connections
                    .Where(connection =>
                        connection.IsConnected &&
                        connection.IsReady &&
                        !string.IsNullOrWhiteSpace(connection.PeerId))
                    .Select(connection => new ChatParticipant
                    {
                        PeerId = connection.PeerId,
                        PeerName = connection.PeerName,
                        ShortSessionId = connection.ShortSessionId
                    }));

            var participantList = new ChatMessage
            {
                Type = "peer_list",
                SenderId = LocalPeerId,
                SenderName = Environment.MachineName,
                ShortSessionId = GetLocalShortSessionId(),
                Participants = participants
                    .GroupBy(participant => participant.PeerId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList()
            };

            await _chatConnectionManager.BroadcastAsync(participantList);
            AddLog($"参加者一覧を配信しました: {participantList.Participants.Count}人", LogLevel.Debug);
        }

        private PeerInfo? FindPeerForHello(ChatMessage message, ChatConnection sourceConnection)
        {
            return _peerRegistryService.FindForHello(message, sourceConnection);
        }

        private static bool IsHelloMismatch(PeerInfo peer, ChatMessage message)
        {
            bool shortSessionMismatch = !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                !string.IsNullOrWhiteSpace(message.ShortSessionId) &&
                !string.Equals(peer.ShortSessionId, message.ShortSessionId, StringComparison.OrdinalIgnoreCase);
            bool peerIdMismatch = !string.IsNullOrWhiteSpace(peer.PeerId) &&
                !string.IsNullOrWhiteSpace(message.SenderId) &&
                !string.Equals(peer.PeerId, message.SenderId, StringComparison.OrdinalIgnoreCase);
            return shortSessionMismatch || peerIdMismatch;
        }

        private static bool IsHelloIdentityConfirmed(PeerInfo peer, ChatMessage message)
        {
            if (IsHelloMismatch(peer, message))
            {
                return false;
            }

            bool shortSessionMatch = !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                !string.IsNullOrWhiteSpace(message.ShortSessionId) &&
                string.Equals(peer.ShortSessionId, message.ShortSessionId, StringComparison.OrdinalIgnoreCase);
            bool peerIdMatch = !string.IsNullOrWhiteSpace(peer.PeerId) &&
                !string.IsNullOrWhiteSpace(message.SenderId) &&
                string.Equals(peer.PeerId, message.SenderId, StringComparison.OrdinalIgnoreCase);
            return shortSessionMatch || peerIdMatch;
        }

        private void ApplyPendingIncomingWiFiDirectCandidate(PeerInfo peer, string remoteIpAddress)
        {
            if (string.IsNullOrWhiteSpace(remoteIpAddress) ||
                !_pendingIncomingWiFiDirectPeers.TryRemove(remoteIpAddress, out PeerInfo? candidate))
            {
                return;
            }

            peer.DiscoveredByWiFiDirect = true;
            peer.PendingWiFiDirectDeviceId = candidate.DeviceId;
            peer.PendingWiFiDirectName = candidate.WiFiDirectName;
            peer.PendingWiFiDirectDeviceKind = candidate.DeviceKind;
            peer.PendingWiFiDirectIsEnabled = candidate.IsEnabled;
            peer.IsConnected |= candidate.IsConnected;
            peer.MatchState = PeerMatchState.Provisional;
            peer.MatchReason = "ConnectionRequested候補をHELLOで確認";
            AddLog($"ConnectionRequested候補をHELLO Peerへ仮適用: Wi-Fi={candidate.WiFiDirectName}", LogLevel.Debug);
        }

        private void ApplyHelloToPeer(PeerInfo peer, ChatMessage message, ChatConnection sourceConnection)
        {
            if (!string.IsNullOrWhiteSpace(message.SenderName))
            {
                peer.DisplayName = message.SenderName;
            }

            if (!string.IsNullOrWhiteSpace(message.ShortSessionId))
            {
                peer.ShortSessionId = message.ShortSessionId;
                peer.MatchKey = message.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(message.SenderId))
            {
                peer.PeerId = message.SenderId;
            }

            peer.RemoteIpAddress = sourceConnection.RemoteIpAddress;
            PeerMergeService.ConfirmAfterHello(peer, "HELLO ShortSessionId確認成功");
            peer.IsTcpConnected = sourceConnection.IsConnected;
            peer.IsHelloVerified = true;
            peer.IsChatReady = sourceConnection.IsConnected && sourceConnection.IsReceiveLoopStarted;
            peer.StatusText = peer.IsChatReady ? "チャット準備完了" : "HELLO確認中";
            sourceConnection.IsHelloVerified = true;
            sourceConnection.IsReady = peer.IsChatReady;
            sourceConnection.ShortSessionId = peer.ShortSessionId;
            RefreshPeerDisplay(peer);
            AddLog($"PeerごとのTCP接続状態を更新: {peer.DisplayName}, Tcp={peer.IsTcpConnected}, Hello={peer.IsHelloVerified}, Ready={peer.IsChatReady}");
        }
    }
}
