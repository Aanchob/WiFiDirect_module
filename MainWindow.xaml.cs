using direct_module.Discovery;
using direct_module.Crypto;
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
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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

    public sealed partial class MainWindow : Window
    {
        private const int LocalTcpPort = 50001;
        private const int MaxLogLines = 500;
        private const int LogTrimBatchSize = 50;
        private static readonly TimeSpan WiFiDirectScanRestartDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan WiFiDirectGoAdvertisementWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan WiFiDirectCandidateRefreshTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan WiFiDirectCandidatePollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan IncomingTcpAuthorizationLifetime = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan TcpConnectionRetryWindow = TimeSpan.FromSeconds(18);
        private const int MaximumTcpConnectionAttempts = 10;
        private const int MaximumPendingAcceptedTcpConnections = 8;
        private const int MaximumPendingUiChatMessagesPerConnection = 128;
        private const int MaximumPendingTcpAuthorizations = 64;

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly ChatConnectionManager _chatConnectionManager = new();
        private readonly ChatMessageFactory _chatMessageFactory;
        private readonly List<string> _logLines = new();
        private readonly string _localPeerId;
        private readonly Guid _localSessionId = LocalIdentityService.CreateDiscoverySessionId();
        private readonly EcdsaChatIdentity? _localHandshakeIdentity;
        private ChatHistoryService? _chatHistoryService;
        private readonly TaskCompletionSource<ChatHistoryService?> _chatHistoryReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConnectionRoleService _connectionRoleService;
        private readonly PeerConnectionStateService _peerConnectionStateService;
        private readonly PeerRegistryService _peerRegistryService = new();
        private readonly FileTransferService _fileTransferService = new();
        private readonly CancellationTokenSource _windowLifetimeCancellation = new();
        private readonly SemaphoreSlim _bleRoleTransitionGate = new(1, 1);
        private readonly ConcurrentDictionary<string, PendingTcpAuthorization> _pendingTcpAuthorizations =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _pendingTcpAuthorizationGate = new();
        private readonly ConcurrentDictionary<ChatConnection, int> _pendingUiChatMessages = new();
        private readonly object _bleRoleGenerationGate = new();
        private BleRoleGenerationState? _bleRoleGenerationState;
        private int _bleRoleGeneration;
        private bool _isAutonomousGoAdvertisementEnabled;
        private string _activeBleRole = "";
        private int _pendingAcceptedTcpConnections;
        private readonly bool _localIdentityReady;
        private readonly bool _localIdentityRecoveryRequired;
        private readonly string _localIdentityFailureMessage = "";
        private int _shutdownStarted;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                _localPeerId = LocalIdentityService.GetOrCreatePeerId();
                _localHandshakeIdentity = LoadLocalHandshakeIdentity();
                _localIdentityReady = true;
            }
            catch (LocalIdentityStoreUnavailableException ex)
            {
                _localPeerId = "";
                _localIdentityRecoveryRequired = true;
                _localIdentityFailureMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _localPeerId = "";
                _localIdentityFailureMessage = ex.Message;
            }

            _chatMessageFactory = new ChatMessageFactory(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            _connectionRoleService = new ConnectionRoleService(
                GetLocalShortSessionId(),
                GetLocalRoleKey(),
                GetLocalDiscoveryIdentity());
            _peerConnectionStateService = new PeerConnectionStateService(_connectionRoleService);
            Title = "Hide Chat";
            ResizeWindow(1440, 920);

            _manager = new WiFiDirectManager();
            _discoveryManager = new DiscoveryManager();

            _manager.LogReceived += OnLogReceived;
            _manager.ConnectionRequested += OnConnectionRequested;
            _manager.IncomingConnectionApprovalAsync = ApproveIncomingConnectionAsync;
            _manager.PeerFound += OnPeerFound;
            _manager.SessionConnected += OnWiFiDirectSessionConnected;
            _manager.PeerRemoved += OnWiFiDirectPeerRemoved;
            _manager.Disconnected += OnWiFiDirectPeerRemoved;

            _discoveryManager.LogReceived += OnLogReceived;
            _discoveryManager.PeerFound += OnPeerFound;
            _discoveryManager.PeerRemoved += OnBlePeerRemoved;

            _tcpServer.LogReceived += OnLogReceived;
            _tcpServer.ConnectionAccepted += OnTcpConnectionAccepted;

            _chatConnectionManager.LogReceived += OnLogReceived;
            _chatConnectionManager.MessageReceived += OnChatMessageReceived;
            _chatConnectionManager.ConnectionDisconnected += OnChatConnectionDisconnected;
            _chatConnectionManager.ConnectionsChanged += OnChatConnectionsChanged;
            if (_localIdentityReady)
            {
                _chatConnectionManager.StartKeepAlive(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            }
            _fileTransferService.LogReceived += OnLogReceived;
            _fileTransferService.ProgressChanged += OnFileTransferProgressChanged;
            InitializeWindowLifecycle();

            if (!_localIdentityReady)
            {
                SearchPeersHeaderButton.IsEnabled = false;
                StartTcpServerHeaderButton.IsEnabled = false;
                NetworkDiagnosticsExpander.IsEnabled = false;
                PeerCountText.Text = "通信無効 / ローカル暗号ID要確認";
                if (Content is FrameworkElement rootContent)
                {
                    rootContent.Loaded += MainWindow_LocalIdentityUnavailableLoaded;
                }
            }

            AddGroupChatPeer();
            SetChatReady(false);
            UpdatePeerCount();
            UpdateSelectedPeerDetails(null);
            StartBackgroundOperation(
                InitializeLocalStorageAsync,
                "ローカルデータ初期化");
        }

        private void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            AddLog("相手探索開始");
            AddLog($"Local ShortSessionId: {GetLocalShortSessionId()}");
            AddLog($"Local RoleKey: {GetLocalRoleKey()}");
            _connectionRoleService.ResetBleNegotiation();
            _activeBleRole = "";
            ResetBleRoleNegotiationGeneration();

            _manager.Start(
                Environment.MachineName,
                GetLocalShortSessionId(),
                autonomousGroupOwner: false,
                peerIdentity: GetLocalDiscoveryIdentity(),
                tcpPort: LocalTcpPort);
            StartBackgroundOperation(
                () => EnsureTcpServerStartedAsync("相手探索開始"),
                "探索開始時のTCP待ち受け");
            StartBleAdvertiseCore();
            _discoveryManager.StartScan();

            ClearStaleWiFiDirectPeers();
            AddLog("BLE RoleKey判定後にClient側だけWi-Fi Direct探索を開始します");

            AddLog("相手探索処理を開始しました");
        }

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            _manager.Start(
                Environment.MachineName,
                GetLocalShortSessionId(),
                autonomousGroupOwner: false,
                peerIdentity: GetLocalDiscoveryIdentity(),
                tcpPort: LocalTcpPort);
            AddLog("Wi-Fi Direct広告+待ち受け開始ボタンを押しました");
            StartBackgroundOperation(
                () => EnsureTcpServerStartedAsync("Wi-Fi Direct広告+待ち受け開始"),
                "TCP待ち受けの開始");
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            AddLog("AssociationEndpoint探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            StartBackgroundOperation(
                _manager.StartAssociationEndpointScanAsync,
                "AssociationEndpoint探索");
        }

        private void SearchDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            AddLog("通常Wi-Fi Direct探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            StartBackgroundOperation(_manager.StartDefaultScanAsync, "Wi-Fi Direct探索");
        }

        private void ConnectIpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            string address = TargetIpTextBox.Text.Trim();
            if (!IPAddress.TryParse(address, out IPAddress? parsedAddress))
            {
                AddLog("有効なIPv4またはIPv6アドレスを入力してください。", LogLevel.Error);
                return;
            }

            string normalizedAddress = parsedAddress.ToString();
            var candidate = new PeerInfo
            {
                DisplayName = normalizedAddress,
                RemoteIpAddress = normalizedAddress,
                TcpPort = LocalTcpPort,
                StatusText = "手動接続待機"
            };
            PeerInfo peer = AddOrMergePeer(candidate);
            PeerList.SelectedItem = peer;
            StartBackgroundOperation(
                () => PrepareChatTcpConnectionAsync(peer),
                "手動TCP接続");
        }

        private void StartBleAdvertise_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;
            StartBleAdvertiseCore();
        }

        private void StartBleAdvertiseCore()
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

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
            if (!EnsureLocalIdentityReadyForNetworking()) return;
            _discoveryManager.StartScan();
        }

        private void StartTcpServer_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            StartBackgroundOperation(
                () => EnsureTcpServerStartedAsync("手動操作"),
                "TCP待ち受けの開始");
        }

        private void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください", LogLevel.Error);
                return;
            }

            StartBackgroundOperation(() => ConnectPeerAsync(peer), "Peer接続");
        }

        private void ConnectPeerItem_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            if ((sender as FrameworkElement)?.DataContext is not PeerInfo peer)
            {
                AddLog("接続対象Peerを取得できませんでした", LogLevel.Error);
                return;
            }

            PeerList.SelectedItem = peer;
            StartBackgroundOperation(() => ConnectPeerAsync(peer), "Peer接続");
        }

        private async System.Threading.Tasks.Task ConnectPeerAsync(PeerInfo peer)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            _peerConnectionStateService.UpdateConnectAvailability(peer);
            if (!peer.CanConnect)
            {
                AddLog($"接続条件を満たしていないため接続を開始しません: Peer={peer.DisplayName}", LogLevel.Error);
                AddLog("接続にはBLEとWi-Fi Directの両方、ShortSessionId、RoleKey、Client側判定が必要です", LogLevel.Error);
                RefreshPeerDisplay(peer);
                return;
            }

            if (peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                AddLog("_PendingRequest付きDeviceIdのため通常接続を中止します", LogLevel.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                AddLog("選択中PeerにWi-Fi Direct DeviceIdがありません", LogLevel.Error);
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
                peer.IsConnectingWiFiDirect = true;
                peer.StatusText = "Wi-Fi Direct接続準備中";
                RefreshPeerDisplay(peer);

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
                await System.Threading.Tasks.Task.Delay(
                    WiFiDirectScanRestartDelay,
                    _windowLifetimeCancellation.Token);

                connectAttempted = true;
                await _manager.ConnectAsync(peer);
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
                AddLog($"Wi-Fi Direct candidate is already available. Reusing DeviceId={peer.DeviceId}", LogLevel.Debug);
                _manager.StopScan();
                await System.Threading.Tasks.Task.Delay(
                    WiFiDirectScanRestartDelay,
                    _windowLifetimeCancellation.Token);
                return true;
            }

            _manager.StopScan();
            await System.Threading.Tasks.Task.Delay(
                WiFiDirectScanRestartDelay,
                _windowLifetimeCancellation.Token);

            ClearWiFiDirectCandidateForPreConnect(peer);

            AddLog($"GO再広告待機: {WiFiDirectGoAdvertisementWait.TotalSeconds:0.0}秒");
            await System.Threading.Tasks.Task.Delay(
                WiFiDirectGoAdvertisementWait,
                _windowLifetimeCancellation.Token);

            AddLog("接続前Wi-Fi Direct再スキャン開始");
            await _manager.StartAssociationEndpointScanAsync();

            if (await WaitForWiFiDirectCandidateAsync(peer, WiFiDirectCandidateRefreshTimeout))
            {
                AddLog($"Wi-Fi Direct候補再取得: Peer={peer.DisplayName}, DeviceId={peer.DeviceId}", LogLevel.Success);
                _manager.StopScan();
                await System.Threading.Tasks.Task.Delay(
                    WiFiDirectScanRestartDelay,
                    _windowLifetimeCancellation.Token);
                return true;
            }

            return false;
        }

        private void ClearWiFiDirectCandidateForPreConnect(PeerInfo peer)
        {
            peer.DiscoveredByWiFiDirect = false;
            peer.WiFiDirectName = "";
            peer.DeviceId = "";
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

                await System.Threading.Tasks.Task.Delay(
                    WiFiDirectCandidatePollInterval,
                    _windowLifetimeCancellation.Token);
            }

            return HasUsableWiFiDirectCandidate(peer);
        }

        private static bool HasUsableWiFiDirectCandidate(PeerInfo peer)
        {
            return peer.DiscoveredByWiFiDirect &&
                   !string.IsNullOrWhiteSpace(peer.DeviceId) &&
                   !peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);
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
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("再接続対象Peerが選択されていません", LogLevel.Error);
                return;
            }

            StartBackgroundOperation(() => ReconnectPeerAsync(peer), "Peer再接続");
        }

        private void ScrollLogBottom_Click(object sender, RoutedEventArgs e)
        {
            MoveLogCaretToEnd();
        }

        private void OnPeerFound(PeerInfo peer)
        {
            TryEnqueueBackgroundOperation(
                async () =>
                {
                    PeerInfo effectivePeer = AddOrMergePeer(peer);

                    if (effectivePeer.DiscoveredByBle)
                    {
                        await HandleBleRoleNegotiationAsync(effectivePeer);
                    }
                },
                "Peer探索結果の処理");
        }

        private void OnBlePeerRemoved(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
                }

                PeerPresenceRemovalResult removal = _peerRegistryService.RemoveBlePresence(peer);
                if (!removal.SourceChanged)
                {
                    return;
                }

                ResetBleRoleNegotiationGeneration();
                if (removal.Peer != null)
                {
                    if (removal.CollectionChanged)
                    {
                        PeerList.Items.Remove(removal.Peer);
                    }
                    else
                    {
                        RefreshPeerDisplay(removal.Peer);
                    }
                }

                PeerInfo? remainingBlePeer = _peerRegistryService.Peers.FirstOrDefault(candidate =>
                    !candidate.IsGroupChat &&
                    candidate.DiscoveredByBle &&
                    ConnectionRoleService.HasRoleKey(candidate));
                if (remainingBlePeer == null)
                {
                    if (_isAutonomousGoAdvertisementEnabled)
                    {
                        _manager.StopAdvertisement();
                    }
                    _manager.StopScan();
                    _activeBleRole = "";
                    _isAutonomousGoAdvertisementEnabled = false;
                    _connectionRoleService.ResetBleNegotiation();
                    AddLog("利用可能なBLE Peerがなくなったため自動Wi-Fi Directロールを解除しました。");
                }
                else
                {
                    StartBackgroundOperation(
                        () => HandleBleRoleNegotiationAsync(remainingBlePeer),
                        "BLE Peer消失後のロール再選出");
                }

                UpdatePeerCount();
                UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            });
        }

        private void OnLogReceived(string message)
        {
            EnqueueLog(message, LogClassifier.Classify(message));
        }

        private async System.Threading.Tasks.Task HandleBleRoleNegotiationAsync(PeerInfo peer)
        {
            (int generation, BleRoleGenerationState generationState, CancellationToken cancellationToken) =
                CaptureBleRoleNegotiationGeneration();
            bool gateAcquired = false;
            try
            {
                await _bleRoleTransitionGate.WaitAsync(cancellationToken);
                gateAcquired = true;
                ThrowIfBleRoleNegotiationIsStale(generation, cancellationToken);

                var decisions = _peerRegistryService.Peers
                    .Where(candidate => !candidate.IsGroupChat &&
                                        candidate.DiscoveredByBle &&
                                        ConnectionRoleService.HasRoleKey(candidate))
                    .Select(candidate => new
                    {
                        Peer = candidate,
                        Decision = _connectionRoleService.DecideBleRole(
                            candidate,
                            PeerIdentityService.GetConnectionId(candidate))
                    })
                    .ToList();

                var collisions = decisions.Where(item =>
                        item.Decision.Status == BleRoleNegotiationStatus.RoleKeyCollision)
                    .ToList();
                foreach (var collision in collisions)
                {
                    AddLog(
                        $"BLE探索ID衝突を検出しました: {collision.Peer.DisplayName}",
                        LogLevel.Error);
                }

                if (collisions.Count > 0)
                {
                    // A collision anywhere in the visible set makes a subset election
                    // unsafe: two colliding devices could both elect themselves GO
                    // relative to a third peer. Fail closed for all automatic roles.
                    if (_isAutonomousGoAdvertisementEnabled)
                    {
                        _manager.StopAdvertisement();
                    }
                    _manager.StopScan();
                    _activeBleRole = "";
                    _isAutonomousGoAdvertisementEnabled = false;
                    AddLog(
                        "BLE探索ID衝突が解消されるまで自動Wi-Fi Directロール選出を停止します。",
                        LogLevel.Error);
                    return;
                }

                var usableDecisions = decisions
                    .Where(item => item.Decision.Status is BleRoleNegotiationStatus.LocalGo or BleRoleNegotiationStatus.LocalClient)
                    .ToList();
                if (usableDecisions.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(_activeBleRole))
                    {
                        if (_isAutonomousGoAdvertisementEnabled)
                        {
                            _manager.StopAdvertisement();
                        }
                        _manager.StopScan();
                        _activeBleRole = "";
                        _isAutonomousGoAdvertisementEnabled = false;
                    }
                    AddLog($"BLE探索IDが未確認のため自動Wi-Fi Direct接続を開始しません: {peer.DisplayName}", LogLevel.Debug);
                    return;
                }

                // Elect one group owner across every known peer. Pair-local state can
                // demand contradictory roles when three or more devices are visible.
                bool localIsGo = usableDecisions.All(item => item.Decision.LocalIsGo);
                string nextRole = localIsGo ? "GO" : "Client";
                AddLog($"BLE group role election: KnownPeers={usableDecisions.Count}, LocalRole={nextRole}");

                if (localIsGo)
                {
                    if (string.Equals(_activeBleRole, nextRole, StringComparison.Ordinal) &&
                        _isAutonomousGoAdvertisementEnabled)
                    {
                        return;
                    }

                    _manager.StopScan();
                    bool started = _manager.RestartAdvertisement(
                        Environment.MachineName,
                        GetLocalShortSessionId(),
                        autonomousGroupOwner: true,
                        peerIdentity: GetLocalDiscoveryIdentity(),
                        tcpPort: LocalTcpPort);
                    if (!started)
                    {
                        AddLog("Autonomous GO広告の開始に失敗しました。", LogLevel.Error);
                        _activeBleRole = "";
                        _isAutonomousGoAdvertisementEnabled = false;
                        return;
                    }

                    _activeBleRole = nextRole;
                    _isAutonomousGoAdvertisementEnabled = true;
                    await EnsureTcpServerStartedAsync("Autonomous GO開始");
                    ThrowIfBleRoleNegotiationIsStale(generation, cancellationToken);
                    AddLog("Autonomous GO広告を開始しました。ClientからのJoinを待ちます");
                    return;
                }

                if (string.Equals(_activeBleRole, nextRole, StringComparison.Ordinal))
                {
                    // The watcher may have stopped independently (adapter reset,
                    // policy change, or enumeration failure). Start is idempotent
                    // while active and also repairs that stale role state.
                    await _manager.StartAssociationEndpointScanAsync();
                    return;
                }

                _activeBleRole = nextRole;
                _isAutonomousGoAdvertisementEnabled = false;
                _manager.StopAdvertisement();
                AddLog("Clientロールのため、GOの起動を待ってから探索します");
                await System.Threading.Tasks.Task.Delay(1500, cancellationToken);
                ThrowIfBleRoleNegotiationIsStale(generation, cancellationToken);
                ClearStaleWiFiDirectPeers();
                await _manager.StartAssociationEndpointScanAsync();
                ThrowIfBleRoleNegotiationIsStale(generation, cancellationToken);
                AddLog("BLE group role election後にClient側のWi-Fi Direct探索を開始しました");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (gateAcquired)
                {
                    _bleRoleTransitionGate.Release();
                }
                ReleaseBleRoleNegotiationGeneration(generationState);
            }
        }

        private void ResetBleRoleNegotiationGeneration()
        {
            BleRoleGenerationState? previous;
            bool disposePrevious;
            lock (_bleRoleGenerationGate)
            {
                previous = _bleRoleGenerationState;
                _bleRoleGenerationState = new BleRoleGenerationState(
                    CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCancellation.Token));
                _bleRoleGeneration++;
                if (previous != null)
                {
                    previous.IsRetired = true;
                }
                disposePrevious = previous is { LeaseCount: 0 };
            }

            if (previous != null)
            {
                previous.Cancel();
                if (disposePrevious)
                {
                    previous.Dispose();
                }
            }
        }

        private (int Generation, BleRoleGenerationState State, CancellationToken CancellationToken)
            CaptureBleRoleNegotiationGeneration()
        {
            lock (_bleRoleGenerationGate)
            {
                _bleRoleGenerationState ??= new BleRoleGenerationState(
                    CancellationTokenSource.CreateLinkedTokenSource(_windowLifetimeCancellation.Token));
                _bleRoleGenerationState.LeaseCount++;
                return (
                    _bleRoleGeneration,
                    _bleRoleGenerationState,
                    _bleRoleGenerationState.Cancellation.Token);
            }
        }

        private void ReleaseBleRoleNegotiationGeneration(BleRoleGenerationState state)
        {
            bool dispose;
            lock (_bleRoleGenerationGate)
            {
                if (state.LeaseCount <= 0)
                {
                    return;
                }

                state.LeaseCount--;
                dispose = state.IsRetired && state.LeaseCount == 0;
            }

            if (dispose)
            {
                state.Dispose();
            }
        }

        private void ThrowIfBleRoleNegotiationIsStale(int generation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_bleRoleGenerationGate)
            {
                if (generation != _bleRoleGeneration)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }

        private sealed class BleRoleGenerationState : IDisposable
        {
            private int _disposed;

            public BleRoleGenerationState(CancellationTokenSource cancellation)
            {
                Cancellation = cancellation;
            }

            public CancellationTokenSource Cancellation { get; }
            public int LeaseCount { get; set; }
            public bool IsRetired { get; set; }

            public void Cancel()
            {
                try
                {
                    Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Cancellation.Dispose();
                }
            }
        }

        private void OnConnectionRequested(PeerInfo peer)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            TryEnqueueBackgroundOperation(async () =>
            {
                AddLog($"接続要求: {peer.DisplayName}");
                await EnsureTcpServerStartedAsync("Wi-Fi Direct接続要求受信");
            }, "Wi-Fi Direct接続要求の処理");
        }

        private void OnWiFiDirectSessionConnected(WiFiDirectSession session)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            PruneExpiredTcpAuthorizations(now);
            string remoteAddress = "";
            if (TryNormalizeRemoteIpAddress(
                    session.RemoteIpAddress,
                    out remoteAddress,
                    out string authorizationKey))
            {
                lock (_pendingTcpAuthorizationGate)
                {
                    PruneExpiredTcpAuthorizations(now);
                    if (_pendingTcpAuthorizations.ContainsKey(authorizationKey) ||
                        _pendingTcpAuthorizations.Count < MaximumPendingTcpAuthorizations)
                    {
                        _pendingTcpAuthorizations[authorizationKey] = new PendingTcpAuthorization(
                            session.Peer,
                            now + IncomingTcpAuthorizationLifetime);
                    }
                    else
                    {
                        EnqueueLog(
                            "保留中の受信TCP承認が上限に達したため、新しい承認を拒否しました。",
                            LogLevel.Error);
                    }
                }
            }
            else
            {
                EnqueueLog(
                    "Wi-Fi Direct sessionから有効なRemote IPを取得できなかったためTCPを承認しません。",
                    LogLevel.Error);
            }

            TryEnqueueBackgroundOperation(
                async () =>
                {
                    PeerInfo peer = session.Peer;
                    peer.RemoteIpAddress = remoteAddress;
                    peer.IsConnected = true;
                    peer.IsConnectingWiFiDirect = false;
                    if (IsTransientWiFiDirectStatus(peer.StatusText))
                    {
                        peer.StatusText = "";
                    }

                    AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}", LogLevel.Success);
                    AddOrMergePeer(peer);
                    await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");

                    PeerInfo effectivePeer = FindPeerForTcpRoleDecision(peer) ?? peer;
                    if (session.Direction == WiFiDirectConnectionDirection.Outgoing)
                    {
                        AddLog($"発信Wi-Fi Direct sessionのためTCP接続側になります: Peer={effectivePeer.DisplayName}");
                        await PrepareChatTcpConnectionAsync(effectivePeer);
                    }
                    else
                    {
                        AddLog($"受信Wi-Fi Direct sessionのためTCP接続を待ち受けます: Peer={effectivePeer.DisplayName}");
                    }
                },
                "Wi-Fi Direct接続完了処理");
        }

        private void OnWiFiDirectPeerRemoved(PeerInfo peer)
        {
            if (TryNormalizeRemoteIpAddress(
                    peer.RemoteIpAddress,
                    out _,
                    out string authorizationKey))
            {
                _pendingTcpAuthorizations.TryRemove(authorizationKey, out _);
            }
            PruneExpiredTcpAuthorizations(DateTimeOffset.UtcNow);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
                }

                PeerInfo effectivePeer = _peerRegistryService.Peers.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate, peer) ||
                    (!string.IsNullOrWhiteSpace(peer.DeviceId) &&
                     string.Equals(candidate.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(peer.MatchKey) &&
                     string.Equals(candidate.MatchKey, peer.MatchKey, StringComparison.OrdinalIgnoreCase))) ?? peer;

                effectivePeer.IsConnected = false;
                effectivePeer.IsConnectingWiFiDirect = false;
                if (!effectivePeer.IsTcpConnected)
                {
                    effectivePeer.IsChatReady = false;
                    effectivePeer.StatusText = "切断";
                }
                RefreshPeerDisplay(effectivePeer);
            });
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            ArgumentNullException.ThrowIfNull(socket);
            if (!EnsureLocalIdentityReadyForNetworking())
            {
                socket.Dispose();
                return;
            }

            if (Interlocked.Increment(ref _pendingAcceptedTcpConnections) >
                MaximumPendingAcceptedTcpConnections)
            {
                Interlocked.Decrement(ref _pendingAcceptedTcpConnections);
                socket.Dispose();
                EnqueueLog(
                    "受信TCP接続の保留上限を超えたため接続を拒否しました。",
                    LogLevel.Error);
                return;
            }

            int slotReleased = 0;
            void ReleasePendingSlot()
            {
                if (Interlocked.Exchange(ref slotReleased, 1) == 0)
                {
                    Interlocked.Decrement(ref _pendingAcceptedTcpConnections);
                }
            }

            try
            {
                string rawRemoteAddress = socket.Information.RemoteAddress?.DisplayName ?? "";
                if (!TryNormalizeRemoteIpAddress(
                        rawRemoteAddress,
                        out string remoteAddress,
                        out string authorizationKey))
                {
                    socket.Dispose();
                    ReleasePendingSlot();
                    EnqueueLog("IPアドレスを確認できない受信TCP接続を拒否しました。", LogLevel.Error);
                    return;
                }

                PruneExpiredTcpAuthorizations(DateTimeOffset.UtcNow);
                TryEnqueueBackgroundOperation(
                    async () =>
                    {
                        ChatConnection? connection = null;
                        try
                        {
                            // Peer registry and PeerList are UI-thread owned. Unknown
                            // LAN sockets cannot pass HELLO policy, so reject them
                            // before consuming an expensive handshake slot.
                            PeerInfo? expectedPeer = FindPeerByRemoteIpOrName(remoteAddress, "");
                            if (expectedPeer == null &&
                                _pendingTcpAuthorizations.TryRemove(
                                    authorizationKey,
                                    out PendingTcpAuthorization authorization) &&
                                authorization.ExpiresAtUtc >= DateTimeOffset.UtcNow)
                            {
                                authorization.Peer.RemoteIpAddress = remoteAddress;
                                expectedPeer = AddOrMergePeer(authorization.Peer);
                            }
                            if (expectedPeer == null)
                            {
                                socket.Dispose();
                                AddLog($"探索・承認されていない受信TCP接続を拒否しました: {remoteAddress}", LogLevel.Error);
                                return;
                            }

                            connection = CreateAuthenticatedChatConnection(expectedPeer);
                            connection.PeerId = expectedPeer.PeerId;
                            connection.PeerName = expectedPeer.DisplayName;
                            connection.RemoteIpAddress = remoteAddress;
                            connection.ShortSessionId = expectedPeer.ShortSessionId;

                            if (!_chatConnectionManager.TryAddConnection(connection))
                            {
                                socket.Dispose();
                                return;
                            }

                            await connection.AttachAcceptedSocketAsync(
                                socket,
                                _windowLifetimeCancellation.Token);

                            AddLog($"受信TCP接続を追加 Count={_chatConnectionManager.ConnectedCount}");

                            if (connection.IsConnected && connection.IsReceiveLoopStarted)
                            {
                                AddLog("TCP接続受信後、HELLO確認を開始します");
                                await SendHelloAsync(connection);
                            }
                        }
                        catch
                        {
                            socket.Dispose();
                            connection?.Close();
                            throw;
                        }
                        finally
                        {
                            ReleasePendingSlot();
                        }
                    },
                    "受信TCP接続の初期化",
                    onRejected: () =>
                    {
                        socket.Dispose();
                        ReleasePendingSlot();
                    });
            }
            catch (Exception ex)
            {
                socket.Dispose();
                ReleasePendingSlot();
                EnqueueLog($"受信TCP接続の受付に失敗しました: {ex.Message}", LogLevel.Error);
            }
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

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer, string preparingStatusText = "TCP準備中")
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

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
                SetChatReady(false);

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
                AddLog($"接続先Port: {GetPeerTcpPort(peer)}");

                ChatConnection? connection = await GetOrCreatePeerConnectionAsync(peer);

                if (connection?.IsConnected == true && connection.IsReceiveLoopStarted)
                {
                    peer.IsTcpConnected = true;
                    peer.IsChatReady = false;
                    peer.StatusText = "HELLO確認中";
                    RefreshPeerDisplay(peer);
                    SetChatReady(false);
                    AddLog($"PeerごとのTCP接続成功: {peer.DisplayName}", LogLevel.Success);
                    AddLog("Chat TCP事前接続成功", LogLevel.Success);
                    AddLog("Chat TCP接続済み", LogLevel.Success);
                    AddLog("Chat TCP ReceiveLoop開始済み", LogLevel.Success);
                    await SendHelloAsync(connection);
                    AddLog("HELLO応答待ち");
                    AddLog($"Chat TCP事前接続合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
                }
                else
                {
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.StatusText = "エラー";
                    RefreshPeerDisplay(peer);
                    SetChatReady(false);
                    AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                    AddLog("Chat TCP事前接続失敗: TCP接続またはReceiveLoopが未完了です", LogLevel.Error);
                    AddLog($"PeerごとのTCP準備失敗: {peer.DisplayName}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                SetChatReady(false);
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
            if (!EnsureLocalIdentityReadyForNetworking()) return null;

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

            if (existing != null)
            {
                existing.Close();
                _chatConnectionManager.RemoveConnection(existing);
            }

            int peerPort = GetPeerTcpPort(peer);
            using var retryWindow = CancellationTokenSource.CreateLinkedTokenSource(
                _windowLifetimeCancellation.Token);
            retryWindow.CancelAfter(TcpConnectionRetryWindow);

            for (int attempt = 1; attempt <= MaximumTcpConnectionAttempts; attempt++)
            {
                retryWindow.Token.ThrowIfCancellationRequested();
                ChatConnection connection = CreateAuthenticatedChatConnection(peer);
                connection.PeerId = PeerIdentityService.GetConnectionId(peer);
                connection.PeerName = peer.DisplayName;
                connection.RemoteIpAddress = peer.RemoteIpAddress;
                connection.ShortSessionId = peer.ShortSessionId;

                if (!_chatConnectionManager.TryAddConnection(connection))
                {
                    connection.Close();
                    return null;
                }

                AddLog(
                    $"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{peerPort} " +
                    $"(試行 {attempt}/{MaximumTcpConnectionAttempts})");
                connection.IsPreparing = true;
                try
                {
                    await connection.ConnectAsync(
                        peer.RemoteIpAddress,
                        peerPort,
                        retryWindow.Token);

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
                catch (OperationCanceledException ex) when (
                    !_windowLifetimeCancellation.IsCancellationRequested &&
                    retryWindow.IsCancellationRequested)
                {
                    connection.Close();
                    _chatConnectionManager.RemoveConnection(connection);
                    throw new TimeoutException(
                        $"TCP接続が{TcpConnectionRetryWindow.TotalSeconds:F0}秒以内に完了しませんでした。",
                        ex);
                }
                catch (Exception ex) when (
                    attempt < MaximumTcpConnectionAttempts &&
                    !retryWindow.IsCancellationRequested &&
                    IsTransientTcpConnectionFailure(ex))
                {
                    connection.Close();
                    _chatConnectionManager.RemoveConnection(connection);
                    TimeSpan retryDelay = TimeSpan.FromMilliseconds(
                        Math.Min(2000, 250 * (1 << Math.Min(attempt - 1, 3))));
                    AddLog(
                        $"TCP待ち受け準備中の可能性があるため{retryDelay.TotalMilliseconds:F0}ms後に再試行します。",
                        LogLevel.Debug);
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(retryDelay, retryWindow.Token);
                    }
                    catch (OperationCanceledException delayCancellation) when (
                        !_windowLifetimeCancellation.IsCancellationRequested &&
                        retryWindow.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"TCP接続が{TcpConnectionRetryWindow.TotalSeconds:F0}秒以内に完了しませんでした。",
                            delayCancellation);
                    }
                }
                catch
                {
                    connection.Close();
                    _chatConnectionManager.RemoveConnection(connection);
                    throw;
                }
                finally
                {
                    connection.IsPreparing = false;
                }
            }

            return null;
        }

        private static bool IsTransientTcpConnectionFailure(Exception exception)
        {
            // ChatConnection's own connect timeout surfaces as cancellation while the
            // caller's retry window remains active.
            if (exception is OperationCanceledException or TimeoutException)
            {
                return true;
            }

            SocketErrorStatus status = SocketError.GetStatus(exception.HResult);
            return status is
                SocketErrorStatus.ConnectionRefused or
                SocketErrorStatus.ConnectionTimedOut or
                SocketErrorStatus.NetworkDroppedConnectionOnReset or
                SocketErrorStatus.NetworkIsDown or
                SocketErrorStatus.NetworkIsUnreachable or
                SocketErrorStatus.HostIsDown or
                SocketErrorStatus.UnreachableHost or
                SocketErrorStatus.CannotAssignRequestedAddress;
        }

        private void OnChatMessageReceived(ChatMessage message, ChatConnection sourceConnection)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            string messageType = string.IsNullOrWhiteSpace(message.Type)
                ? "chat"
                : message.Type;

            switch (messageType.ToLowerInvariant())
            {
                case "hello":
                    TryEnqueueBackgroundOperation(
                        () => HandleHelloMessageAsync(message, sourceConnection),
                        "HELLOメッセージ処理",
                        sourceConnection);
                    return;

                case "chat":
                    break;

                case "ping":
                    StartBackgroundOperation(
                        () => HandlePingMessageAsync(message, sourceConnection),
                        "PINGメッセージ処理",
                        sourceConnection);
                    return;

                case "pong":
                    HandlePongMessage(message, sourceConnection);
                    return;

                case "file_start":
                case "file_chunk":
                case "file_end":
                case "file_abort":
                    QueueFileTransferMessage(message, sourceConnection);
                    return;

                case "file_ack":
                    HandleFileAcknowledgement(message, sourceConnection);
                    return;

                default:
                    EnqueueLog($"不明なChatMessage Typeを受信: {messageType}", LogLevel.Error);
                    return;
            }

            int pendingUiMessages = _pendingUiChatMessages.AddOrUpdate(
                sourceConnection,
                1,
                static (_, current) => current + 1);
            if (pendingUiMessages > MaximumPendingUiChatMessagesPerConnection)
            {
                ReleasePendingUiChatMessage(sourceConnection);
                EnqueueLog(
                    "受信チャットのUI処理上限を超えたため接続を閉じました。",
                    LogLevel.Error);
                sourceConnection.Close();
                return;
            }

            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (Volatile.Read(ref _shutdownStarted) != 0)
                    {
                        return;
                    }

                    string conversationId = GetConversationIdForMessage(message, sourceConnection);
                    message.ConversationId = conversationId;
                    AddChatMessage(message.Body, conversationId, isMine: false, senderName: message.SenderName, message.MessageId);
                    SaveChatMessageSafely(message, false, FindPeerForConnection(sourceConnection), sourceConnection);
                    AddLog($"TCP受信メッセージ: MessageId={message.MessageId}", LogLevel.Success);
                    AddConnectedPeerDisplay(sourceConnection);

                    // Normalize/bind the conversation on the UI-owned peer registry
                    // before creating the relay envelope. Relaying outside this
                    // callback raced the mutation of the shared ChatMessage instance.
                    if (sourceConnection.ShouldRelayGroupMessages &&
                        message.IsGroup &&
                        message.HopCount == 0)
                    {
                        StartBackgroundOperation(
                            () => RelayGroupChatMessageAsync(message, sourceConnection),
                            "グループメッセージ中継");
                    }
                }
                catch (Exception ex)
                {
                    EnqueueLog(
                        $"受信チャットのUI反映に失敗しました: {ex.Message}",
                        LogLevel.Error);
                    sourceConnection.Close();
                }
                finally
                {
                    ReleasePendingUiChatMessage(sourceConnection);
                }
            }))
            {
                ReleasePendingUiChatMessage(sourceConnection);
                return;
            }

        }

        private void ReleasePendingUiChatMessage(ChatConnection connection)
        {
            while (_pendingUiChatMessages.TryGetValue(connection, out int current))
            {
                if (current <= 1)
                {
                    if (((ICollection<KeyValuePair<ChatConnection, int>>)_pendingUiChatMessages)
                        .Remove(new KeyValuePair<ChatConnection, int>(connection, current)))
                    {
                        return;
                    }
                }
                else if (_pendingUiChatMessages.TryUpdate(connection, current - 1, current))
                {
                    return;
                }
            }
        }

        private async System.Threading.Tasks.Task RelayGroupChatMessageAsync(
            ChatMessage message,
            ChatConnection sourceConnection)
        {
            ChatMessage relay = message.CreateRelayEnvelope(
                LocalPeerId,
                Environment.MachineName,
                GetLocalShortSessionId());
            EnqueueLog($"グループ中継開始: From={message.SenderName}, MessageId={message.MessageId}");
            List<ChatConnection> targets = SnapshotReadyFileRecipients(sourceConnection)
                .Where(connection => connection.IsInbound)
                .ToList();
            if (targets.Count == 0)
            {
                EnqueueLog("グループ中継先のPeerはありません。", LogLevel.Debug);
                return;
            }

            string[] failures = (await Task.WhenAll(targets.Select(async target =>
            {
                try
                {
                    await target.SendAsync(relay, _windowLifetimeCancellation.Token);
                    return "";
                }
                catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    target.Close();
                    return $"{GetStableRemotePeerId(target)} ({ex.Message})";
                }
            }))).Where(failure => failure.Length > 0).ToArray();
            if (failures.Length > 0)
            {
                EnqueueLog(
                    $"グループ中継は一部失敗しました: {string.Join(", ", failures)}",
                    LogLevel.Error);
                return;
            }
            EnqueueLog("グループ中継完了");
        }

        private void QueueFileTransferMessage(ChatMessage message, ChatConnection sourceConnection)
        {
            if (_windowLifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            SemaphoreSlim processingGate = _fileProcessingGates.GetOrAdd(
                sourceConnection,
                _ => new SemaphoreSlim(1, 1));
            using CancellationTokenSource waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _windowLifetimeCancellation.Token,
                    sourceConnection.LifetimeToken);
            try
            {
                // File start/chunk processing is intentionally bounded backpressure:
                // it preserves exact wire order without buffering a whole fast-LAN
                // transfer in memory. The long downstream-ACK wait for file_end is
                // detached by HandleFileTransferMessageAsync before this gate releases.
                processingGate.Wait(waitCancellation.Token);
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
                return;
            }

            if (!StartBackgroundOperation(
                    () => ProcessOrderedFileTransferMessageAsync(
                        message,
                        sourceConnection,
                        processingGate),
                    "ファイル受信処理"))
            {
                processingGate.Release();
            }
        }

        private async System.Threading.Tasks.Task ProcessOrderedFileTransferMessageAsync(
            ChatMessage message,
            ChatConnection sourceConnection,
            SemaphoreSlim processingGate)
        {
            using CancellationTokenSource processingCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _windowLifetimeCancellation.Token,
                    sourceConnection.LifetimeToken);
            try
            {
                await HandleFileTransferMessageAsync(
                    message,
                    sourceConnection,
                    processingCancellation.Token);
            }
            finally
            {
                processingGate.Release();
            }
        }

        private async System.Threading.Tasks.Task HandleFileTransferMessageAsync(
            ChatMessage message,
            ChatConnection sourceConnection,
            CancellationToken cancellationToken)
        {
            RelayFileTransferContext? relayContext = null;
            bool isFileStart = string.Equals(message.Type, "file_start", StringComparison.OrdinalIgnoreCase);
            bool isFileEnd = string.Equals(message.Type, "file_end", StringComparison.OrdinalIgnoreCase);
            bool isFileAbort = string.Equals(message.Type, "file_abort", StringComparison.OrdinalIgnoreCase);
            bool isFileTerminal = isFileEnd || isFileAbort;
            bool shouldRelay = isFileStart &&
                               sourceConnection.ShouldRelayGroupMessages &&
                               message.IsGroup &&
                               message.HopCount == 0;
            string transferSourceId = "";
            bool relayDeliveryCompleted = false;

            try
            {
                if (string.IsNullOrWhiteSpace(sourceConnection.RemoteIdentityFingerprint))
                {
                    throw new InvalidOperationException("認証済みの転送元暗号IDがありません。");
                }
                transferSourceId = $"{sourceConnection.RemoteIdentityFingerprint}:{message.SenderId}";
                FileTransferDisplayResult? displayResult = null;
                if (!string.Equals(message.Type, "file_start", StringComparison.OrdinalIgnoreCase) &&
                    TryGetRelayFileTransferContext(message, out RelayFileTransferContext? existingRelayContext))
                {
                    relayContext = existingRelayContext;
                    ValidateRelayContinuation(message, sourceConnection, relayContext!);
                    shouldRelay = true;
                }

                switch (message.Type.ToLowerInvariant())
                {
                    case "file_start":
                        await _fileTransferService.HandleFileStartAsync(
                            message,
                            transferSourceId,
                            cancellationToken);
                        TrackIncomingTransfer(
                            sourceConnection,
                            message.FileId ?? "",
                            transferSourceId);
                        if (shouldRelay)
                        {
                            relayContext = CreateRelayFileTransferContext(message, sourceConnection);
                            await RelayFileMessageToSnapshotAsync(
                                message,
                                relayContext!,
                                cancellationToken);
                        }
                        break;
                    case "file_chunk":
                        await _fileTransferService.HandleFileChunkAsync(
                            message,
                            transferSourceId,
                            cancellationToken);
                        if (shouldRelay)
                        {
                            await RelayFileMessageToSnapshotAsync(
                                message,
                                relayContext!,
                                cancellationToken);
                        }
                        break;
                    case "file_end":
                        displayResult = await _fileTransferService.HandleFileEndAsync(
                            message,
                            transferSourceId,
                            cancellationToken);
                        if (displayResult != null &&
                            !string.IsNullOrWhiteSpace(displayResult.LocalFilePath))
                        {
                            EnqueueCompletedIncomingFile(
                                message,
                                sourceConnection,
                                displayResult);
                        }

                        FileTransferAcknowledgement? downstreamAcknowledgement = null;
                        if (shouldRelay)
                        {
                            await RelayFileMessageToSnapshotAsync(
                                message,
                                relayContext!,
                                cancellationToken);
                            downstreamAcknowledgement = await WaitForRelayAcknowledgementsAsync(
                                relayContext!,
                                cancellationToken);
                            relayDeliveryCompleted = downstreamAcknowledgement.IsSuccess;
                            if (!relayDeliveryCompleted)
                            {
                                await AbortRelayFileTransferAsync(relayContext!);
                            }
                        }

                        ChatMessage acknowledgement = FileTransferService.CreateAcknowledgement(
                            message,
                            LocalPeerId,
                            Environment.MachineName,
                            GetLocalShortSessionId(),
                            success: downstreamAcknowledgement?.IsSuccess != false,
                            errorMessage: downstreamAcknowledgement?.IsSuccess == false
                                ? "いずれかの中継先でファイル受信を完了できませんでした。"
                                : null);
                        await sourceConnection.SendAsync(acknowledgement, cancellationToken);
                        break;
                    case "file_abort":
                        await _fileTransferService.HandleFileAbortAsync(
                            message,
                            transferSourceId,
                            cancellationToken);
                        if (shouldRelay)
                        {
                            await RelayFileMessageToSnapshotAsync(
                                message,
                                relayContext!,
                                cancellationToken);
                            relayDeliveryCompleted = true;
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CleanupFailedIncomingFileMessageAsync(
                    message,
                    sourceConnection,
                    transferSourceId,
                    relayContext,
                    relayDeliveryCompleted);
            }
            catch (Exception ex)
            {
                await CleanupFailedIncomingFileMessageAsync(
                    message,
                    sourceConnection,
                    transferSourceId,
                    relayContext,
                    relayDeliveryCompleted);

                if (isFileEnd)
                {
                    try
                    {
                        ChatMessage acknowledgement = FileTransferService.CreateAcknowledgement(
                            message,
                            LocalPeerId,
                            Environment.MachineName,
                            GetLocalShortSessionId(),
                            success: false,
                            errorMessage: GetRemoteFileFailureMessage(ex));
                        await sourceConnection.SendAsync(acknowledgement, cancellationToken);
                    }
                    catch (Exception acknowledgementError)
                    {
                        EnqueueLog($"ファイル失敗ACKの送信に失敗しました: {acknowledgementError.Message}", LogLevel.Error);
                    }
                }
                EnqueueLog($"ファイル受信処理に失敗しました: {ex.Message}", LogLevel.Error);
                if (ex is System.IO.InvalidDataException or FormatException)
                {
                    sourceConnection.Close();
                }
            }
            finally
            {
                if (isFileTerminal && relayContext != null)
                {
                    RemoveRelayFileTransferContext(relayContext);
                }
                if (isFileTerminal && !string.IsNullOrWhiteSpace(transferSourceId))
                {
                    UntrackIncomingTransfer(
                        sourceConnection,
                        message.FileId,
                        transferSourceId);
                }
            }
        }

        private void EnqueueCompletedIncomingFile(
            ChatMessage message,
            ChatConnection sourceConnection,
            FileTransferDisplayResult displayResult)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
                }

                string conversationId = GetConversationIdForMessage(message, sourceConnection);
                message.ConversationId = conversationId;
                string displayFileName = SanitizeUntrustedDisplayText(displayResult.FileName, 180);
                AddFileChatMessage(
                    $"受信完了: {displayFileName}",
                    displayResult.FileName,
                    displayResult.LocalFilePath,
                    conversationId,
                    isMine: false,
                    senderName: message.SenderName,
                    messageId: message.MessageId);

                var historyMessage = new ChatMessage
                {
                    MessageId = message.MessageId,
                    Type = "chat",
                    SenderId = message.SenderId,
                    SenderName = message.SenderName,
                    ShortSessionId = message.ShortSessionId,
                    Body = $"[ファイル] {displayFileName}",
                    IsGroup = message.IsGroup,
                    ConversationId = conversationId
                };

                SaveChatMessageSafely(
                    historyMessage,
                    false,
                    FindPeerForConnection(sourceConnection),
                    sourceConnection,
                    new ChatHistoryAttachment
                    {
                        FileId = displayResult.FileId,
                        FileName = displayResult.FileName,
                        LocalFilePath = displayResult.LocalFilePath,
                        FileSize = displayResult.FileSize
                    });
            });
        }

        private static string GetRemoteFileFailureMessage(Exception exception) => exception switch
        {
            UnauthorizedAccessException => "受信先でファイルへのアクセスが拒否されました。",
            System.IO.InvalidDataException or FormatException => "ファイル転送データが不正です。",
            System.IO.IOException => "受信先でファイルを保存できませんでした。",
            _ => "受信先でファイル転送を完了できませんでした。"
        };

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
                await connection.SendAsync(hello, _windowLifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog("TCP HELLO送信失敗", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private async System.Threading.Tasks.Task HandleHelloMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            using CancellationTokenSource helloCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    sourceConnection.LifetimeToken,
                    _windowLifetimeCancellation.Token);
            CancellationToken cancellationToken = helloCancellation.Token;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string shortSessionId = message.ShortSessionId ?? "";
            AddLog("SelectedItemには依存せずHELLO判定します", LogLevel.Debug);
            AddLog($"TCP HELLO受信: shortSessionId={shortSessionId}");

            if (string.IsNullOrWhiteSpace(message.SenderId) ||
                string.IsNullOrWhiteSpace(message.SenderName) ||
                string.IsNullOrWhiteSpace(shortSessionId))
            {
                AddLog("HELLO拒否: 必須のPeer ID、表示名、Session IDがありません。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }

            // A remote transport must never be allowed to claim this installation's
            // stable identity. ChatConnection performs the same check once our
            // outbound HELLO has populated its local binding, but an inbound HELLO
            // can win that race. Enforce the invariant here with the already-loaded
            // local identity before any discovery candidate is selected or pinned.
            if (string.Equals(
                    message.SenderId.Trim(),
                    LocalPeerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddLog("HELLO拒否: 接続先がローカルPeer IDを名乗っています。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }

            AddLog($"PeerごとのHELLO確認開始: {message.SenderName} / {sourceConnection.RemoteIpAddress}");

            PeerInfo? matchedPeer = FindPeerForHello(message, sourceConnection);
            if (matchedPeer == null)
            {
                AddLog("HELLO拒否: 探索または手動接続で承認されたPeerと照合できません。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }
            string provisionalConversationId = GetConversationIdForPeer(matchedPeer);

            string bindError = "HELLO identity does not match the discovered peer.";
            if (IsHelloMismatch(matchedPeer, message.SenderId, shortSessionId) ||
                !sourceConnection.TryStageRemoteIdentity(message, out bindError))
            {
                matchedPeer.StatusText = "HELLO不一致";
                matchedPeer.IsHelloVerified = false;
                matchedPeer.IsChatReady = false;
                sourceConnection.IsReady = false;
                RefreshPeerDisplay(matchedPeer);
                AddLog($"HELLO確認失敗: {bindError}", LogLevel.Error);
                UpdateSendButtonState();
                sourceConnection.Close();
                return;
            }

            if (!await ApproveAndPinRemoteIdentityAsync(
                    matchedPeer,
                    message,
                    sourceConnection,
                    cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                matchedPeer.StatusText = "暗号ID不一致";
                matchedPeer.IsHelloVerified = false;
                matchedPeer.IsChatReady = false;
                RefreshPeerDisplay(matchedPeer);
                AddLog("HELLO拒否: 暗号IDが未承認または保存済みpinと不一致です。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }

            if (cancellationToken.IsCancellationRequested) return;
            if (!sourceConnection.CompleteHelloVerification())
            {
                matchedPeer.StatusText = "HELLO検証失敗";
                RefreshPeerDisplay(matchedPeer);
                AddLog("HELLO拒否: 検証状態を確定できませんでした。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }

            PeerInfo? canonicalPeer = PromoteVerifiedHelloPeer(
                matchedPeer,
                message.SenderId,
                provisionalConversationId);
            if (canonicalPeer == null)
            {
                AddLog("HELLO拒否: 安定したPeer IDを既存の探索情報へ安全に統合できませんでした。", LogLevel.Error);
                sourceConnection.Close();
                return;
            }
            matchedPeer = canonicalPeer;

            cancellationToken.ThrowIfCancellationRequested();
            ApplyHelloToPeer(matchedPeer, message, sourceConnection);
            AddLog($"HELLO確認後にPeerを確定紐付け: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog($"ChatConnectionとPeerInfoを紐付けました: {matchedPeer.DisplayName}");
            AddLog($"PeerごとのHELLO確認成功: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog("HELLO確認成功: BLE Peerと接続先が一致", LogLevel.Success);
            AddLog("HELLO確認後、チャット準備完了", LogLevel.Success);
            UpdateSendButtonState();
            await SendPingAfterHelloAsync(sourceConnection);
        }

        private PeerInfo? PromoteVerifiedHelloPeer(
            PeerInfo candidate,
            string stablePeerId,
            string provisionalConversationId)
        {
            PeerIdentityPromotionResult promotion;
            try
            {
                promotion = _peerRegistryService.PromoteVerifiedStableIdentity(
                    candidate,
                    stablePeerId);
            }
            catch (Exception ex)
            {
                AddLog($"Peer ID統合に失敗しました: {ex.Message}", LogLevel.Error);
                return null;
            }

            if (promotion.IsConflict)
            {
                return null;
            }

            PeerInfo canonicalPeer = promotion.CanonicalPeer;
            bool restoreSelection = ReferenceEquals(PeerList.SelectedItem, candidate) ||
                promotion.RemovedPeers.Any(peer => ReferenceEquals(PeerList.SelectedItem, peer));
            foreach (PeerInfo removedPeer in promotion.RemovedPeers)
            {
                PeerList.Items.Remove(removedPeer);
            }

            if (!PeerList.Items.Contains(canonicalPeer))
            {
                PeerList.Items.Add(canonicalPeer);
            }

            MigrateConversation(provisionalConversationId, stablePeerId);
            if (restoreSelection)
            {
                PeerList.SelectedItem = canonicalPeer;
            }

            UpdatePeerCount();
            return canonicalPeer;
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
            string expectedSessionId = !string.IsNullOrWhiteSpace(sourceConnection.ExpectedRemoteShortSessionId)
                ? sourceConnection.ExpectedRemoteShortSessionId
                : sourceConnection.ShortSessionId;
            if (!string.IsNullOrWhiteSpace(expectedSessionId) &&
                string.Equals(expectedSessionId, message.ShortSessionId, StringComparison.OrdinalIgnoreCase))
            {
                PeerInfo? currentDiscoveryCandidate = FindPeerByShortSessionId(message.ShortSessionId);
                if (currentDiscoveryCandidate != null)
                {
                    return currentDiscoveryCandidate;
                }
            }

            return _peerRegistryService.FindForHello(message, sourceConnection);
        }

        private static bool IsHelloMismatch(PeerInfo peer, string peerId, string shortSessionId)
        {
            bool peerIdMismatch = !string.IsNullOrWhiteSpace(peer.PeerId) &&
                                  !string.Equals(peer.PeerId, peerId, StringComparison.OrdinalIgnoreCase);
            bool sessionMismatch = !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                                   !string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase);
            return peerIdMismatch || sessionMismatch;
        }

        private void ApplyHelloToPeer(PeerInfo peer, ChatMessage message, ChatConnection sourceConnection)
        {
            string previousConversationId = GetConversationIdForPeer(peer);
            if (!string.IsNullOrWhiteSpace(message.SenderName))
            {
                peer.DisplayName = message.SenderName;
            }

            if (!string.IsNullOrWhiteSpace(message.ShortSessionId))
            {
                peer.ShortSessionId = message.ShortSessionId;
            }

            peer.PeerId = message.SenderId;
            string canonicalConversationId = GetConversationIdForPeer(peer);
            MigrateConversation(previousConversationId, canonicalConversationId);
            peer.RemoteIpAddress = sourceConnection.RemoteIpAddress;
            peer.IsTcpConnected = sourceConnection.IsConnected;
            peer.IsHelloVerified = true;
            peer.IsChatReady = sourceConnection.IsConnected && sourceConnection.IsReceiveLoopStarted;
            peer.StatusText = peer.IsChatReady ? "チャット準備完了" : "HELLO確認中";
            sourceConnection.IsReady = peer.IsChatReady;
            sourceConnection.ShortSessionId = peer.ShortSessionId;
            RefreshPeerDisplay(peer);
            if (ReferenceEquals(PeerList.SelectedItem, peer))
            {
                SwitchConversation(peer);
            }
            AddLog($"PeerごとのTCP接続状態を更新: {peer.DisplayName}, Tcp={peer.IsTcpConnected}, Hello={peer.IsHelloVerified}, Ready={peer.IsChatReady}");
        }

        private void OnChatConnectionsChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
                }

                AddLog($"接続中Peer数: {_chatConnectionManager.ConnectedCount}");
                UpdatePeerCount();
                if (PeerList.SelectedItem is PeerInfo { IsGroupChat: true } groupPeer)
                {
                    UpdateSelectedPeerDetails(groupPeer);
                }
                UpdateSendButtonState();
            });
        }

        private void OnChatConnectionDisconnected(ChatConnection connection)
        {
            HandleFileAcknowledgementConnectionDisconnected(connection);
            _fileProcessingGates.TryRemove(connection, out _);
            AbortIncomingTransfersForDisconnectedConnection(connection);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) != 0)
                {
                    return;
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
                ?? FindPeerByRemoteIpOrName("", connectedPeer.DisplayName)
                ?? FindPeerByPeerId(PeerIdentityService.GetConnectionId(connectedPeer));
        }

        private async System.Threading.Tasks.Task ReconnectPeerAsync(PeerInfo peer)
        {
            if (!EnsureLocalIdentityReadyForNetworking()) return;

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

                if (!string.IsNullOrWhiteSpace(peer.DeviceId) &&
                    !peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"再接続中: Peer={peer.DisplayName}");
                    AddLog($"再接続処理を開始しました: Peer={peer.DisplayName}");
                    peer.IsConnected = false;
                    peer.IsTcpConnected = false;
                    peer.IsHelloVerified = false;
                    peer.IsChatReady = false;
                    bool wifiConnected = await _manager.ConnectAsync(peer);
                    RefreshPeerDisplay(peer);
                    if (wifiConnected)
                    {
                        return;
                    }

                    peer.StatusText = "Wi-Fi Direct再接続失敗";
                    AddLog($"Wi-Fi Direct再接続に失敗しました: Peer={peer.DisplayName}", LogLevel.Error);
                }

                if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    AddLog($"再接続中: Peer={peer.DisplayName}");
                    AddLog($"再接続処理を開始しました: Peer={peer.DisplayName}");

                    await PrepareChatTcpConnectionAsync(peer, "再接続中");

                    if (!peer.IsTcpConnected)
                    {
                        peer.StatusText = "再接続失敗";
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
            if (!EnsureLocalIdentityReadyForNetworking()) return;

            if (_tcpServer.IsStarted)
            {
                AddLog($"TCP待ち受けは開始済みです: Reason={reason}", LogLevel.Debug);
                return;
            }

            AddLog($"TCP待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
            await _tcpServer.StartAsync(LocalTcpPort, _windowLifetimeCancellation.Token);
        }

        private static int GetPeerTcpPort(PeerInfo peer)
            => peer.TcpPort is > 0 and <= 65535 ? peer.TcpPort : LocalTcpPort;

        private static bool TryNormalizeRemoteIpAddress(
            string? value,
            out string normalizedAddress,
            out string authorizationKey)
        {
            normalizedAddress = "";
            authorizationKey = "";
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string candidate = value.Trim();
            if (candidate.StartsWith("[", StringComparison.Ordinal))
            {
                int closingBracket = candidate.IndexOf(']');
                if (closingBracket != candidate.Length - 1)
                {
                    return false;
                }
                candidate = candidate[1..closingBracket];
            }

            if (!IPAddress.TryParse(candidate, out IPAddress? address))
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            byte[] addressBytes = address.GetAddressBytes();
            normalizedAddress = address.ToString();
            // Textual IPv6 formatting is normalized to address bytes. Scope is part
            // of link-local interface identity, so IPv6 authorization also requires
            // the exact canonical ScopeId observed for the Wi-Fi Direct session.
            authorizationKey = addressBytes.Length == 4
                ? $"4:{Convert.ToHexString(addressBytes)}"
                : $"6:{Convert.ToHexString(addressBytes)}:S{address.ScopeId}";
            return true;
        }

        private void PruneExpiredTcpAuthorizations(DateTimeOffset now)
        {
            foreach (KeyValuePair<string, PendingTcpAuthorization> entry in _pendingTcpAuthorizations)
            {
                if (entry.Value.ExpiresAtUtc < now)
                {
                    _pendingTcpAuthorizations.TryRemove(entry.Key, out _);
                }
            }
        }

        private readonly record struct PendingTcpAuthorization(
            PeerInfo Peer,
            DateTimeOffset ExpiresAtUtc);

        private void SetChatReady(bool isReady)
        {
            if (!isReady)
            {
                SendMessageButton.IsEnabled = false;
                AttachFileButton.IsEnabled = false;
                return;
            }

            UpdateSendButtonState();
        }


        private ChatConnection? GetConnectionForPeer(PeerInfo peer)
        {
            return _chatConnectionManager.FindForPeer(peer);
        }

        private PeerInfo AddOrMergePeer(PeerInfo incoming)
        {
            PeerRegistrationResult registration = _peerRegistryService.Register(incoming);
            PeerInfo registeredPeer = registration.Peer;

            if (registration.Kind == PeerRegistrationKind.IgnoredPendingRequest)
            {
                AddLog($"PendingRequestはPeerListに追加しません: {incoming.DisplayName}", LogLevel.Debug);
                return registeredPeer;
            }

            _peerConnectionStateService.UpdateConnectAvailability(registeredPeer);
            if (registration.CollectionChanged)
            {
                if (!PeerList.Items.Contains(registeredPeer))
                {
                    PeerList.Items.Add(registeredPeer);
                }
                AddLog($"Peer追加: {registeredPeer.DisplayText}");
            }
            else
            {
                AddLog($"Peer統合: {registration.Kind} {registration.MatchReason} -> {registeredPeer.DisplayText}", LogLevel.Success);
            }

            RefreshPeerDisplay(registeredPeer);
            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
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

            var peer = new PeerInfo
            {
                DisplayName = displayName,
                RemoteIpAddress = connection.RemoteIpAddress,
                TcpPort = LocalTcpPort,
                IsTcpConnected = connection.IsConnected,
                IsChatReady = false,
                StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可"
            };

            AddOrMergePeer(peer);
        }

    }
}
