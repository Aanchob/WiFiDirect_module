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
        private void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            AddLog("相手探索開始");
            AddLog($"Local ShortSessionId: {GetLocalShortSessionId()}");
            AddLog($"Local RoleKey: {GetLocalRoleKey()}");
            _connectionRoleService.ResetBleNegotiation();
            _activeBleRolePeerKey = "";
            _activeBleRoleIsGo = null;
            _isClientWiFiDirectScanScheduled = false;
            Interlocked.Increment(ref _bleRoleGeneration);
            _pendingIncomingWiFiDirectPeers.Clear();

            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            StartBleAdvertiseCore();
            _discoveryManager.StartScan();

            ClearStaleWiFiDirectPeers();
            AddLog("BLE RoleKey判定後にClient側だけWi-Fi Direct探索を開始します");

            AddLog("相手探索処理を開始しました");
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("AssociationEndpoint探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            RunSafelyInBackground(
                () => _manager.StartAssociationEndpointScanAsync(),
                "AssociationEndpoint探索");
        }

        private void SearchDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("通常Wi-Fi Direct探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            RunSafelyInBackground(
                () => _manager.StartDefaultScanAsync(),
                "通常Wi-Fi Direct探索");
        }

        private void StartBleAdvertise_Click(object sender, RoutedEventArgs e)
        {
            StartBleAdvertiseCore();
        }

        private void StartBleAdvertiseCore()
        {
            string localIp = LocalNetworkInfo.GetLocalIpv4Address();

            _discoveryManager.StartAdvertise(
                Environment.MachineName,
                _localSessionId,
                LocalTcpPort);

            AddLog($"Local IP: {localIp}");
            AddLog($"Local SessionId: {_localSessionId}");
            AddLog($"Local ShortSessionId: {GetLocalShortSessionId()}");
            AddLog($"Local TCP Port: {LocalTcpPort}");
        }

        private void StartBleScan_Click(object sender, RoutedEventArgs e)
        {
            _discoveryManager.StartScan();
        }

        private void OnPeerFound(PeerInfo peer)
        {
            EnqueueAsyncSafely(async () =>
            {
                PeerInfo effectivePeer = AddOrMergePeer(peer);

                if (effectivePeer.DiscoveredByBle)
                {
                    await HandleBleRoleNegotiationAsync(effectivePeer);
                }
            }, "Peer検出処理");
        }

        private async System.Threading.Tasks.Task HandleBleRoleNegotiationAsync(PeerInfo peer)
        {
            int generation = Volatile.Read(ref _bleRoleGeneration);
            string peerKey = PeerIdentityService.GetConnectionId(peer);
            BleRoleNegotiationResult decision = _connectionRoleService.DecideBleRole(peer, peerKey);

            switch (decision.Status)
            {
                case BleRoleNegotiationStatus.MissingRemoteRoleKey:
                    AddLog($"BLE RoleKeyなしのため、自動Wi-Fi Direct探索を開始しません Peer={peer.DisplayName}", LogLevel.Debug);
                    return;
                case BleRoleNegotiationStatus.AlreadyNegotiatedForOtherPeer:
                    AddLog($"BLE Role Negotiationは既に別Peerで確定済みのためスキップ: Current={decision.CurrentPeerKey}, Ignored={decision.IgnoredPeerKey}", LogLevel.Debug);
                    return;
                case BleRoleNegotiationStatus.RoleKeyCollision:
                    AddLog($"BLE RoleKey衝突のため、自動Wi-Fi Direct探索を開始しません Local={decision.LocalRoleKey}, Remote={decision.RemoteRoleKey}", LogLevel.Error);
                    return;
            }

            AddLog($"BLE Role Negotiation: LocalRoleKey={decision.LocalRoleKey}, RemoteRoleKey={decision.RemoteRoleKey}, LocalRole={(decision.LocalIsGo ? "GO" : "Client")}");

            if (decision.LocalIsGo)
            {
                if (IsSameActiveBleRole(peerKey, localIsGo: true) &&
                    _isAutonomousGoAdvertisementEnabled)
                {
                    AddLog($"BLE role handling skipped because GO role is already active. PeerKey={peerKey}", LogLevel.Debug);
                    return;
                }

                SetActiveBleRole(peerKey, localIsGo: true);

                if (!_isAutonomousGoAdvertisementEnabled)
                {
                    _manager.RestartAdvertisement(
                        Environment.MachineName,
                        GetLocalShortSessionId(),
                        autonomousGroupOwner: true);
                    _isAutonomousGoAdvertisementEnabled = true;
                    await EnsureTcpServerStartedAsync("Autonomous GO開始");
                }

                AddLog("Autonomous GO広告を開始しました。ClientからのJoinを待ちます");
                return;
            }

            AddLog("Clientロールのため、GOのAutonomous起動を待ってから探索します");
            if (IsSameActiveBleRole(peerKey, localIsGo: false) &&
                _isClientWiFiDirectScanScheduled)
            {
                AddLog($"BLE role handling skipped because client scan is already scheduled. PeerKey={peerKey}", LogLevel.Debug);
                return;
            }

            SetActiveBleRole(peerKey, localIsGo: false);
            _isClientWiFiDirectScanScheduled = true;
            _manager.StopAdvertisement();

            if (_isAutonomousGoAdvertisementEnabled)
            {
                _manager.RestartAdvertisement(
                    Environment.MachineName,
                    GetLocalShortSessionId(),
                    autonomousGroupOwner: false);
                _isAutonomousGoAdvertisementEnabled = false;
            }

            await System.Threading.Tasks.Task.Delay(1500);
            if (generation != Volatile.Read(ref _bleRoleGeneration) ||
                !IsSameActiveBleRole(peerKey, localIsGo: false))
            {
                AddLog($"古いBLEロール判定によるWi-Fi Direct探索を中止します: PeerKey={peerKey}", LogLevel.Debug);
                return;
            }

            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();
            AddLog("BLE RoleKey判定後にClient側だけWi-Fi Direct探索を開始します");
        }

        private bool IsSameActiveBleRole(string peerKey, bool localIsGo)
        {
            return _activeBleRoleIsGo == localIsGo &&
                   string.Equals(_activeBleRolePeerKey, peerKey, StringComparison.OrdinalIgnoreCase);
        }

        private void SetActiveBleRole(string peerKey, bool localIsGo)
        {
            _activeBleRolePeerKey = peerKey;
            _activeBleRoleIsGo = localIsGo;

            if (localIsGo)
            {
                _isClientWiFiDirectScanScheduled = false;
            }
        }
    }
}
