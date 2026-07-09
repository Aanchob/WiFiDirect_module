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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly ChatConnectionManager _chatConnectionManager = new();
        private readonly List<string> _logLines = new();
        private readonly Guid _localSessionId = Guid.NewGuid();
        private readonly DatabaseService? _databaseService = null;
        private readonly ChatHistoryService? _chatHistoryService = null;
        private readonly ConnectionRoleService _connectionRoleService;
        private readonly PeerConnectionStateService _peerConnectionStateService;
        private bool _isAutonomousGoAdvertisementEnabled;
        private readonly Dictionary<string, IncomingFileSession> _incomingFiles = new();

        private class IncomingFileSession
        {
            public string FileId { get; set; } = "";
            public string FileName { get; set; } = "";
            public long FileSize { get; set; }
            public string MimeType { get; set; } = "";
            public string PartFilePath { get; set; } = "";
            public int LastChunkIndex { get; set; } = -1;
            public ChatMessageViewModel? ViewModel { get; set; }
        }

        private ChatRole _chatRole = ChatRole.Client;

        public MainWindow()
        {
            InitializeComponent();
            MessageTextBox.Paste += MessageTextBox_Paste;
            _connectionRoleService = new ConnectionRoleService(GetLocalShortSessionId(), GetLocalRoleKey());
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
            Closed += MainWindow_Closed;

            // グループチャット用疑似Peerの追加
            var groupPeer = new PeerInfo
            {
                DisplayName = "グループ",
                IsGroupChat = true,
                CanConnect = false,
                StatusText = "グループチャット"
            };
            PeerList.Items.Add(groupPeer);

            SetChatReady(false);
            UpdatePeerCount();
            UpdateSelectedPeerDetails(null);
        }

        private string LocalPeerId => _localSessionId.ToString("N");

        private string GetLocalShortSessionId()
        {
            return _localSessionId.ToString("N")[..4];
        }

        private string GetLocalRoleKey()
        {
            return _localSessionId.ToString("N")[..8];
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _chatConnectionManager.StopKeepAlive();
            _manager.Stop();
        }

        private void ResizeWindow(int width, int height)
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(width, height));
        }

        private async void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            AddLog("相手探索開始");
            AddLog($"Local ShortSessionId: {GetLocalShortSessionId()}");
            AddLog($"Local RoleKey: {GetLocalRoleKey()}");
            _connectionRoleService.ResetBleNegotiation();

            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            StartBleAdvertiseCore();
            _discoveryManager.StartScan();

            ClearStaleWiFiDirectPeers();
            AddLog("BLE RoleKey判定後にClient側だけWi-Fi Direct探索を開始します");

            AddLog("相手探索処理を開始しました");
        }

        private async void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _chatRole = ChatRole.Host;
            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            AddLog("Wi-Fi Direct広告+待ち受け開始ボタンを押しました");
            AddLog("Chat Role: Host");
            await EnsureTcpServerStartedAsync("Wi-Fi Direct広告+待ち受け開始");
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("AssociationEndpoint探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();
        }

        private async void SearchDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("通常Wi-Fi Direct探索ボタンを押しました");
            ClearStaleWiFiDirectPeers();
            await _manager.StartDefaultScanAsync();
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

        private async void StartTcpServer_Click(object sender, RoutedEventArgs e)
        {
            _chatRole = ChatRole.Host;
            AddLog("Chat Role: Host");
            await EnsureTcpServerStartedAsync("手動操作");
        }

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

                bool isGroup = false;
                string conversationId = "";
                PeerInfo? peer = null;
                ChatConnection? connection = null;

                if (PeerList.SelectedItem is PeerInfo selectedPeer)
                {
                    if (selectedPeer.IsGroupChat)
                    {
                        isGroup = true;
                        conversationId = "group";
                    }
                    else
                    {
                        isGroup = false;
                        conversationId = PeerIdentityService.GetConnectionId(selectedPeer);
                        peer = selectedPeer;
                        connection = GetConnectionForPeer(selectedPeer);
                    }
                }
                else
                {
                    if (_chatRole == ChatRole.Host && _chatConnectionManager.ConnectedCount > 0)
                    {
                        isGroup = true;
                        conversationId = "group";
                    }
                    else
                    {
                        AddLog("送信先Peerが選択されていません", LogLevel.Error);
                        return;
                    }
                }

                var message = new ChatMessage
                {
                    Type = "chat",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = body,
                    IsGroup = isGroup,
                    ConversationId = conversationId
                };

                _chatConnectionManager.MarkMessageSeen(message.MessageId);

                if (isGroup)
                {
                    if (_chatRole == ChatRole.Host)
                    {
                        AddLog($"Hostグループ送信: 接続中ClientへBroadcast Count={_chatConnectionManager.ConnectedCount}");
                        await _chatConnectionManager.BroadcastAsync(message);
                    }
                    else
                    {
                        var hostConn = _chatConnectionManager.Connections.FirstOrDefault(c => c.IsConnected && c.IsReady);
                        if (hostConn != null)
                        {
                            AddLog("Clientグループ送信: Hostへ送信");
                            await hostConn.SendAsync(message);
                        }
                        else
                        {
                            AddLog("グループ送信失敗: Hostへの接続がありません", LogLevel.Error);
                            return;
                        }
                    }

                    var viewModel = CreateViewModelFromNetworkMessage(message, true);
                    AddChatMessage(viewModel);
                    SaveChatMessageSafely(message, true, null, null);
                }
                else
                {
                    if (peer == null || !PeerConnectionStateService.IsChatReady(peer))
                    {
                        AddLog("送信先Peerがチャット準備完了ではありません", LogLevel.Error);
                        UpdateSendButtonState();
                        return;
                    }

                    if (connection == null || !connection.IsConnected || !connection.IsReady)
                    {
                        AddLog("選択中PeerのChatConnectionが見つかりません", LogLevel.Error);
                        UpdateSendButtonState();
                        return;
                    }

                    await connection.SendAsync(message);

                    var viewModel = CreateViewModelFromNetworkMessage(message, true);
                    AddChatMessage(viewModel);
                    SaveChatMessageSafely(message, true, peer, connection);
                }

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

        private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください", LogLevel.Error);
                return;
            }

            if (peer.CanDisconnect)
            {
                await DisconnectPeerAsync(peer);
            }
            else
            {
                await ConnectPeerAsync(peer);
            }
        }

        private async void ConnectPeerItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PeerInfo peer)
            {
                AddLog("接続対象Peerを取得できませんでした", LogLevel.Error);
                return;
            }

            PeerList.SelectedItem = peer;
            if (peer.CanDisconnect)
            {
                await DisconnectPeerAsync(peer);
            }
            else
            {
                await ConnectPeerAsync(peer);
            }
        }

        private async System.Threading.Tasks.Task ConnectPeerAsync(PeerInfo peer)
        {
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

            _manager.StopScan();
            await System.Threading.Tasks.Task.Delay(WiFiDirectScanRestartDelay);

            ClearWiFiDirectCandidateForPreConnect(peer);

            AddLog($"GO再広告待機: {WiFiDirectGoAdvertisementWait.TotalSeconds:0.0}秒");
            await System.Threading.Tasks.Task.Delay(WiFiDirectGoAdvertisementWait);

            AddLog("接続前Wi-Fi Direct再スキャン開始");
            await _manager.StartAssociationEndpointScanAsync();

            if (await WaitForWiFiDirectCandidateAsync(peer, WiFiDirectCandidateRefreshTimeout))
            {
                AddLog($"Wi-Fi Direct候補再取得: Peer={peer.DisplayName}, DeviceId={peer.DeviceId}", LogLevel.Success);
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

                await System.Threading.Tasks.Task.Delay(WiFiDirectCandidatePollInterval);
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

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogTextBox.Text = string.Empty;
        }

        private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("再接続対象Peerが選択されていません", LogLevel.Error);
                return;
            }

            await ReconnectPeerAsync(peer);
        }

        private void ScrollLogBottom_Click(object sender, RoutedEventArgs e)
        {
            MoveLogCaretToEnd();
        }

        private void OnPeerFound(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                AddOrMergePeer(peer);

                if (peer.DiscoveredByBle)
                {
                    await HandleBleRoleNegotiationAsync(peer);
                }
            });
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
            if (_isAutonomousGoAdvertisementEnabled)
            {
                _manager.RestartAdvertisement(
                    Environment.MachineName,
                    GetLocalShortSessionId(),
                    autonomousGroupOwner: false);
                _isAutonomousGoAdvertisementEnabled = false;
            }

            await System.Threading.Tasks.Task.Delay(1500);
            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();
            AddLog("BLE RoleKey判定後にClient側だけWi-Fi Direct探索を開始します");
        }

        private void OnConnectionRequested(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _chatRole = ChatRole.Host;
                AddLog($"接続要求: {peer.DisplayName}");
                AddLog("Chat Role: Host");
                _ = EnsureTcpServerStartedAsync("Wi-Fi Direct接続要求受信");
            });
        }

        private void OnWiFiDirectConnected(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                peer.IsConnectingWiFiDirect = false;
                if (IsTransientWiFiDirectStatus(peer.StatusText))
                {
                    peer.StatusText = "";
                }

                AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}", LogLevel.Success);
                AddOrMergePeer(peer);
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
            });
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            DispatcherQueue.TryEnqueue(async () =>
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
                }
            });
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
                AddLog($"接続先Port: {LocalTcpPort}");

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

        private async void OnChatMessageReceived(ChatMessage message, ChatConnection sourceConnection)
        {
            string messageType = string.IsNullOrWhiteSpace(message.Type)
                ? "chat"
                : message.Type;

            switch (messageType.ToLowerInvariant())
            {
                case "hello":
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await HandleHelloMessageAsync(message, sourceConnection);
                    });
                    return;

                case "chat":
                    break;

                case "ping":
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await HandlePingMessageAsync(message, sourceConnection);
                    });
                    return;

                case "pong":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HandlePongMessage(message, sourceConnection);
                    });
                    return;

                case "file_start":
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await HandleFileStartAsync(message, sourceConnection);
                    });
                    break;

                case "file_chunk":
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await HandleFileChunkAsync(message, sourceConnection);
                    });
                    break;

                case "file_end":
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        await HandleFileEndAsync(message, sourceConnection);
                    });
                    break;

                case "system":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddLog($"システムメッセージを受信: {message.Body}");
                    });
                    return;

                default:
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddLog($"不明なChatMessage Typeを受信: {messageType}", LogLevel.Error);
                    });
                    return;
            }

            if (messageType.ToLowerInvariant() == "chat")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    bool shouldAddToUi = false;
                    if (PeerList.SelectedItem is PeerInfo activePeer)
                    {
                        if (message.IsGroup && activePeer.IsGroupChat)
                        {
                            shouldAddToUi = true;
                        }
                        else if (!message.IsGroup && !activePeer.IsGroupChat)
                        {
                            string activeId = PeerIdentityService.GetConnectionId(activePeer);
                            if (string.Equals(activeId, message.ConversationId, StringComparison.OrdinalIgnoreCase))
                            {
                                shouldAddToUi = true;
                            }
                        }
                    }

                    if (shouldAddToUi)
                    {
                        AddChatMessage(CreateViewModelFromNetworkMessage(message, false));
                    }

                    SaveChatMessageSafely(message, false, FindPeerForConnection(sourceConnection), sourceConnection);
                    AddLog($"TCP受信メッセージ: {message.Body}", LogLevel.Success);
                    AddConnectedPeerDisplay(sourceConnection);
                });
            }

            if (_chatRole == ChatRole.Host && message.IsGroup)
            {
                AddLog($"Host転送開始: From={message.SenderName}, MessageId={message.MessageId}");
                await _chatConnectionManager.BroadcastExceptAsync(message, sourceConnection);
                AddLog("Host転送完了");
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

        private async System.Threading.Tasks.Task HandlePingMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            try
            {
                AddLog($"PING受信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);

                var pong = new ChatMessage
                {
                    Type = "pong",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = ""
                };

                await sourceConnection.SendAsync(pong);
                AddLog($"PONG送信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                AddLog($"PONG送信失敗: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private void HandlePongMessage(ChatMessage message, ChatConnection sourceConnection)
        {
            sourceConnection.LastPongAt = DateTime.Now;
            sourceConnection.LastResponseAt = sourceConnection.LastPongAt;
            sourceConnection.IsPingWaiting = false;

            AddLog($"PONG受信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);
            AddLog($"接続確認成功: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Success);
        }

        private async System.Threading.Tasks.Task SendPingAfterHelloAsync(ChatConnection connection)
        {
            try
            {
                AddLog($"PING送信: Peer={GetConnectionPeerName(connection)}", LogLevel.Debug);
                await connection.SendPingAsync(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            }
            catch (Exception ex)
            {
                connection.IsPingWaiting = false;
                AddLog($"PING送信失敗: Peer={GetConnectionPeerName(connection)}", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private static string GetConnectionPeerName(ChatConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.PeerName))
            {
                return connection.PeerName;
            }

            if (!string.IsNullOrWhiteSpace(connection.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return connection.PeerId;
        }

        private async System.Threading.Tasks.Task HandleHelloMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            string shortSessionId = message.ShortSessionId ?? "";
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

            PeerInfo? matchedPeer = FindPeerForHello(message, sourceConnection);
            if (matchedPeer == null)
            {
                AddLog("HELLOのShortSessionIdに一致するPeerInfoが見つかりません", LogLevel.Debug);
                matchedPeer = new PeerInfo
                {
                    DisplayName = string.IsNullOrWhiteSpace(message.SenderName)
                        ? sourceConnection.RemoteIpAddress
                        : message.SenderName,
                    RemoteIpAddress = sourceConnection.RemoteIpAddress
                };
                _peerConnectionStateService.UpdateConnectAvailability(matchedPeer);
                PeerList.Items.Add(matchedPeer);
                AddLog("BLE情報なしのためHELLO情報でPeerInfoを更新");
            }

            if (IsHelloMismatch(matchedPeer, shortSessionId))
            {
                matchedPeer.StatusText = "HELLO不一致";
                matchedPeer.IsHelloVerified = false;
                matchedPeer.IsChatReady = false;
                sourceConnection.IsHelloVerified = false;
                sourceConnection.IsReady = false;
                RefreshPeerDisplay(matchedPeer);
                AddLog("PeerごとのHELLO確認失敗: ShortSessionId不一致", LogLevel.Error);
                AddLog("HELLO確認失敗: BLE Peerと接続先が不一致", LogLevel.Error);
                UpdateSendButtonState();
                sourceConnection.Close();
                return;
            }

            ApplyHelloToPeer(matchedPeer, message, sourceConnection);
            AddLog($"HELLO確認後にPeerを確定紐付け: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog($"ChatConnectionとPeerInfoを紐付けました: {matchedPeer.DisplayName}");
            AddLog($"PeerごとのHELLO確認成功: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog("HELLO確認成功: BLE Peerと接続先が一致", LogLevel.Success);
            AddLog("HELLO確認後、チャット準備完了", LogLevel.Success);
            UpdateSendButtonState();
            await SendPingAfterHelloAsync(sourceConnection);
        }

        private PeerInfo? FindPeerByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId))
            {
                return null;
            }

            return PeerList.Items
                .Cast<PeerInfo>()
                .FirstOrDefault(peer =>
                    string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase));
        }

        private PeerInfo? FindPeerByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            return PeerList.Items
                .Cast<PeerInfo>()
                .FirstOrDefault(peer =>
                    (!string.IsNullOrWhiteSpace(remoteIpAddress) &&
                     string.Equals(peer.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(displayName) &&
                     string.Equals(peer.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));
        }

        private PeerInfo? FindPeerByPeerId(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId))
            {
                return null;
            }

            return PeerList.Items
                .Cast<PeerInfo>()
                .FirstOrDefault(peer =>
                    string.Equals(PeerIdentityService.GetConnectionId(peer), peerId, StringComparison.OrdinalIgnoreCase));
        }

        private PeerInfo? FindPeerForHello(ChatMessage message, ChatConnection sourceConnection)
        {
            return FindPeerByShortSessionId(message.ShortSessionId)
                ?? FindPeerByRemoteIpOrName(sourceConnection.RemoteIpAddress, "")
                ?? FindPeerByPeerId(sourceConnection.PeerId)
                ?? FindPeerByRemoteIpOrName("", message.SenderName);
        }

        private static bool IsHelloMismatch(PeerInfo peer, string shortSessionId)
        {
            return !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                   !string.IsNullOrWhiteSpace(shortSessionId) &&
                   !string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase);
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

            peer.RemoteIpAddress = sourceConnection.RemoteIpAddress;
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
            return FindPeerByPeerId(connection.PeerId)
                ?? FindPeerByShortSessionId(connection.ShortSessionId)
                ?? FindPeerByRemoteIpOrName(connection.RemoteIpAddress, "")
                ?? FindPeerByRemoteIpOrName("", connection.PeerName);
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

                if (!string.IsNullOrWhiteSpace(peer.DeviceId) &&
                    !peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
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
            if (_tcpServer.IsStarted)
            {
                AddLog($"TCP待ち受けは開始済みです: Reason={reason}", LogLevel.Debug);
                return;
            }

            AddLog($"TCP待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
            await _tcpServer.StartAsync(LocalTcpPort);
        }

        private void SetChatReady(bool isReady)
        {
            UpdateSendButtonState();
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            UpdateSendButtonState();
            LoadChatHistory();
        }

        private void UpdatePeerCount()
        {
            PeerCountText.Text = $"検出済み {PeerList.Items.Count}";
        }

        private void UpdateSelectedPeerDetails(PeerInfo? peer)
        {
            if (peer is null)
            {
                ChatHeaderAvatarText.Text = "--";
                ChatHeaderTitleText.Text = "相手未選択";
                ChatHeaderStatusText.Text = "Peerを選択すると状態が表示されます";
                SelectedPeerAvatarText.Text = "--";
                SelectedPeerNameText.Text = "未選択";
                SelectedPeerStatusText.Text = "相手を選択してください";
                SelectedPeerSourceText.Text = "BLE / Wi-Fi Direct の検出状況がここに表示されます。";
                SelectedPeerProgress.Value = 10;
                SelectedPeerIpText.Text = "Remote IP: -";
                SelectedPeerSessionText.Text = "Session: -";
                SelectedPeerDeviceText.Text = "DeviceId: -";
                SelectedPeerOnlineDot.Fill = new SolidColorBrush(Colors.Gray);
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(peer.DisplayName)
                ? "Unknown Peer"
                : peer.DisplayName;
            string status = PeerDisplayService.GetStatusText(peer);

            ChatHeaderAvatarText.Text = PeerDisplayService.CreateInitials(displayName);
            ChatHeaderTitleText.Text = displayName;
            ChatHeaderStatusText.Text = status;
            SelectedPeerAvatarText.Text = PeerDisplayService.CreateInitials(displayName);
            SelectedPeerNameText.Text = displayName;
            SelectedPeerStatusText.Text = status;
            SelectedPeerSourceText.Text = $"{peer.SourceText} / TCP:{(peer.IsTcpConnected ? "接続済み" : peer.IsPreparingChatTcp ? "準備中" : "未接続")} / HELLO:{(peer.IsHelloVerified ? "確認済み" : "未確認")}";
            SelectedPeerProgress.Value = PeerDisplayService.GetProgressValue(peer);
            SelectedPeerIpText.Text = $"Remote IP: {PeerDisplayService.GetDisplayValue(peer.RemoteIpAddress)}";
            SelectedPeerSessionText.Text = $"Session: {PeerDisplayService.GetDisplayValue(peer.ShortSessionId)}";
            SelectedPeerDeviceText.Text = $"DeviceId: {PeerDisplayService.GetDisplayValue(peer.DeviceId)}";
            SelectedPeerOnlineDot.Fill = new SolidColorBrush(peer.IsChatReady ? Colors.LimeGreen : peer.IsTcpConnected ? Colors.DeepSkyBlue : Colors.Gray);
        }
        private void UpdateSendButtonState()
        {
            bool canSend = false;

            if (PeerList.SelectedItem is PeerInfo peer)
            {
                if (peer.IsGroupChat)
                {
                    canSend = _chatConnectionManager.ConnectedCount > 0;
                }
                else
                {
                    canSend = PeerConnectionStateService.IsChatReady(peer) && GetConnectionForPeer(peer)?.IsReady == true;
                }
            }
            else if (_chatRole == ChatRole.Host)
            {
                canSend = _chatConnectionManager.Connections.Any(connection => connection.IsConnected && connection.IsReady);
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                SendMessageButton.IsEnabled = canSend;
                AttachFileButton.IsEnabled = canSend;
            });

            AddLog(
                canSend
                    ? "選択中Peerの状態によりSendMessageButtonを有効化"
                    : "選択中Peerの状態によりSendMessageButtonを無効化",
                LogLevel.Debug);

            UpdateReconnectButtonState();
        }
        private void UpdateReconnectButtonState()
        {
            bool canReconnect = PeerList.SelectedItem is PeerInfo peer &&
                !peer.IsGroupChat &&
                _peerConnectionStateService.CanReconnect(peer);

            DispatcherQueue.TryEnqueue(() =>
            {
                ReconnectButton.IsEnabled = canReconnect;
            });
        }

        private ChatConnection? GetConnectionForPeer(PeerInfo peer)
        {
            if (peer.IsGroupChat) return null;
            return _chatConnectionManager.FindForPeer(peer);
        }

        private void AddOrMergePeer(PeerInfo incoming)
        {
            if (incoming.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"PendingRequestはPeerListに追加しません: {incoming.DisplayName}", LogLevel.Debug);
                return;
            }

            AddLog(
                $"Peer照合開始: Name={incoming.DisplayName}, ShortSessionId={incoming.ShortSessionId}, DeviceIdあり={!string.IsNullOrWhiteSpace(incoming.DeviceId)}",
                LogLevel.Debug);

            foreach (PeerInfo existing in PeerList.Items.Cast<PeerInfo>())
            {
                if (existing.IsGroupChat)
                {
                    continue;
                }
                string matchReason = PeerMergeService.GetMatchReason(existing, incoming);
                if (string.IsNullOrEmpty(matchReason))
                {
                    if (PeerMergeService.IsPartialNameMatchCandidate(existing, incoming))
                    {
                        if (IsBleWiFiDirectPartialNamePair(existing, incoming))
                        {
                            PeerMergeService.Merge(existing, incoming);
                            _peerConnectionStateService.UpdateConnectAvailability(existing);
                            RefreshPeerDisplay(existing);
                            AddLog($"Peer統合: BLE/Wi-Fi Direct部分名一致 -> {existing.DisplayText}", LogLevel.Success);
                            return;
                        }

                        AddLog($"Peer名の部分一致候補を検出しましたが、自動統合しません: {existing.DisplayName} / {incoming.DisplayName}", LogLevel.Debug);
                    }

                    continue;
                }

                PeerMergeService.Merge(existing, incoming);
                _peerConnectionStateService.UpdateConnectAvailability(existing);
                RefreshPeerDisplay(existing);

                LogLevel level = matchReason.StartsWith("注意:", StringComparison.Ordinal)
                    ? LogLevel.Error
                    : LogLevel.Success;

                AddLog($"Peer統合: {matchReason} -> {existing.DisplayText}", level);
                return;
            }

            var fallbackCandidates = PeerList.Items
                .Cast<PeerInfo>()
                .Where(existing => !existing.IsGroupChat && PeerMergeService.IsSingleCandidateFallback(existing, incoming))
                .ToList();

            if (fallbackCandidates.Count == 1)
            {
                PeerInfo existing = fallbackCandidates[0];
                PeerMergeService.Merge(existing, incoming);
                _peerConnectionStateService.UpdateConnectAvailability(existing);
                RefreshPeerDisplay(existing);

                AddLog($"Peer統合: 注意: 単一BLE/Wi-Fi Direct候補として統合 ({existing.DisplayName} / {incoming.DisplayName}) -> {existing.DisplayText}", LogLevel.Error);
                return;
            }

            if (fallbackCandidates.Count > 1)
            {
                AddLog($"Peer統合スキップ: BLE/Wi-Fi Direct候補が複数あるため自動統合しません Count={fallbackCandidates.Count}, Incoming={incoming.DisplayName}", LogLevel.Debug);
            }

            _peerConnectionStateService.UpdateConnectAvailability(incoming);
            PeerList.Items.Add(incoming);
            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            AddLog($"確実な照合キーがないため新規Peerとして追加: {incoming.DisplayName}", LogLevel.Debug);
            AddLog($"Peer追加: {incoming.DisplayText}");
        }

        private static bool IsBleWiFiDirectPartialNamePair(PeerInfo existing, PeerInfo incoming)
        {
            return (existing.DiscoveredByBle &&
                    !existing.DiscoveredByWiFiDirect &&
                    incoming.DiscoveredByWiFiDirect) ||
                   (incoming.DiscoveredByBle &&
                    !incoming.DiscoveredByWiFiDirect &&
                    existing.DiscoveredByWiFiDirect);
        }

        private void ClearStaleWiFiDirectPeers()
        {
            int removed = 0;
            var peers = PeerList.Items.Cast<PeerInfo>().ToList();

            foreach (PeerInfo peer in peers)
            {
                if (peer.IsGroupChat)
                {
                    continue;
                }
                if (!peer.DiscoveredByWiFiDirect || peer.IsConnected)
                {
                    continue;
                }

                if (peer.DiscoveredByBle)
                {
                    peer.DiscoveredByWiFiDirect = false;
                    peer.WiFiDirectName = "";
                    peer.DeviceId = "";
                    peer.DeviceKind = "";
                    peer.IsEnabled = null;
                    RefreshPeerDisplay(peer);
                }
                else
                {
                    PeerList.Items.Remove(peer);
                }

                removed++;
            }

            AddLog($"古いWi-Fi Direct候補を削除: {removed}件");
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

            foreach (PeerInfo existing in PeerList.Items.Cast<PeerInfo>())
            {
                if (existing.IsGroupChat)
                {
                    continue;
                }
                if (string.Equals(existing.RemoteIpAddress, connection.RemoteIpAddress, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    existing.IsTcpConnected = connection.IsConnected;
                    existing.IsChatReady = existing.IsChatReady && connection.IsConnected && connection.IsReceiveLoopStarted;
                    existing.StatusText = existing.IsChatReady ? "チャット準備完了" : connection.IsConnected ? "HELLO確認中" : "送信不可";
                    RefreshPeerDisplay(existing);
                    return;
                }
            }

            var peer = new PeerInfo
            {
                DisplayName = displayName,
                RemoteIpAddress = connection.RemoteIpAddress,
                IsTcpConnected = connection.IsConnected,
                IsChatReady = false,
                StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可"
            };

            _peerConnectionStateService.UpdateConnectAvailability(peer);
            PeerList.Items.Add(peer);
            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
        }

        private void RefreshPeerDisplay(PeerInfo peer)
        {
            _peerConnectionStateService.UpdateConnectAvailability(peer);

            int selectedIndex = PeerList.SelectedIndex;
            int index = PeerList.Items.IndexOf(peer);
            if (index < 0)
            {
                return;
            }

            PeerList.Items.RemoveAt(index);
            PeerList.Items.Insert(index, peer);

            if (selectedIndex >= 0 && selectedIndex < PeerList.Items.Count)
            {
                PeerList.SelectedIndex = selectedIndex;
            }

            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            UpdateSendButtonState();
        }

        private async System.Threading.Tasks.Task DisconnectPeerAsync(PeerInfo peer)
        {
            AddLog($"切断処理開始: Peer={peer.DisplayName}");
            try
            {
                // 1. ChatConnection を閉じる
                ChatConnection? connection = _chatConnectionManager.FindForPeer(peer);
                if (connection != null)
                {
                    connection.Close();
                    _chatConnectionManager.RemoveConnection(connection);
                    AddLog($"ChatConnectionを閉じました: Peer={peer.DisplayName}");
                }

                // 2. Wi-Fi Directセッションを破棄
                _manager.CloseSession(peer);

                // 3. Peer状態を更新
                peer.IsConnected = false;
                peer.IsTcpConnected = false;
                peer.IsHelloVerified = false;
                peer.IsChatReady = false;
                peer.StatusText = "切断";

                RefreshPeerDisplay(peer);
                AddLog($"切断処理完了: Peer={peer.DisplayName}", LogLevel.Success);
            }
            catch (Exception ex)
            {
                AddLog($"切断処理エラー: {ex.Message}", LogLevel.Error);
            }
        }

        private void AddChatMessage(ChatMessageViewModel message)
        {
            MessageList.Items.Add(message);
            MessageList.ScrollIntoView(message);
        }

        private ChatMessageViewModel CreateViewModelFromNetworkMessage(ChatMessage message, bool isMine)
        {
            return new ChatMessageViewModel
            {
                SenderName = message.SenderName,
                Body = message.Body,
                TimeText = message.SentAt.ToString("HH:mm"),
                IsMine = isMine,
                IsGroup = message.IsGroup,
                MessageType = "chat"
            };
        }

        private ChatMessageViewModel CreateViewModelFromDbMessage(direct_module.Models.ChatMessage message)
        {
            return new ChatMessageViewModel
            {
                SenderName = message.SenderName,
                Body = message.Message,
                TimeText = message.SendTime.ToString("HH:mm"),
                IsMine = message.IsMine,
                IsGroup = message.IsGroup,
                MessageType = message.MessageType,
                FileId = message.FileId ?? "",
                FileName = message.FileName ?? "",
                FileSize = message.FileSize,
                LocalFilePath = message.LocalFilePath ?? "",
                MimeType = message.MimeType ?? "",
                IsTransferring = false,
                ProgressValue = 100
            };
        }

        private void LoadChatHistory()
        {
            MessageList.Items.Clear();

            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                return;
            }

            string conversationId = peer.IsGroupChat ? "group" : PeerIdentityService.GetConnectionId(peer);
            
            if (_databaseService == null)
            {
                return;
            }

            try
            {
                var chatRepository = new ChatRepository(_databaseService);
                var messages = chatRepository.GetMessages(conversationId);

                foreach (var msg in messages)
                {
                    MessageList.Items.Add(CreateViewModelFromDbMessage(msg));
                }

                if (MessageList.Items.Count > 0)
                {
                    MessageList.ScrollIntoView(MessageList.Items[^1]);
                }
            }
            catch (Exception ex)
            {
                AddLog($"履歴読み込み失敗: {ex.Message}", LogLevel.Error);
            }
        }

        private async System.Threading.Tasks.Task SendFileAsync(string filePath, string fileName)
        {
            if (!System.IO.File.Exists(filePath))
            {
                AddLog($"ファイルが見つかりません: {filePath}", LogLevel.Error);
                return;
            }

            var fileInfo = new System.IO.FileInfo(filePath);
            const long MaxFileSize = 50 * 1024 * 1024; // 50MB limit
            if (fileInfo.Length > MaxFileSize)
            {
                AddLog($"送信不可: ファイルサイズが上限(50MB)を超えています ({fileInfo.Length / (1024.0 * 1024.0):F1}MB)", LogLevel.Error);
                return;
            }

            long fileSize = fileInfo.Length;
            string fileId = Guid.NewGuid().ToString("N");
            string mimeType = GetMimeType(fileName);
            string msgType = mimeType.StartsWith("image/") ? "image" : "file";

            AddLog($"添付送信開始: {fileName} ({fileSize / 1024.0:F1} KB)");

            bool isGroup = false;
            string conversationId = "";
            PeerInfo? peer = null;
            ChatConnection? connection = null;

            if (PeerList.SelectedItem is PeerInfo selectedPeer)
            {
                if (selectedPeer.IsGroupChat)
                {
                    isGroup = true;
                    conversationId = "group";
                }
                else
                {
                    isGroup = false;
                    conversationId = PeerIdentityService.GetConnectionId(selectedPeer);
                    peer = selectedPeer;
                    connection = GetConnectionForPeer(selectedPeer);
                }
            }

            var viewModel = new ChatMessageViewModel
            {
                SenderName = Environment.MachineName,
                Body = $"[添付ファイル] {fileName}",
                TimeText = DateTime.Now.ToString("HH:mm"),
                IsMine = true,
                IsGroup = isGroup,
                MessageType = msgType,
                FileId = fileId,
                FileName = fileName,
                FileSize = fileSize,
                LocalFilePath = filePath,
                MimeType = mimeType,
                IsTransferring = true,
                ProgressValue = 0,
                ProgressText = "準備中..."
            };

            AddChatMessage(viewModel);

            var dbMsg = new direct_module.Models.ChatMessage
            {
                ConversationId = conversationId,
                SenderId = LocalPeerId,
                SenderName = Environment.MachineName,
                ReceiverId = isGroup ? "group" : (peer != null ? PeerIdentityService.GetConnectionId(peer) : "unknown"),
                ReceiverName = isGroup ? "Group" : (peer != null ? peer.DisplayName : "Unknown"),
                Message = $"[添付ファイル] {fileName}",
                SendTime = DateTime.Now,
                IsMine = true,
                MessageType = msgType,
                FileId = fileId,
                FileName = fileName,
                FileSize = fileSize,
                LocalFilePath = filePath,
                MimeType = mimeType,
                IsGroup = isGroup
            };

            if (_databaseService != null)
            {
                var chatRepository = new ChatRepository(_databaseService);
                chatRepository.SaveMessage(dbMsg);
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var startMsg = new ChatMessage
                    {
                        Type = "file_start",
                        SenderId = LocalPeerId,
                        SenderName = Environment.MachineName,
                        ShortSessionId = GetLocalShortSessionId(),
                        IsGroup = isGroup,
                        ConversationId = conversationId,
                        FileId = fileId,
                        FileName = fileName,
                        FileSize = fileSize,
                        MimeType = mimeType,
                        Body = ""
                    };

                    await SendNetworkMessageAsync(startMsg, isGroup, connection);

                    const int ChunkSize = 256 * 1024; // 256KB chunks
                    int chunkCount = (int)Math.Ceiling((double)fileSize / ChunkSize);
                    if (chunkCount == 0) chunkCount = 1;

                    byte[] buffer = new byte[ChunkSize];
                    using (var fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                    {
                        for (int i = 0; i < chunkCount; i++)
                        {
                            int bytesRead = await fileStream.ReadAsync(buffer, 0, ChunkSize);
                            byte[] actualChunk = new byte[bytesRead];
                            Array.Copy(buffer, actualChunk, bytesRead);

                            var chunkMsg = new ChatMessage
                            {
                                Type = "file_chunk",
                                SenderId = LocalPeerId,
                                SenderName = Environment.MachineName,
                                ShortSessionId = GetLocalShortSessionId(),
                                IsGroup = isGroup,
                                ConversationId = conversationId,
                                FileId = fileId,
                                ChunkIndex = i,
                                ChunkCount = chunkCount,
                                ChunkBase64 = Convert.ToBase64String(actualChunk),
                                Body = ""
                            };

                            await SendNetworkMessageAsync(chunkMsg, isGroup, connection);

                            double pct = (double)(i + 1) / chunkCount * 100.0;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                viewModel.ProgressValue = pct;
                                viewModel.ProgressText = $"送信中... {pct:F0}%";
                            });
                        }
                    }

                    var endMsg = new ChatMessage
                    {
                        Type = "file_end",
                        SenderId = LocalPeerId,
                        SenderName = Environment.MachineName,
                        ShortSessionId = GetLocalShortSessionId(),
                        IsGroup = isGroup,
                        ConversationId = conversationId,
                        FileId = fileId,
                        FileName = fileName,
                        FileSize = fileSize,
                        MimeType = mimeType,
                        Body = ""
                    };

                    await SendNetworkMessageAsync(endMsg, isGroup, connection);

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        viewModel.IsTransferring = false;
                        viewModel.ProgressValue = 100;
                        viewModel.ProgressText = "送信完了";
                    });

                    AddLog($"添付送信完了: {fileName}", LogLevel.Success);
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        viewModel.IsTransferring = false;
                        viewModel.ProgressText = "送信失敗";
                        AddLog($"添付送信失敗: {fileName}. エラー: {ex.Message}", LogLevel.Error);
                    });
                }
            });
        }

        private async System.Threading.Tasks.Task SendNetworkMessageAsync(ChatMessage msg, bool isGroup, ChatConnection? peerConnection)
        {
            if (isGroup)
            {
                if (_chatRole == ChatRole.Host)
                {
                    await _chatConnectionManager.BroadcastAsync(msg);
                }
                else
                {
                    var hostConn = _chatConnectionManager.Connections.FirstOrDefault(c => c.IsConnected && c.IsReady);
                    if (hostConn == null)
                    {
                        throw new InvalidOperationException("Host接続が見つかりません。");
                    }
                    await hostConn.SendAsync(msg);
                }
            }
            else
            {
                if (peerConnection == null || !peerConnection.IsConnected)
                {
                    throw new InvalidOperationException("送信先への接続がありません。");
                }
                await peerConnection.SendAsync(msg);
            }
        }

        private static string GetMimeType(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        private static string GetUniqueFilePath(string directory, string fileName)
        {
            string fullPath = System.IO.Path.Combine(directory, fileName);
            if (!System.IO.File.Exists(fullPath))
            {
                return fullPath;
            }

            string nameOnly = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string extension = System.IO.Path.GetExtension(fileName);
            int count = 1;

            while (System.IO.File.Exists(fullPath))
            {
                fullPath = System.IO.Path.Combine(directory, $"{nameOnly}({count}){extension}");
                count++;
            }

            return fullPath;
        }

        private string GetAttachmentsDirectory()
        {
            string dbDir = System.IO.Path.GetDirectoryName(_databaseService?.DatabasePath ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "direct_module", "chat.db"))!;
            return System.IO.Path.Combine(dbDir, "attachments");
        }

        private async System.Threading.Tasks.Task HandleFileStartAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            if (string.IsNullOrWhiteSpace(message.FileId)) return;

            string fileId = message.FileId;
            string fileName = message.FileName ?? "unknown";
            long fileSize = message.FileSize ?? 0;
            string mimeType = message.MimeType ?? "application/octet-stream";
            string msgType = mimeType.StartsWith("image/") ? "image" : "file";

            AddLog($"添付受信開始: {fileName} ({fileSize / 1024.0:F1} KB)");

            string attachmentsDir = GetAttachmentsDirectory();
            System.IO.Directory.CreateDirectory(attachmentsDir);
            string partFilePath = System.IO.Path.Combine(attachmentsDir, fileId + ".part");

            if (System.IO.File.Exists(partFilePath))
            {
                System.IO.File.Delete(partFilePath);
            }

            var session = new IncomingFileSession
            {
                FileId = fileId,
                FileName = fileName,
                FileSize = fileSize,
                MimeType = mimeType,
                PartFilePath = partFilePath
            };

            bool isCurrent = false;
            if (PeerList.SelectedItem is PeerInfo activePeer)
            {
                if (message.IsGroup && activePeer.IsGroupChat) isCurrent = true;
                else if (!message.IsGroup && !activePeer.IsGroupChat && string.Equals(PeerIdentityService.GetConnectionId(activePeer), message.ConversationId, StringComparison.OrdinalIgnoreCase)) isCurrent = true;
            }

            if (isCurrent)
            {
                var viewModel = new ChatMessageViewModel
                {
                    SenderName = message.SenderName,
                    Body = $"[添付ファイル] {fileName}",
                    TimeText = DateTime.Now.ToString("HH:mm"),
                    IsMine = false,
                    IsGroup = message.IsGroup,
                    MessageType = msgType,
                    FileId = fileId,
                    FileName = fileName,
                    FileSize = fileSize,
                    MimeType = mimeType,
                    IsTransferring = true,
                    ProgressValue = 0,
                    ProgressText = "受信中... 0%"
                };
                session.ViewModel = viewModel;
                AddChatMessage(viewModel);
            }

            lock (_incomingFiles)
            {
                _incomingFiles[fileId] = session;
            }
        }

        private async System.Threading.Tasks.Task HandleFileChunkAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            if (string.IsNullOrWhiteSpace(message.FileId) || string.IsNullOrWhiteSpace(message.ChunkBase64)) return;

            string fileId = message.FileId;
            int chunkIndex = message.ChunkIndex ?? 0;
            int chunkCount = message.ChunkCount ?? 1;

            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(fileId, out session);
            }

            if (session == null)
            {
                return;
            }

            try
            {
                byte[] chunkData = Convert.FromBase64String(message.ChunkBase64);

                using (var fs = new System.IO.FileStream(session.PartFilePath, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
                {
                    fs.Seek(0, System.IO.SeekOrigin.End);
                    await fs.WriteAsync(chunkData, 0, chunkData.Length);
                }

                session.LastChunkIndex = chunkIndex;

                if (session.ViewModel != null)
                {
                    double pct = (double)(chunkIndex + 1) / chunkCount * 100.0;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        session.ViewModel.ProgressValue = pct;
                        session.ViewModel.ProgressText = $"受信中... {pct:F0}%";
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"チャンク書き込みエラー: {ex.Message}", LogLevel.Error);
            }
        }

        private async System.Threading.Tasks.Task HandleFileEndAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            if (string.IsNullOrWhiteSpace(message.FileId)) return;

            string fileId = message.FileId;

            IncomingFileSession? session;
            lock (_incomingFiles)
            {
                _incomingFiles.TryGetValue(fileId, out session);
                if (session != null)
                {
                    _incomingFiles.Remove(fileId);
                }
            }

            if (session == null)
            {
                return;
            }

            try
            {
                string attachmentsDir = GetAttachmentsDirectory();
                string finalPath = GetUniqueFilePath(attachmentsDir, session.FileName);

                if (System.IO.File.Exists(session.PartFilePath))
                {
                    System.IO.File.Move(session.PartFilePath, finalPath);
                }
                else
                {
                    AddLog($"一時ファイルが見つかりません: {session.PartFilePath}", LogLevel.Error);
                    return;
                }

                AddLog($"添付受信完了: {session.FileName} -> {finalPath}", LogLevel.Success);

                string senderPeerId = message.SenderId;
                string senderName = message.SenderName;
                string convId = message.IsGroup ? "group" : message.ConversationId;

                var dbMsg = new direct_module.Models.ChatMessage
                {
                    ConversationId = convId,
                    SenderId = senderPeerId,
                    SenderName = senderName,
                    ReceiverId = message.IsGroup ? "group" : LocalPeerId,
                    ReceiverName = message.IsGroup ? "Group" : Environment.MachineName,
                    Message = $"[添付ファイル] {session.FileName}",
                    SendTime = DateTime.Now,
                    IsMine = false,
                    MessageType = session.MimeType.StartsWith("image/") ? "image" : "file",
                    FileId = fileId,
                    FileName = session.FileName,
                    FileSize = session.FileSize,
                    LocalFilePath = finalPath,
                    MimeType = session.MimeType,
                    IsGroup = message.IsGroup
                };

                if (_databaseService != null)
                {
                    var chatRepository = new ChatRepository(_databaseService);
                    chatRepository.SaveMessage(dbMsg);
                }

                if (session.ViewModel != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        session.ViewModel.LocalFilePath = finalPath;
                        session.ViewModel.IsTransferring = false;
                        session.ViewModel.ProgressValue = 100;
                        session.ViewModel.ProgressText = "受信完了";
                        RefreshMessageListDisplay();
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ファイル受信完了処理エラー: {ex.Message}", LogLevel.Error);
                if (session.ViewModel != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        session.ViewModel.IsTransferring = false;
                        session.ViewModel.ProgressText = "受信失敗";
                    });
                }
            }
        }

        private async void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                
                var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

                var files = await picker.PickMultipleFilesAsync();
                if (files != null && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        await SendFileAsync(file.Path, file.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"ファイル選択エラー: {ex.Message}", LogLevel.Error);
            }
        }

        private async void MessageTextBox_Paste(object sender, TextControlPasteEventArgs e)
        {
            var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                e.Handled = true; // prevent text paste of image
                
                try
                {
                    var imageStreamRef = await dataPackageView.GetBitmapAsync();
                    using var imageStream = await imageStreamRef.OpenReadAsync();
                    
                    string attachmentsDir = GetAttachmentsDirectory();
                    System.IO.Directory.CreateDirectory(attachmentsDir);
                    
                    string tempFileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string tempFilePath = System.IO.Path.Combine(attachmentsDir, tempFileName);
                    
                    using (var fileStream = new System.IO.FileStream(tempFilePath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                    {
                        using (var stream = imageStream.AsStreamForRead())
                        {
                            await stream.CopyToAsync(fileStream);
                        }
                    }
                    
                    AddLog($"クリップボード画像を取得しました: {tempFileName}");
                    await SendFileAsync(tempFilePath, tempFileName);
                }
                catch (Exception ex)
                {
                    AddLog($"クリップボード画像取得エラー: {ex.Message}", LogLevel.Error);
                }
            }
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessageViewModel viewModel)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(viewModel.LocalFilePath) || !System.IO.File.Exists(viewModel.LocalFilePath))
            {
                AddLog($"ファイルが見つかりません: {viewModel.LocalFilePath}", LogLevel.Error);
                return;
            }

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(viewModel.LocalFilePath);
                await Windows.System.Launcher.LaunchFileAsync(file);
                AddLog($"ファイルを開きました: {viewModel.FileName}");
            }
            catch (Exception ex)
            {
                AddLog($"ファイルオープン失敗: {ex.Message}", LogLevel.Error);
            }
        }

        private void RefreshMessageListDisplay()
        {
            // Binding triggers update automatically through INotifyPropertyChanged.
        }

        private void SaveChatMessageSafely(ChatMessage message, bool isOutgoing, PeerInfo? peer, ChatConnection? connection)
        {
            if (_chatHistoryService == null)
            {
                AddLog("履歴保存失敗: ChatHistoryServiceが初期化されていません", LogLevel.Error);
                return;
            }

            ChatHistorySaveResult result = _chatHistoryService.SaveMessage(message, isOutgoing, peer, connection);
            switch (result.Status)
            {
                case ChatHistorySaveStatus.SkippedNonChat:
                    return;
                case ChatHistorySaveStatus.DuplicateMessageId:
                    AddLog($"重複MessageIdのため履歴保存をスキップ: {result.MessageId}", LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Saved:
                    AddLog(
                        isOutgoing
                            ? $"送信メッセージ履歴保存成功: MessageId={result.MessageId}"
                            : $"受信メッセージ履歴保存成功: MessageId={result.MessageId}",
                        LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Failed:
                    AddLog($"履歴保存失敗: MessageId={result.MessageId}, Error={result.ErrorMessage}", LogLevel.Error);
                    return;
                default:
                    return;
            }
        }

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            LogLevel effectiveLevel = level == LogLevel.Info
                ? LogClassifier.Classify(message)
                : level;

            if (effectiveLevel == LogLevel.Debug && ShowDebugLogCheckBox?.IsChecked != true)
            {
                return;
            }

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{effectiveLevel}] {message}";
            _logLines.Add(line);

            bool trimmed = false;
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
                trimmed = true;
            }

            if (trimmed)
            {
                LogTextBox.Text = string.Join(Environment.NewLine, _logLines);
            }
            else
            {
                LogTextBox.Text = string.IsNullOrEmpty(LogTextBox.Text)
                    ? line
                    : $"{LogTextBox.Text}{Environment.NewLine}{line}";
            }

            MoveLogCaretToEnd();
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
        }
    }
}
