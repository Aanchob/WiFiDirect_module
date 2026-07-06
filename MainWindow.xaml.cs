using direct_module.Discovery;
using direct_module.Network;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
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
        private readonly Guid _localSessionId = Guid.NewGuid();

        private ChatRole _chatRole = ChatRole.Client;
        private bool _isChatReady;
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
                if (!_isChatReady)
                {
                    AddLog("チャット準備中のため送信できません");
                    return;
                }

                string body = MessageTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(body))
                {
                    AddLog("送信内容が空です");
                    return;
                }

                var message = new ChatMessage
                {
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    Body = body
                };

                _chatConnectionManager.MarkMessageSeen(message.MessageId);

                if (_chatRole == ChatRole.Host && _chatConnectionManager.ConnectedCount > 0)
                {
                    AddLog($"Host送信: 接続中ClientへBroadcast Count={_chatConnectionManager.ConnectedCount}");
                    await _chatConnectionManager.BroadcastAsync(message);
                    AddChatMessage($"自分: {body}");
                    return;
                }

                ChatConnection? connection = GetSelectedPeerPreparedConnection();
                if (connection == null || !connection.IsConnected)
                {
                    AddLog("Chat TCP未接続のため送信できません", LogLevel.Error);
                    SetChatReady(false);
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
                SendMessageButton.IsEnabled = _isChatReady;
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
            DispatcherQueue.TryEnqueue(() =>
            {
                AddOrMergePeer(peer);
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
            DispatcherQueue.TryEnqueue(() =>
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

                SetChatReady(connection.IsConnected && connection.IsReceiveLoopStarted);
                AddLog($"Host: Client TCP接続を追加 Count={_chatConnectionManager.ConnectedCount}");
                AddConnectedPeerDisplay(connection);
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

            ChatConnection? connection =
                _chatConnectionManager.FindByPeerId(GetPeerConnectionId(peer)) ??
                _chatConnectionManager.FindByRemoteIpAddress(peer.RemoteIpAddress);

            if (connection == null)
            {
                AddLog("Chat TCP未接続です。事前接続を確認してください。", LogLevel.Error);
                return null;
            }

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
                RemoteIpAddress = peer.RemoteIpAddress
            };

            _chatConnectionManager.AddConnection(connection);
            await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);

            peer.IsTcpConnected = connection.IsConnected;
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
                    peer.IsTcpConnected = true;
                    peer.IsChatReady = true;
                    peer.StatusText = "チャット準備完了";
                    RefreshPeerDisplay(peer);
                    SetChatReady(true);
                    AddLog("Chat TCP事前接続成功", LogLevel.Success);
                    AddLog("Chat TCP接続済み", LogLevel.Success);
                    AddLog("Chat TCP ReceiveLoop開始済み", LogLevel.Success);
                    AddLog("チャット準備完了", LogLevel.Success);
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
                }
            }
            catch (Exception ex)
            {
                SetChatReady(false);
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
                _chatConnectionManager.FindByPeerId(GetPeerConnectionId(peer)) ??
                _chatConnectionManager.FindByRemoteIpAddress(peer.RemoteIpAddress);

            if (existing?.IsConnected == true)
            {
                AddLog("Chat TCP接続済みなので再利用");
                peer.IsTcpConnected = true;
                peer.IsChatReady = existing.IsReceiveLoopStarted;
                peer.StatusText = existing.IsReceiveLoopStarted ? "チャット準備完了" : "TCP準備中";
                RefreshPeerDisplay(peer);
                return existing;
            }

            var connection = new ChatConnection
            {
                PeerId = GetPeerConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress
            };

            _chatConnectionManager.AddConnection(connection);
            AddLog($"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{LocalTcpPort}");
            await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);

            peer.IsTcpConnected = connection.IsConnected;
            peer.IsChatReady = connection.IsConnected && connection.IsReceiveLoopStarted;
            peer.StatusText = peer.IsChatReady ? "チャット準備完了" : connection.IsConnected ? "TCP準備中" : "送信不可";
            RefreshPeerDisplay(peer);
            return connection;
        }

        private async void OnChatMessageReceived(ChatMessage message, ChatConnection sourceConnection)
        {
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

        private void OnChatConnectionsChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog($"接続中Peer数: {_chatConnectionManager.ConnectedCount}");
            });
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
            _isChatReady = isReady;

            DispatcherQueue.TryEnqueue(() =>
            {
                SendMessageButton.IsEnabled = isReady;
            });

            AddLog(isReady ? "SendMessageButton有効化" : "SendMessageButton無効化", LogLevel.Debug);
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
                    existing.IsChatReady = connection.IsConnected && connection.IsReceiveLoopStarted;
                    existing.StatusText = existing.IsChatReady ? "チャット準備完了" : connection.IsConnected ? "TCP準備中" : "送信不可";
                    RefreshPeerDisplay(existing);
                    return;
                }
            }

            var peer = new PeerInfo
            {
                DisplayName = displayName,
                RemoteIpAddress = connection.RemoteIpAddress,
                IsTcpConnected = connection.IsConnected,
                IsChatReady = connection.IsConnected && connection.IsReceiveLoopStarted,
                StatusText = connection.IsConnected && connection.IsReceiveLoopStarted
                    ? "チャット準備完了"
                    : connection.IsConnected ? "TCP準備中" : "送信不可"
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

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
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
            if (message.Contains("失敗", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("エラー", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("例外", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("不正", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("ありません", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("注意:", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Error;
            }

            if (message.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("完了", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("受信", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Peer統合", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Success;
            }

            if (message.Contains("Status", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Selector", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Bytes", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("照合", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Debug", StringComparison.OrdinalIgnoreCase))
            {
                return LogLevel.Debug;
            }

            return LogLevel.Info;
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
            LogTextBox.Focus(FocusState.Programmatic);
        }
    }
}
