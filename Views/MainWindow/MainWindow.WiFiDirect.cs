using direct_module.Discovery;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _chatRole = ChatRole.Host;
            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            AddLog("Wi-Fi Direct広告+待ち受け開始ボタンを押しました");
            AddLog("Chat Role: Host");
            RunSafelyInBackground(
                () => EnsureTcpServerStartedAsync("Wi-Fi Direct広告+待ち受け開始"),
                "Wi-Fi Direct広告+待ち受け開始");
        }

        private void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください", LogLevel.Error);
                return;
            }

            RunSafelyInBackground(() => ConnectPeerAsync(peer), "選択Peer接続");
        }

        private void ConnectPeerItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PeerInfo peer)
            {
                AddLog("接続対象Peerを取得できませんでした", LogLevel.Error);
                return;
            }

            PeerList.SelectedItem = peer;
            RunSafelyInBackground(() => ConnectPeerAsync(peer), "Peer接続");
        }

        private async System.Threading.Tasks.Task ConnectPeerAsync(PeerInfo peer)
        {
            if (peer.IsConnectingWiFiDirect)
            {
                AddLog($"Wi-Fi Direct接続処理中のため重複要求を無視します: Peer={peer.DisplayName}", LogLevel.Debug);
                return;
            }

            _peerConnectionStateService.UpdateConnectAvailability(peer);
            if (!peer.CanConnect)
            {
                AddLog($"接続条件を満たしていないため接続を開始しません: Peer={peer.DisplayName}", LogLevel.Error);
                AddLog("接続にはBLEとWi-Fi Directの両方、ShortSessionId、RoleKey、Client側判定が必要です", LogLevel.Error);
                RefreshPeerDisplay(peer);
                return;
            }

            string? connectionDeviceId = peer.WiFiDirectDeviceIdForConnection;
            if (string.IsNullOrWhiteSpace(connectionDeviceId))
            {
                AddLog("選択中PeerにWi-Fi Direct DeviceIdがありません", LogLevel.Error);
                return;
            }

            if (connectionDeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                AddLog("_PendingRequest付きDeviceIdのため通常接続を中止します", LogLevel.Error);
                return;
            }

            if (ConnectionRoleService.HasRoleKey(peer) &&
                !_connectionRoleService.IsLocalClientForWifiDirect(peer))
            {
                AddLog($"BLE RoleKey判定では自分がGOのため、手動Wi-Fi Direct接続を開始しません: Peer={peer.DisplayName}");
                AddLog("相手ClientからのJoinを待ち受けます");
                return;
            }

            bool connectAttempted = false;

            try
            {
                _chatRole = ChatRole.Client;
                peer.IsConnectingWiFiDirect = true;
                peer.StatusText = "Wi-Fi Direct接続準備中";
                RefreshPeerDisplay(peer);

                AddLog("Chat Role: Client");

                if (!await RefreshWiFiDirectCandidateBeforeConnectAsync(peer))
                {
                    peer.StatusText = "Wi-Fi Direct再探索失敗";
                    AddLog($"Wi-Fi Direct候補を再取得できなかったため接続を中止します: Peer={peer.DisplayName}", LogLevel.Error);
                    return;
                }

                peer.StatusText = "Wi-Fi Direct接続中";
                RefreshPeerDisplay(peer);

                AddLog($"Wi-Fi Direct接続開始: {peer.DisplayText}");
                _manager.StopAdvertisement();
                _manager.StopScan();
                await System.Threading.Tasks.Task.Delay(WiFiDirectScanRestartDelay);

                connectAttempted = true;
                await _manager.ConnectAsync(peer);
            }
            catch (Exception ex)
            {
                peer.StatusText = "Wi-Fi Direct接続失敗";
                AddLog($"Wi-Fi Direct接続失敗: Peer={peer.DisplayName}, {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                peer.IsConnectingWiFiDirect = false;

                if (peer.IsConnected)
                {
                    if (IsTransientWiFiDirectStatus(peer.StatusText))
                    {
                        peer.StatusText = "";
                    }
                }
                else if (connectAttempted)
                {
                    peer.StatusText = "Wi-Fi Direct接続失敗";
                }

                _peerConnectionStateService.UpdateConnectAvailability(peer);
                RefreshPeerDisplay(peer);
            }
        }

        private async System.Threading.Tasks.Task<bool> RefreshWiFiDirectCandidateBeforeConnectAsync(PeerInfo peer)
        {
            AddLog($"接続前にWi-Fi Direct候補を再探索します: Peer={peer.DisplayName}");

            if (HasUsableWiFiDirectCandidate(peer))
            {
                AddLog($"Wi-Fi Direct candidate is already available. Reusing DeviceId={peer.WiFiDirectDeviceIdForConnection}", LogLevel.Debug);
                _manager.StopScan();
                await System.Threading.Tasks.Task.Delay(WiFiDirectScanRestartDelay);
                return true;
            }

            _manager.StopScan();
            await System.Threading.Tasks.Task.Delay(WiFiDirectScanRestartDelay);

            ClearWiFiDirectCandidateForPreConnect(peer);

            AddLog($"GO再広告待機: {WiFiDirectGoAdvertisementWait.TotalSeconds:0.0}秒");
            await System.Threading.Tasks.Task.Delay(WiFiDirectGoAdvertisementWait);

            AddLog("接続前Wi-Fi Direct再スキャン開始");
            await _manager.StartAssociationEndpointScanAsync();

            if (await WaitForWiFiDirectCandidateAsync(peer, WiFiDirectCandidateRefreshTimeout))
            {
                AddLog($"Wi-Fi Direct候補再取得: Peer={peer.DisplayName}, DeviceId={peer.WiFiDirectDeviceIdForConnection}", LogLevel.Success);
                _manager.StopScan();
                await System.Threading.Tasks.Task.Delay(WiFiDirectScanRestartDelay);
                return true;
            }

            return false;
        }

        private void ClearWiFiDirectCandidateForPreConnect(PeerInfo peer)
        {
            peer.DiscoveredByWiFiDirect = false;
            peer.WiFiDirectName = "";
            peer.DeviceId = "";
            peer.PendingWiFiDirectDeviceId = "";
            peer.PendingWiFiDirectName = "";
            peer.PendingWiFiDirectDeviceKind = "";
            peer.PendingWiFiDirectIsEnabled = null;
            peer.MatchState = PeerMatchState.Unmatched;
            peer.MatchScore = 0;
            peer.MatchReason = "";
            peer.DeviceKind = "";
            peer.IsEnabled = null;
            peer.IsConnected = false;
            peer.RemoteIpAddress = "";
            peer.StatusText = "Wi-Fi Direct再探索中";
            RefreshPeerDisplay(peer);
        }

        private async System.Threading.Tasks.Task<bool> WaitForWiFiDirectCandidateAsync(PeerInfo peer, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < timeout)
            {
                if (HasUsableWiFiDirectCandidate(peer))
                {
                    return true;
                }

                await System.Threading.Tasks.Task.Delay(WiFiDirectCandidatePollInterval);
            }

            return HasUsableWiFiDirectCandidate(peer);
        }

        private static bool HasUsableWiFiDirectCandidate(PeerInfo peer)
        {
            return peer.DiscoveredByWiFiDirect &&
                   !string.IsNullOrWhiteSpace(peer.WiFiDirectDeviceIdForConnection) &&
                   !peer.WiFiDirectDeviceIdForConnection.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransientWiFiDirectStatus(string statusText)
        {
            return string.Equals(statusText, "Wi-Fi Direct接続準備中", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(statusText, "Wi-Fi Direct再探索中", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(statusText, "Wi-Fi Direct接続中", StringComparison.OrdinalIgnoreCase);
        }

        private void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("再接続対象Peerが選択されていません", LogLevel.Error);
                return;
            }

            RunSafelyInBackground(() => ReconnectPeerAsync(peer), "Peer再接続");
        }

        private void OnConnectionRequested(PeerInfo peer)
        {
            EnqueueAsyncSafely(async () =>
            {
                _chatRole = ChatRole.Host;
                AddLog($"接続要求: {peer.DisplayName}");
                AddLog("Chat Role: Host");
                await EnsureTcpServerStartedAsync("Wi-Fi Direct接続要求受信");
            }, "Wi-Fi Direct接続要求処理");
        }

        private void OnWiFiDirectConnected(PeerInfo peer)
        {
            bool isIncomingRequest = peer.IsIncomingConnectionRequest ||
                (!string.IsNullOrWhiteSpace(peer.DeviceId) &&
                 peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase));
            if (isIncomingRequest && !string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                _pendingIncomingWiFiDirectPeers[peer.RemoteIpAddress] = peer;
            }

            EnqueueAsyncSafely(async () =>
            {
                peer.IsConnectingWiFiDirect = false;
                if (IsTransientWiFiDirectStatus(peer.StatusText))
                {
                    peer.StatusText = "";
                }

                AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}", LogLevel.Success);
                if (isIncomingRequest)
                {
                    peer.MatchState = PeerMatchState.Provisional;
                    peer.MatchScore = 0;
                    peer.MatchReason = "ConnectionRequested受信。HELLO確認待ち";
                    AddLog("GO側ConnectionRequested候補はBLE Peerへ自動統合せずHELLO確認を待ちます", LogLevel.Debug);
                }
                else
                {
                    AddOrMergePeer(peer);
                }
                await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");

                PeerInfo effectivePeer = FindPeerForTcpRoleDecision(peer) ?? peer;
                if (ShouldStartTcpConnection(effectivePeer))
                {
                    _chatRole = ChatRole.Client;
                    AddLog($"ShortSessionId判定によりTCP接続側になります: Local={GetLocalShortSessionId()}, Remote={effectivePeer.ShortSessionId}");
                    await PrepareChatTcpConnectionAsync(effectivePeer);
                }
                else
                {
                    _chatRole = ChatRole.Host;
                    AddLog($"ShortSessionId判定によりTCP待ち受け側になります: Local={GetLocalShortSessionId()}, Remote={effectivePeer.ShortSessionId}");
                    AddLog("Hostモードのため、ClientからのTCP接続を待ち受けます");
                }
            }, "Wi-Fi Direct接続完了処理");
        }

        private PeerInfo? FindPeerForTcpRoleDecision(PeerInfo connectedPeer)
        {
            if (!string.IsNullOrWhiteSpace(connectedPeer.ShortSessionId))
            {
                return connectedPeer;
            }

            return FindPeerByRemoteIpOrName(connectedPeer.RemoteIpAddress, "")
                ?? FindPeerByPeerId(PeerIdentityService.GetConnectionId(connectedPeer));
        }

        private bool ShouldStartTcpConnection(PeerInfo peer)
        {
            TcpRoleDecision decision = _connectionRoleService.DecideTcpRole(
                peer,
                _chatRole == ChatRole.Client);

            if (decision.Source == TcpRoleDecisionSource.RoleKey)
            {
                AddLog($"RoleKey判定によりTCPロール決定: LocalRole={decision.LocalRoleText}, RemoteRoleKey={decision.RemoteRoleKey}", LogLevel.Debug);
                return decision.ShouldStartConnection;
            }

            if (decision.Source == TcpRoleDecisionSource.EqualShortSessionIdFallback)
            {
                AddLog("ShortSessionIdが同一のため現在のChatRoleにフォールバックします", LogLevel.Error);
                return decision.ShouldStartConnection;
            }

            if (decision.Source == TcpRoleDecisionSource.MissingShortSessionIdFallback)
            {
                AddLog("ShortSessionId不足のため現在のChatRoleにフォールバックします", LogLevel.Debug);
            }

            return decision.ShouldStartConnection;
        }

        private async System.Threading.Tasks.Task ReconnectPeerAsync(PeerInfo peer)
        {
            if (peer == null)
            {
                return;
            }

            if (peer.IsChatReady)
            {
                AddLog($"すでにチャット準備完了のため再接続不要: Peer={peer.DisplayName}", LogLevel.Debug);
                UpdateReconnectButtonState();
                return;
            }

            if (peer.IsPreparingChatTcp || string.Equals(peer.StatusText, "再接続中", StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"すでに再接続処理中のためスキップ: Peer={peer.DisplayName}", LogLevel.Debug);
                UpdateReconnectButtonState();
                return;
            }

            AddLog($"再接続開始: Peer={peer.DisplayName}");
            peer.StatusText = "再接続中";
            RefreshPeerDisplay(peer);
            UpdateSendButtonState();
            UpdateReconnectButtonState();

            try
            {
                await EnsureTcpServerStartedAsync("手動再接続");

                if (!string.IsNullOrWhiteSpace(peer.WiFiDirectDeviceIdForConnection) &&
                    !peer.WiFiDirectDeviceIdForConnection.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"再接続中: Peer={peer.DisplayName}");
                    AddLog($"再接続処理を開始しました: Peer={peer.DisplayName}");
                    peer.IsConnected = false;
                    peer.IsTcpConnected = false;
                    peer.IsHelloVerified = false;
                    peer.IsChatReady = false;
                    await _manager.ConnectAsync(peer);
                    RefreshPeerDisplay(peer);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    AddLog($"再接続中: Peer={peer.DisplayName}");
                    AddLog($"再接続処理を開始しました: Peer={peer.DisplayName}");

                    PeerInfo effectivePeer = FindPeerForTcpRoleDecision(peer) ?? peer;
                    if (ShouldStartTcpConnection(effectivePeer))
                    {
                        AddLog($"再接続TCPロール判定: 接続側 Local={GetLocalShortSessionId()}, Remote={effectivePeer.ShortSessionId}");
                        await PrepareChatTcpConnectionAsync(effectivePeer, "再接続中");
                    }
                    else
                    {
                        AddLog($"再接続TCPロール判定: 待ち受け側 Local={GetLocalShortSessionId()}, Remote={effectivePeer.ShortSessionId}");
                        peer.IsTcpConnected = false;
                        peer.IsHelloVerified = false;
                        peer.IsChatReady = false;
                        peer.StatusText = "TCP待ち受け中";
                        RefreshPeerDisplay(peer);
                    }

                    return;
                }

                peer.IsTcpConnected = false;
                peer.IsHelloVerified = false;
                peer.IsChatReady = false;
                peer.StatusText = "再接続失敗";
                RefreshPeerDisplay(peer);
                AddLog($"再接続失敗: Peer={peer.DisplayName}", LogLevel.Error);
                AddLog("再接続に必要なRemoteIpAddressまたはWi-Fi Direct DeviceIdがありません", LogLevel.Error);
            }
            catch (Exception ex)
            {
                peer.IsTcpConnected = false;
                peer.IsHelloVerified = false;
                peer.IsChatReady = false;
                peer.IsPreparingChatTcp = false;
                peer.StatusText = "再接続失敗";
                RefreshPeerDisplay(peer);

                AddLog($"再接続失敗: Peer={peer.DisplayName}", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                UpdateSendButtonState();
                UpdateReconnectButtonState();
            }
        }
    }
}
