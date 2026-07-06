using direct_module.Discovery;
using direct_module.Network;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Networking.Sockets;

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

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly ChatConnectionManager _chatConnectionManager = new();
        private readonly List<string> _logLines = new();
        private readonly HashSet<string> _autoWiFiDirectConnectInProgress = new(StringComparer.OrdinalIgnoreCase);
        private readonly Guid _localSessionId = Guid.NewGuid();

        private ChatRole _chatRole = ChatRole.Client;
        private bool _isPreparingChatTcp;

        public MainWindow()
        {
            InitializeComponent();

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
            _chatConnectionManager.ConnectionsChanged += OnChatConnectionsChanged;

            SetChatReady(false);
        }

        private string LocalPeerId => _localSessionId.ToString("N");

        private string GetLocalShortSessionId()
        {
            return _localSessionId.ToString("N")[..4];
        }

        private async void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            AddLog("相手探索開始");
            AddLog($"Local ShortSessionId: {GetLocalShortSessionId()}");

            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            await EnsureTcpServerStartedAsync("相手探索開始");
            StartBleAdvertiseCore();
            _discoveryManager.StartScan();

            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();

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

                var message = new ChatMessage
                {
                    Type = "chat",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = body
                };

                _chatConnectionManager.MarkMessageSeen(message.MessageId);

                if (_chatRole == ChatRole.Host && PeerList.SelectedItem is not PeerInfo && _chatConnectionManager.ConnectedCount > 0)
                {
                    AddLog($"Host送信: 接続中ClientへBroadcast Count={_chatConnectionManager.ConnectedCount}");
                    await _chatConnectionManager.BroadcastAsync(message);
                    AddChatMessage($"自分: {body}");
                    return;
                }

                if (PeerList.SelectedItem is not PeerInfo peer || !IsPeerChatReady(peer))
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

            AddLog($"Wi-Fi Direct接続開始: {peer.DisplayText}");
            await _manager.ConnectAsync(peer);
            RefreshPeerDisplay(peer);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogTextBox.Text = string.Empty;
        }

        private void ScrollLogBottom_Click(object sender, RoutedEventArgs e)
        {
            MoveLogCaretToEnd();
        }

        private async void OnPeerFound(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                AddOrMergePeer(peer);
                PeerInfo mergedPeer = FindMergedPeer(peer);
                await TryAutoConnectWiFiDirectAsync(mergedPeer);
            });

            if (peer.DiscoveredByBle)
            {
                await _manager.StartAssociationEndpointScanAsync();
            }
        }

        private void OnLogReceived(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog(message, ClassifyLogMessage(message));
            });
        }

        private void OnConnectionRequested(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _chatRole = ChatRole.Host;
                AddLog($"接続要求: {peer.DisplayName}");
                AddLog("Chat Role: Host");
            });
        }

        private void OnWiFiDirectConnected(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}", LogLevel.Success);
                AddOrMergePeer(peer);
                await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");

                if (_chatRole == ChatRole.Client)
                {
                    await PrepareChatTcpConnectionAsync(peer);
                }
                else
                {
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
                connection.AttachAcceptedSocket(socket);

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

            ChatConnection? existing =
                _chatConnectionManager.FindByShortSessionId(peer.ShortSessionId) ??
                _chatConnectionManager.FindByPeerId(GetPeerConnectionId(peer)) ??
                _chatConnectionManager.FindByRemoteIpAddress(peer.RemoteIpAddress);

            if (existing?.IsConnected == true)
            {
                AddLog("Chat TCP接続済みなので再利用");
                return existing;
            }

            var connection = new ChatConnection
            {
                PeerId = GetPeerConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress,
                ShortSessionId = peer.ShortSessionId
            };

            _chatConnectionManager.AddConnection(connection);
            peer.IsTcpConnecting = true;
            peer.LastConnectError = "";
            RefreshPeerDisplay(peer);
            await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);

            peer.IsTcpConnecting = false;
            peer.IsTcpConnected = connection.IsConnected;
            peer.LastConnectError = connection.IsConnected ? "" : "TCP preconnect failed";
            RefreshPeerDisplay(peer);

            return connection;
        }

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer)
        {
            if (_isPreparingChatTcp)
            {
                AddLog("Chat TCP接続準備中のためスキップします", LogLevel.Debug);
                return;
            }

            _isPreparingChatTcp = true;

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
                peer.IsTcpConnecting = true;
                peer.LastConnectError = "";
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.StatusText = "TCP準備中";
                RefreshPeerDisplay(peer);
                AddLog("チャット準備中: TCP事前接続を開始します");
                AddLog("Chat TCP事前接続開始");
                AddLog($"接続先IP: {peer.RemoteIpAddress}");
                AddLog($"接続先Port: {LocalTcpPort}");

                ChatConnection? connection = await GetOrCreatePeerConnectionAsync(peer);

                if (connection?.IsConnected == true && connection.IsReceiveLoopStarted)
                {
                    peer.IsTcpConnecting = false;
                    peer.IsTcpConnected = true;
                    peer.IsChatReady = false;
                    peer.StatusText = "HELLO確認中";
                    RefreshPeerDisplay(peer);
                    SetChatReady(false);
                    AddLog("Chat TCP事前接続成功", LogLevel.Success);
                    AddLog("Chat TCP接続済み", LogLevel.Success);
                    AddLog("Chat TCP ReceiveLoop開始済み", LogLevel.Success);
                    await SendHelloAsync(connection);
                    AddLog("HELLO応答待ち");
                    AddLog($"Chat TCP事前接続合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
                }
                else
                {
                    peer.IsTcpConnecting = false;
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.LastConnectError = "TCP preconnect failed";
                    peer.StatusText = "エラー";
                    RefreshPeerDisplay(peer);
                    SetChatReady(false);
                    AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                    AddLog("Chat TCP事前接続失敗: TCP接続またはReceiveLoopが未完了です", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                SetChatReady(false);
                peer.IsTcpConnecting = false;
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.LastConnectError = ex.Message;
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
                peer.IsTcpConnecting = false;
                _isPreparingChatTcp = false;
            }
        }

        private async System.Threading.Tasks.Task<ChatConnection?> GetOrCreatePeerConnectionAsync(PeerInfo peer)
        {
            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("RemoteIpAddressがないためChat TCP自動接続をスキップします");
                return null;
            }

            ChatConnection? existing =
                _chatConnectionManager.FindByShortSessionId(peer.ShortSessionId) ??
                _chatConnectionManager.FindByPeerId(GetPeerConnectionId(peer)) ??
                _chatConnectionManager.FindByRemoteIpAddress(peer.RemoteIpAddress);

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
                PeerId = GetPeerConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress,
                ShortSessionId = peer.ShortSessionId
            };

            _chatConnectionManager.AddConnection(connection);
            AddLog($"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{LocalTcpPort}");
            await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);

            peer.IsTcpConnected = connection.IsConnected;
            peer.IsChatReady = false;
            peer.StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可";
            RefreshPeerDisplay(peer);
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
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        HandleHelloMessage(message, sourceConnection);
                    });
                    return;

                case "chat":
                    break;

                case "ping":
                case "pong":
                case "system":
                case "file_start":
                case "file_chunk":
                case "file_end":
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddLog($"未実装Typeを受信: {messageType}");
                    });
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
                AddLog($"TCP受信メッセージ: {message.Body}", LogLevel.Success);
                AddConnectedPeerDisplay(sourceConnection);
            });

            if (_chatRole == ChatRole.Host)
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
                var helloWatch = Stopwatch.StartNew();
                await connection.SendAsync(hello);
                AddLog($"HELLO送信完了: elapsedMs={helloWatch.ElapsedMilliseconds}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                AddLog("TCP HELLO送信失敗", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private void HandleHelloMessage(ChatMessage message, ChatConnection sourceConnection)
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
            AddLog($"ChatConnectionとPeerInfoを紐付けました: {matchedPeer.DisplayName}");
            AddLog($"PeerごとのHELLO確認成功: {matchedPeer.DisplayName}", LogLevel.Success);
            AddLog("HELLO確認成功: BLE Peerと接続先が一致", LogLevel.Success);
            AddLog("HELLO確認後、チャット準備完了", LogLevel.Success);
            UpdateSendButtonState();
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
                    string.Equals(GetPeerConnectionId(peer), peerId, StringComparison.OrdinalIgnoreCase));
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

        private async System.Threading.Tasks.Task EnsureTcpServerStartedAsync(string reason)
        {
            if (_tcpServer.IsStarted)
            {
                MarkTcpServerReadyForPeers();
                AddLog($"TCPサーバー待ち受け済み: Reason={reason}");
                AddLog($"TCP待ち受けは開始済みです: Reason={reason}", LogLevel.Debug);
                return;
            }

            AddLog($"TCP待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
            AddLog($"Chat TCP Server待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
            await _tcpServer.StartAsync(LocalTcpPort);
            MarkTcpServerReadyForPeers();
        }

        private void MarkTcpServerReadyForPeers()
        {
            foreach (PeerInfo peer in PeerList.Items.Cast<PeerInfo>())
            {
                peer.IsTcpServerReady = true;
            }
        }

        private void SetChatReady(bool isReady)
        {
            UpdateSendButtonState();
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSendButtonState();
        }

        private void UpdateSendButtonState()
        {
            bool canSend = false;

            if (PeerList.SelectedItem is PeerInfo peer)
            {
                canSend = IsPeerChatReady(peer) && GetConnectionForPeer(peer)?.IsReady == true;
            }
            else if (_chatRole == ChatRole.Host)
            {
                canSend = _chatConnectionManager.Connections.Any(connection => connection.IsConnected && connection.IsReady);
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                SendMessageButton.IsEnabled = canSend;
            });

            AddLog(
                canSend
                    ? "選択中Peerの状態によりSendMessageButtonを有効化"
                    : "選択中Peerの状態によりSendMessageButtonを無効化",
                LogLevel.Debug);
        }

        private static bool IsPeerChatReady(PeerInfo peer)
        {
            return peer.IsTcpConnected &&
                   peer.IsHelloVerified &&
                   peer.IsChatReady;
        }

        private ChatConnection? GetConnectionForPeer(PeerInfo peer)
        {
            return _chatConnectionManager.FindByShortSessionId(peer.ShortSessionId) ??
                   _chatConnectionManager.FindByPeerId(GetPeerConnectionId(peer)) ??
                   _chatConnectionManager.FindByRemoteIpAddress(peer.RemoteIpAddress);
        }

        private static string GetPeerConnectionId(PeerInfo peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.ShortSessionId))
            {
                return peer.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                return peer.DeviceId;
            }

            if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                return peer.RemoteIpAddress;
            }

            return peer.DisplayName;
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
                string matchReason = GetPeerMatchReason(existing, incoming);
                if (string.IsNullOrEmpty(matchReason))
                {
                    continue;
                }

                MergePeer(existing, incoming);
                existing.IsAutoConnectEligible |= IsSafeAutoConnectMatchReason(matchReason);
                RefreshPeerDisplay(existing);

                LogLevel level = matchReason.StartsWith("注意:", StringComparison.Ordinal)
                    ? LogLevel.Error
                    : LogLevel.Success;

                AddLog($"Peer統合: {matchReason} -> {existing.DisplayText}", level);
                return;
            }

            PeerList.Items.Add(incoming);
            AddLog($"Peer追加: {incoming.DisplayText}");
        }

        private PeerInfo FindMergedPeer(PeerInfo incoming)
        {
            return PeerList.Items
                .Cast<PeerInfo>()
                .FirstOrDefault(peer =>
                    HasSameValue(peer.ShortSessionId, incoming.ShortSessionId) ||
                    HasSameValue(peer.DeviceId, incoming.DeviceId) ||
                    HasSameValue(peer.DisplayName, incoming.DisplayName) ||
                    HasSameValue(peer.BleName, incoming.BleName) ||
                    HasSameValue(peer.WiFiDirectName, incoming.WiFiDirectName))
                ?? incoming;
        }

        private async System.Threading.Tasks.Task TryAutoConnectWiFiDirectAsync(PeerInfo peer)
        {
            if (!ShouldAutoConnectWiFiDirect(peer))
            {
                return;
            }

            string key = GetPeerConnectionId(peer);
            if (!_autoWiFiDirectConnectInProgress.Add(key))
            {
                AddLog($"Auto Wi-Fi Direct connect skipped: already running, Peer={peer.DisplayName}", LogLevel.Debug);
                return;
            }

            try
            {
                AddLog($"Auto Wi-Fi Direct connect start: Peer={peer.DisplayName}, Reason=safe match");
                AddLog($"Peer merge -> Wi-Fi Direct connect start elapsed marker: Peer={peer.DisplayName}", LogLevel.Debug);
                await _manager.ConnectAsync(peer);
                RefreshPeerDisplay(peer);
            }
            finally
            {
                _autoWiFiDirectConnectInProgress.Remove(key);
            }
        }

        private static bool ShouldAutoConnectWiFiDirect(PeerInfo peer)
        {
            if (peer.IsConnected || peer.IsWifiDirectConnected)
            {
                return false;
            }

            if (!peer.DiscoveredByBle || !peer.DiscoveredByWiFiDirect)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId) ||
                peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return peer.IsAutoConnectEligible &&
                   (!string.IsNullOrWhiteSpace(peer.ShortSessionId) ||
                    HasSameValue(peer.BleName, peer.WiFiDirectName));
        }

        private static bool IsSafeAutoConnectMatchReason(string matchReason)
        {
            return matchReason.StartsWith("ShortSessionId", StringComparison.OrdinalIgnoreCase) ||
                   matchReason.StartsWith("DisplayName", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPeerMatchReason(PeerInfo existing, PeerInfo incoming)
        {
            if (HasSameValue(existing.ShortSessionId, incoming.ShortSessionId))
            {
                return $"ShortSessionId一致 ({incoming.ShortSessionId})";
            }

            if (HasSameValue(existing.MatchKey, incoming.MatchKey))
            {
                return $"MatchKey一致 ({incoming.MatchKey})";
            }

            if (HasSameValue(existing.DeviceId, incoming.DeviceId))
            {
                return "DeviceId一致";
            }

            if (HasSameValue(existing.DisplayName, incoming.DisplayName))
            {
                return $"DisplayName完全一致 ({incoming.DisplayName})";
            }

            string existingName = existing.DisplayName ?? "";
            string incomingName = incoming.DisplayName ?? "";

            if (existingName.Length >= 4 &&
                incomingName.Length >= 4 &&
                (existingName.Contains(incomingName, StringComparison.OrdinalIgnoreCase) ||
                 incomingName.Contains(existingName, StringComparison.OrdinalIgnoreCase)))
            {
                return $"注意: 名前の部分一致 ({existingName} / {incomingName})";
            }

            return "";
        }

        private static bool HasSameValue(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private void MergePeer(PeerInfo target, PeerInfo source)
        {
            if (!string.IsNullOrWhiteSpace(source.DisplayName) &&
                (string.IsNullOrWhiteSpace(target.DisplayName) ||
                 source.DisplayName.Length > target.DisplayName.Length))
            {
                target.DisplayName = source.DisplayName;
            }

            target.DiscoveredByBle |= source.DiscoveredByBle;
            target.DiscoveredByWiFiDirect |= source.DiscoveredByWiFiDirect;

            CopyIfPresent(source.BleName, value => target.BleName = value);
            CopyIfPresent(source.WiFiDirectName, value => target.WiFiDirectName = value);
            CopyIfPresent(source.MatchKey, value => target.MatchKey = value);
            CopyIfPresent(source.ShortSessionId, value => target.ShortSessionId = value);
            CopyIfPresent(source.DeviceId, value => target.DeviceId = value);
            CopyIfPresent(source.DeviceKind, value => target.DeviceKind = value);
            CopyIfPresent(source.IpAddress, value => target.IpAddress = value);
            CopyIfPresent(source.RemoteIpAddress, value => target.RemoteIpAddress = value);

            if (source.TcpPort > 0)
            {
                target.TcpPort = source.TcpPort;
            }

            if (source.IsEnabled.HasValue)
            {
                target.IsEnabled = source.IsEnabled;
            }

            target.IsConnected |= source.IsConnected;
            target.IsTcpConnected |= source.IsTcpConnected;
            target.IsHelloVerified |= source.IsHelloVerified;
            target.IsChatReady |= source.IsChatReady;
            CopyIfPresent(source.StatusText, value => target.StatusText = value);
        }

        private static void CopyIfPresent(string value, Action<string> apply)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value);
            }
        }

        private void ClearStaleWiFiDirectPeers()
        {
            int removed = 0;
            var peers = PeerList.Items.Cast<PeerInfo>().ToList();

            foreach (PeerInfo peer in peers)
            {
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

            PeerList.Items.Add(peer);
        }

        private void RefreshPeerDisplay(PeerInfo peer)
        {
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

            UpdateSendButtonState();
        }

        private void AddChatMessage(string message)
        {
            MessageList.Items.Add(message);
            MessageList.ScrollIntoView(message);
        }

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            LogLevel effectiveLevel = level == LogLevel.Info
                ? ClassifyLogMessage(message)
                : level;

            if (effectiveLevel == LogLevel.Debug && ShowDebugLogCheckBox?.IsChecked != true)
            {
                return;
            }

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{effectiveLevel}] {message}";
            _logLines.Add(line);

            while (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
            }

            LogTextBox.Text = string.Join(Environment.NewLine, _logLines);
            MoveLogCaretToEnd();
        }

        private static LogLevel ClassifyLogMessage(string message)
        {
            if (IsErrorLogMessage(message))
            {
                return LogLevel.Error;
            }

            if (IsDebugLogMessage(message))
            {
                return LogLevel.Debug;
            }

            if (IsSuccessLogMessage(message))
            {
                return LogLevel.Success;
            }

            return LogLevel.Info;
        }

        private static bool IsErrorLogMessage(string message)
        {
            string[] errorKeywords =
            {
                "失敗",
                "エラー",
                "例外",
                "Exception",
                "HResult",
                "Message:",
                "不正",
                "切断",
                "未接続",
                "送信できません",
                "接続できません",
                "ありません",
                "注意:"
            };

            return errorKeywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSuccessLogMessage(string message)
        {
            string[] successKeywords =
            {
                "成功",
                "完了",
                "接続済み",
                "送信成功",
                "受信",
                "RemoteIpAddress保存",
                "チャット準備完了",
                "SendMessageButton有効化",
                "Peer統合"
            };

            return successKeywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDebugLogMessage(string message)
        {
            string[] debugKeywords =
            {
                "Selector",
                "Watcher Status",
                "Added",
                "Updated",
                "Removed",
                "EnumerationCompleted",
                "Stopped",
                "Kind",
                "IsEnabled",
                "InformationElements.Count",
                "InformationElement",
                "ConnectAttempt",
                "elapsedMs",
                "LegacySettings.IsEnabled",
                "ListenStateDiscoverability",
                "LocalServiceName",
                "RemoteServiceName",
                "WriteUInt32",
                "WriteBytes",
                "StoreAsync",
                "FlushAsync",
                "平文Bytes",
                "暗号化後Bytes",
                "送信Bytes",
                "送信フレームBytes",
                "length読み取り",
                "本文読み取り",
                "ConnectAsync:",
                "Stopwatch",
                "Elapsed",
                "合計:",
                "ms",
                "Local IP",
                "Local SessionId",
                "Local ShortSessionId",
                "Local TCP Port",
                "Peer照合開始",
                "DeviceIdあり",
                "接続中Peer数",
                "SendAsync内でConnectが必要か",
                "接続状態: IsConnected",
                "MessageId:",
                "SenderName:",
                "MessageCrypto:"
            };

            return debugKeywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
            LogTextBox.Focus(FocusState.Programmatic);
        }
    }
}
