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
        private PeerInfo? FindPeerByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            return _peerRegistryService.FindByRemoteIpOrName(remoteIpAddress, displayName);
        }

        private PeerInfo? FindPeerByPeerId(string peerId)
        {
            return _peerRegistryService.FindByPeerId(peerId);
        }

        private void OnChatConnectionsChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog($"接続中Peer数: {_chatConnectionManager.ConnectedCount}");
                UpdateSendButtonState();
            });
        }

        private void OnChatConnectionDisconnected(ChatConnection connection)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(connection.RemoteIpAddress))
                {
                    _pendingIncomingWiFiDirectPeers.TryRemove(connection.RemoteIpAddress, out _);
                }

                PeerInfo? peer = FindPeerForConnection(connection);
                if (peer != null)
                {
                    peer.IsTcpConnected = false;
                    peer.IsHelloVerified = false;
                    peer.IsChatReady = false;
                    peer.IsPreparingChatTcp = false;
                    peer.StatusText = "切断";
                    RefreshPeerDisplay(peer);
                    AddLog($"Peer状態を切断に変更: {peer.DisplayName}", LogLevel.Error);
                }

                connection.IsReady = false;
                connection.IsHelloVerified = false;
                connection.IsPreparing = false;
                connection.IsPingWaiting = false;

                UpdateSendButtonState();
                AddLog($"Peer切断: {GetConnectionPeerName(connection)}", LogLevel.Error);
                if (_chatRole == ChatRole.Host)
                {
                    RunSafelyInBackground(PublishParticipantListAsync, "参加者一覧再配信");
                }
            });
        }

        private PeerInfo? FindPeerForConnection(ChatConnection connection)
        {
            return _peerRegistryService.FindForConnection(connection);
        }

        private PeerInfo AddOrMergePeer(PeerInfo incoming)
        {
            AddLog(
                $"Peer照合開始: Name={incoming.DisplayName}, ShortSessionId={incoming.ShortSessionId}, DeviceIdあり={!string.IsNullOrWhiteSpace(incoming.DeviceId)}",
                LogLevel.Debug);
            PeerRegistrationResult registration = _peerRegistryService.Register(incoming);
            PeerInfo registeredPeer = registration.Peer;

            if (registration.Kind == PeerRegistrationKind.IgnoredPendingRequest)
            {
                AddLog($"PendingRequestはPeerListに追加しません: {incoming.DisplayName}", LogLevel.Debug);
                return registeredPeer;
            }

            if (registration.CollectionChanged)
            {
                _peerConnectionStateService.UpdateConnectAvailability(registeredPeer);
                PeerList.Items.Add(registeredPeer);
                UpdatePeerCount();
                UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);

                if (registration.PartialNameCandidateDetected)
                {
                    AddLog("名前部分一致候補ですが自動統合しません", LogLevel.Debug);
                }

                if (registration.RoleConflictDetected)
                {
                    AddLog("Role矛盾のため候補から除外しました", LogLevel.Debug);
                }

                if (registration.UnmergedCandidateCount == 1)
                {
                    AddLog("単一候補ですが自動統合しません", LogLevel.Debug);
                }

                AddLog($"確実な照合キーがないため別Peerとして保持: {incoming.DisplayName}", LogLevel.Debug);
                AddLog($"Peer追加: {registeredPeer.DisplayText}");
                return registeredPeer;
            }

            _peerConnectionStateService.UpdateConnectAvailability(registeredPeer);
            RefreshPeerDisplay(registeredPeer);
            switch (registration.Kind)
            {
                case PeerRegistrationKind.Confirmed:
                    AddLog($"強い識別子一致でPeer確定統合: Reason={registration.MatchReason}, Score={registration.MatchScore}", LogLevel.Success);
                    break;
                case PeerRegistrationKind.Provisional:
                    AddLog($"Role整合候補: BLE={registeredPeer.BleName}, Wi-Fi={registeredPeer.PendingWiFiDirectName}", LogLevel.Debug);
                    AddLog($"弱い条件のためPeerを仮紐付け: Score={registration.MatchScore}, Reason={registration.MatchReason}", LogLevel.Debug);
                    break;
            }

            return registeredPeer;
        }

        private void ClearStaleWiFiDirectPeers()
        {
            IReadOnlyList<PeerInfo> changedPeers = _peerRegistryService.RemoveStaleWiFiDirectPeers();
            foreach (PeerInfo peer in changedPeers)
            {
                if (_peerRegistryService.Peers.Contains(peer))
                {
                    RefreshPeerDisplay(peer);
                }
                else
                {
                    PeerList.Items.Remove(peer);
                }
            }

            AddLog($"古いWi-Fi Direct候補を削除: {changedPeers.Count}件");
            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
        }

        private void AddConnectedPeerDisplay(ChatConnection connection)
        {
            string displayName = string.IsNullOrWhiteSpace(connection.PeerName)
                ? connection.RemoteIpAddress
                : connection.PeerName;

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return;
            }

            PeerInfo? existing = _peerRegistryService.FindForConnection(connection);
            if (existing != null)
            {
                existing.IsTcpConnected = connection.IsConnected;
                existing.IsChatReady = existing.IsChatReady && connection.IsConnected && connection.IsReceiveLoopStarted;
                existing.StatusText = existing.IsChatReady ? "チャット準備完了" : connection.IsConnected ? "HELLO確認中" : "送信不可";
                RefreshPeerDisplay(existing);
                return;
            }

            var peer = new PeerInfo
            {
                DisplayName = displayName,
                RemoteIpAddress = connection.RemoteIpAddress,
                IsTcpConnected = connection.IsConnected,
                IsChatReady = false,
                StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可"
            };

            AddOrMergePeer(peer);
        }
    }
}
