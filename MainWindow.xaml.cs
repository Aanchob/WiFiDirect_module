using direct_module.Discovery;
using direct_module.Database;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Graphics;
using Windows.Networking.Sockets;
using Windows.System;
using WinRT.Interop;

namespace direct_module
{
    public enum LogLevel
    {
        Info,
        Success,
        Debug,
        Error
    }

    public enum ChatRole
    {
        Host,
        Client
    }

    public sealed partial class MainWindow : Window
    {
        private const int LocalTcpPort = 50001;
        private const int MaxLogLines = 500;
        private static readonly TimeSpan WiFiDirectScanRestartDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan WiFiDirectGoAdvertisementWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan WiFiDirectCandidateRefreshTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WiFiDirectCandidatePollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly ChatConnectionManager _chatConnectionManager = new();
        private readonly ChatMessageRouter _chatMessageRouter;
        private readonly ChatMessageFactory _chatMessageFactory;
        private readonly List<string> _logLines = new();
        private readonly Guid _localSessionId = Guid.NewGuid();
        private readonly DatabaseService? _databaseService = null;
        private readonly ChatHistoryService? _chatHistoryService = null;
        private readonly ConnectionRoleService _connectionRoleService;
        private readonly PeerConnectionStateService _peerConnectionStateService;
        private readonly PeerRegistryService _peerRegistryService;
        private readonly FileTransferService _fileTransferService = new();
        private readonly SemaphoreSlim _fileTransferReceiveGate = new(1, 1);
        private readonly SemaphoreSlim _tcpServerStartGate = new(1, 1);
        private readonly ConcurrentDictionary<string, PeerInfo> _pendingIncomingWiFiDirectPeers =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _isAutonomousGoAdvertisementEnabled;
        private string _activeBleRolePeerKey = "";
        private bool? _activeBleRoleIsGo;
        private bool _isClientWiFiDirectScanScheduled;
        private int _bleRoleGeneration;

        private ChatRole _chatRole = ChatRole.Client;

        public MainWindow()
        {
            InitializeComponent();
            _chatMessageRouter = new ChatMessageRouter(_chatConnectionManager);
            _chatMessageFactory = new ChatMessageFactory(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            _connectionRoleService = new ConnectionRoleService(GetLocalShortSessionId(), GetLocalRoleKey());
            _peerRegistryService = new PeerRegistryService(_connectionRoleService);
            _peerConnectionStateService = new PeerConnectionStateService(_connectionRoleService);
            Title = "Hide Chat";
            ResizeWindow(1440, 920);

            try
            {
                _databaseService = new DatabaseService();
                var chatRepository = new ChatRepository(_databaseService);
                _chatHistoryService = new ChatHistoryService(chatRepository, LocalPeerId, Environment.MachineName);
                AddLog("履歴DB初期化成功", LogLevel.Success);
                AddLog($"履歴DBパス: {_databaseService.DatabasePath}");
            }
            catch (Exception ex)
            {
                AddLog($"履歴DB初期化失敗: {ex.Message}", LogLevel.Error);
            }

            _manager = new WiFiDirectManager();
            _discoveryManager = new DiscoveryManager();

            _manager.LogReceived += OnLogReceived;
            _manager.ConnectionRequested += OnConnectionRequested;
            _manager.PeerFound += OnPeerFound;
            _manager.Connected += OnWiFiDirectConnected;

            _discoveryManager.LogReceived += OnLogReceived;
            _discoveryManager.PeerFound += OnPeerFound;

            _tcpServer.LogReceived += OnLogReceived;
            _tcpServer.ConnectionAccepted += OnTcpConnectionAccepted;

            _chatConnectionManager.LogReceived += OnLogReceived;
            _chatConnectionManager.MessageReceived += OnChatMessageReceived;
            _chatConnectionManager.ConnectionDisconnected += OnChatConnectionDisconnected;
            _chatConnectionManager.ConnectionsChanged += OnChatConnectionsChanged;
            _chatConnectionManager.StartKeepAlive(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            _fileTransferService.LogReceived += OnLogReceived;
            _fileTransferService.ProgressChanged += OnFileTransferProgressChanged;
            _fileTransferService.EnsureStorageReady();
            AddLog($"添付ファイル一時保存先: {_fileTransferService.AttachmentsDirectory}");
            AddLog($"添付ファイルDownloads保存先: {_fileTransferService.DownloadsDirectory}");
            Closed += MainWindow_Closed;

            AddGroupChatPeer();
            UpdateSendButtonState();
            UpdatePeerCount();
            UpdateSelectedPeerDetails(null);
        }

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

        private void StartTcpServer_Click(object sender, RoutedEventArgs e)
        {
            _chatRole = ChatRole.Host;
            AddLog("Chat Role: Host");
            RunSafelyInBackground(
                () => EnsureTcpServerStartedAsync("手動操作"),
                "TCP待ち受け開始");
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

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogTextBox.Text = string.Empty;
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

        private void ScrollLogBottom_Click(object sender, RoutedEventArgs e)
        {
            MoveLogCaretToEnd();
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

        private void OnLogReceived(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog(message, LogClassifier.Classify(message));
            });
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

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            EnqueueAsyncSafely(async () =>
            {
                _chatRole = ChatRole.Host;

                var connection = new ChatConnection
                {
                    PeerId = $"{socket.Information.RemoteAddress?.DisplayName}:{socket.Information.RemotePort}",
                    PeerName = socket.Information.RemoteAddress?.DisplayName ?? "Client",
                    RemoteIpAddress = socket.Information.RemoteAddress?.DisplayName ?? ""
                };

                _chatConnectionManager.AddConnection(connection);
                await connection.AttachAcceptedSocketAsync(socket);

                AddLog($"Host: Client TCP接続を追加 Count={_chatConnectionManager.ConnectedCount}");
                AddConnectedPeerDisplay(connection);

                if (connection.IsConnected && connection.IsReceiveLoopStarted)
                {
                    AddLog("TCP接続受信後、HELLO確認を開始します");
                    await SendHelloAsync(connection);
                    StartHelloTimeout(connection);
                }
            }, "TCP接続受け入れ処理");
        }

        private ChatConnection? GetSelectedPeerPreparedConnection()
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("送信する相手を選択してください", LogLevel.Error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("送信先RemoteIpAddressがありません。先にWi-Fi Direct接続してください。", LogLevel.Error);
                return null;
            }

            ChatConnection? connection = GetConnectionForPeer(peer);

            if (connection == null)
            {
                AddLog("Chat TCP未接続です。事前接続を確認してください。", LogLevel.Error);
                return null;
            }

            AddLog($"送信先PeerのChatConnectionを取得: {peer.DisplayName}", LogLevel.Debug);

            return connection;
        }

        private async System.Threading.Tasks.Task<ChatConnection?> GetOrCreateSelectedPeerConnectionAsync()
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("送信する相手を選択してください", LogLevel.Error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("送信先RemoteIpAddressがありません。先にWi-Fi Direct接続してください。", LogLevel.Error);
                return null;
            }

            ChatConnection? existing = _chatConnectionManager.FindForPeer(peer);

            if (existing?.IsConnected == true)
            {
                AddLog("Chat TCP接続済みなので再利用");
                return existing;
            }

            var connection = new ChatConnection
            {
                PeerId = PeerIdentityService.GetConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress,
                ShortSessionId = peer.ShortSessionId
            };

            _chatConnectionManager.AddConnection(connection);
            connection.IsPreparing = true;
            try
            {
                await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);
            }
            finally
            {
                connection.IsPreparing = false;
            }

            peer.IsTcpConnected = connection.IsConnected;
            RefreshPeerDisplay(peer);

            return connection;
        }

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer, string preparingStatusText = "TCP準備中")
        {
            if (peer.IsPreparingChatTcp || _chatConnectionManager.IsPreparingForPeer(peer))
            {
                AddLog($"PeerごとのTCP準備中のためスキップ: {peer.DisplayName}", LogLevel.Debug);
                AddLog("Chat TCP接続準備中のためスキップします", LogLevel.Debug);
                return;
            }

            if (peer.IsChatReady)
            {
                AddLog($"PeerごとのTCP準備スキップ: すでにチャット準備完了 {peer.DisplayName}", LogLevel.Debug);
                return;
            }

            peer.IsPreparingChatTcp = true;
            peer.StatusText = preparingStatusText;
            RefreshPeerDisplay(peer);
            UpdateSendButtonState();
            AddLog($"PeerごとのTCP準備開始: {peer.DisplayName}");

            try
            {
                UpdateSendButtonState();

                if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    AddLog("Chat TCP事前接続失敗: RemoteIpAddressがありません", LogLevel.Error);
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.StatusText = "送信不可";
                    RefreshPeerDisplay(peer);
                    return;
                }

                var totalWatch = Stopwatch.StartNew();
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.StatusText = preparingStatusText;
                RefreshPeerDisplay(peer);
                AddLog("チャット準備中: TCP事前接続を開始します");
                AddLog("Chat TCP事前接続開始");
                AddLog($"接続先IP: {peer.RemoteIpAddress}");
                AddLog($"接続先Port: {LocalTcpPort}");

                ChatConnection? connection = await GetOrCreatePeerConnectionAsync(peer);

                if (connection?.IsConnected == true && connection.IsReceiveLoopStarted)
                {
                    peer.IsTcpConnected = true;
                    peer.IsChatReady = false;
                    peer.StatusText = "HELLO確認中";
                    RefreshPeerDisplay(peer);
                    UpdateSendButtonState();
                    AddLog($"PeerごとのTCP接続成功: {peer.DisplayName}", LogLevel.Success);
                    AddLog("Chat TCP事前接続成功", LogLevel.Success);
                    AddLog("Chat TCP接続済み", LogLevel.Success);
                    AddLog("Chat TCP ReceiveLoop開始済み", LogLevel.Success);
                    await SendHelloAsync(connection);
                    StartHelloTimeout(connection);
                    AddLog("HELLO応答待ち");
                    AddLog($"Chat TCP事前接続合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
                }
                else
                {
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.StatusText = "エラー";
                    RefreshPeerDisplay(peer);
                    UpdateSendButtonState();
                    AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                    AddLog("Chat TCP事前接続失敗: TCP接続またはReceiveLoopが未完了です", LogLevel.Error);
                    AddLog($"PeerごとのTCP準備失敗: {peer.DisplayName}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                UpdateSendButtonState();
                AddLog($"PeerごとのTCP準備失敗: {peer.DisplayName}", LogLevel.Error);
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.StatusText = "エラー";
                RefreshPeerDisplay(peer);

                AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                AddLog("Chat TCP事前接続エラー", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                peer.IsPreparingChatTcp = false;
                RefreshPeerDisplay(peer);
                UpdateSendButtonState();
                AddLog($"PeerごとのTCP準備終了: {peer.DisplayName}", LogLevel.Debug);
            }
        }

        private async System.Threading.Tasks.Task<ChatConnection?> GetOrCreatePeerConnectionAsync(PeerInfo peer)
        {
            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("RemoteIpAddressがないためChat TCP自動接続をスキップします");
                return null;
            }

            ChatConnection? existing = _chatConnectionManager.FindForPeer(peer);

            if (existing?.IsConnected == true)
            {
                AddLog("Chat TCP接続済みなので再利用");
                peer.IsTcpConnected = true;
                peer.IsChatReady = peer.IsChatReady && existing.IsReceiveLoopStarted;
                peer.StatusText = peer.IsChatReady ? "チャット準備完了" : "HELLO確認中";
                RefreshPeerDisplay(peer);
                return existing;
            }

            var connection = new ChatConnection
            {
                PeerId = PeerIdentityService.GetConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress,
                ShortSessionId = peer.ShortSessionId
            };

            _chatConnectionManager.AddConnection(connection);
            AddLog($"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{LocalTcpPort}");
            connection.IsPreparing = true;
            try
            {
                await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);
            }
            finally
            {
                connection.IsPreparing = false;
            }

            peer.IsTcpConnected = connection.IsConnected;
            peer.IsChatReady = false;
            peer.StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可";
            RefreshPeerDisplay(peer);

            if (!connection.IsConnected)
            {
                _chatConnectionManager.RemoveConnection(connection);
            }

            return connection;
        }

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

            DispatcherQueue.TryEnqueue(() =>
            {
                AddChatMessage($"{message.SenderName}: {message.Body}");
                SaveChatMessageSafely(message, false, FindPeerForConnection(sourceConnection), sourceConnection);
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

        private async System.Threading.Tasks.Task ProcessFileTransferMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            await _fileTransferReceiveGate.WaitAsync();
            try
            {
                await HandleFileTransferMessageAsync(message, sourceConnection);
            }
            finally
            {
                _fileTransferReceiveGate.Release();
            }
        }

        private async System.Threading.Tasks.Task HandleFileTransferMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            try
            {
                if (_chatRole == ChatRole.Host && message.IsGroup)
                {
                    await _chatConnectionManager.BroadcastExceptAsync(message, sourceConnection);
                }

                FileTransferDisplayResult? displayResult = null;

                switch (message.Type.ToLowerInvariant())
                {
                    case "file_start":
                        displayResult = await _fileTransferService.HandleFileStartAsync(message);
                        break;
                    case "file_chunk":
                        await _fileTransferService.HandleFileChunkAsync(message);
                        break;
                    case "file_end":
                        displayResult = await _fileTransferService.HandleFileEndAsync(message);
                        break;
                }

                if (displayResult != null && !string.IsNullOrWhiteSpace(displayResult.Message))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddFileChatMessage(
                            $"{message.SenderName}: {displayResult.Message}",
                            displayResult.FileName,
                            displayResult.LocalFilePath);

                        if (string.Equals(message.Type, "file_end", StringComparison.OrdinalIgnoreCase))
                        {
                            var historyMessage = new ChatMessage
                            {
                                Type = "chat",
                                SenderId = message.SenderId,
                                SenderName = message.SenderName,
                                ShortSessionId = message.ShortSessionId,
                                Body = $"[ファイル] {message.FileName}",
                                IsGroup = message.IsGroup,
                                ConversationId = message.ConversationId
                            };

                            SaveChatMessageSafely(historyMessage, false, FindPeerForConnection(sourceConnection), sourceConnection);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                EnqueueLog($"ファイル受信処理に失敗しました: {ex.Message}", LogLevel.Error);
            }
        }

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
            AddLog($"HELLO確認後にPeerを正式統合: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog($"ChatConnectionとPeerInfoを紐付けました: {matchedPeer.DisplayName}");
            AddLog($"PeerごとのHELLO確認成功: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog("HELLO確認成功: BLE Peerと接続先が一致", LogLevel.Success);
            AddLog("HELLO確認後、チャット準備完了", LogLevel.Success);
            UpdateSendButtonState();
            await SendPingAfterHelloAsync(sourceConnection);
        }

        private PeerInfo? FindPeerByShortSessionId(string shortSessionId)
        {
            return _peerRegistryService.FindByShortSessionId(shortSessionId);
        }

        private PeerInfo? FindPeerByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            return _peerRegistryService.FindByRemoteIpOrName(remoteIpAddress, displayName);
        }

        private PeerInfo? FindPeerByPeerId(string peerId)
        {
            return _peerRegistryService.FindByPeerId(peerId);
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
            });
        }

        private PeerInfo? FindPeerForConnection(ChatConnection connection)
        {
            return _peerRegistryService.FindForConnection(connection);
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

        private async System.Threading.Tasks.Task EnsureTcpServerStartedAsync(string reason)
        {
            await _tcpServerStartGate.WaitAsync();
            try
            {
                if (_tcpServer.IsStarted)
                {
                    AddLog($"TCP待ち受けは開始済みです: Reason={reason}", LogLevel.Debug);
                    return;
                }

                AddLog($"TCP待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
                await _tcpServer.StartAsync(LocalTcpPort);
            }
            finally
            {
                _tcpServerStartGate.Release();
            }
        }

        private void EnqueueAsyncSafely(Func<System.Threading.Tasks.Task> action, string context)
        {
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        AddLog($"{context}に失敗しました: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                    }
                }))
            {
                EnqueueLog($"{context}をUIスレッドへ送れませんでした", LogLevel.Error);
            }
        }

        private async void RunSafelyInBackground(
            Func<System.Threading.Tasks.Task> action,
            string context)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                EnqueueLog($"{context}に失敗しました: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
        }


        private ChatConnection? GetConnectionForPeer(PeerInfo peer)
        {
            return _chatConnectionManager.FindForPeer(peer);
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
