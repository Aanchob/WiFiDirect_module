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

    public sealed partial class MainWindow : Window
    {
        private const int LocalTcpPort = 50001;
        private const int MaxLogLines = 500;

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly List<string> _logLines = new();
        private readonly Guid _localSessionId = Guid.NewGuid();

        private ChatConnection? _chatConnection;
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
        }

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

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start(Environment.MachineName, GetLocalShortSessionId());
            AddLog("Wi-Fi Direct広告+待ち受け開始ボタンを押しました");
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
            await EnsureTcpServerStartedAsync("手動操作");
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            var totalWatch = Stopwatch.StartNew();
            SendMessageButton.IsEnabled = false;

            try
            {
                string message = MessageTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = $"Hello from {Environment.MachineName}";
                }

                string ipAddress = ResolveTcpDestinationIp();
                if (string.IsNullOrWhiteSpace(ipAddress))
                {
                    AddLog("送信先RemoteIpAddressがありません。先にWi-Fi Direct接続してください。", LogLevel.Error);
                    return;
                }

                ChatConnection chatConnection = GetOrCreateChatConnection();

                if (!chatConnection.IsConnected)
                {
                    AddLog("Chat TCP未接続のため接続します");
                    await chatConnection.ConnectAsync(ipAddress, LocalTcpPort);
                    MarkSelectedPeerTcpState(chatConnection.IsConnected);
                }
                else
                {
                    AddLog("Chat TCP接続済みなので再利用");
                }

                if (!chatConnection.IsConnected)
                {
                    AddLog("Chat TCPが未接続のため送信を中止します", LogLevel.Error);
                    return;
                }

                await chatConnection.SendAsync(message);
                AddChatMessage($"自分: {message}");
                AddLog($"SendMessage_Click完了 合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
            }
            finally
            {
                SendMessageButton.IsEnabled = true;
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
                AddLog($"接続要求: {peer.DisplayName}");
            });
        }

        private void OnWiFiDirectConnected(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}", LogLevel.Success);
                AddOrMergePeer(peer);
                await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");
                await PrepareChatTcpConnectionAsync(peer);
            });
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_chatConnection?.IsConnected == true)
                {
                    AddLog("既存のChat TCP接続があるため、後から来たTCP接続を閉じます");
                    socket.Dispose();
                    return;
                }

                ChatConnection chatConnection = GetOrCreateChatConnection();
                chatConnection.AttachAcceptedSocket(socket);
                MarkSelectedPeerTcpState(true);
            });
        }

        private ChatConnection GetOrCreateChatConnection()
        {
            if (_chatConnection != null)
            {
                return _chatConnection;
            }

            _chatConnection = new ChatConnection();
            _chatConnection.LogReceived += OnLogReceived;
            _chatConnection.MessageReceived += OnChatMessageReceived;
            _chatConnection.Closed += OnChatConnectionClosed;
            return _chatConnection;
        }

        private void OnChatMessageReceived(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddChatMessage($"相手: {message}");
                AddLog($"TCP受信メッセージ: {message}", LogLevel.Success);
            });
        }

        private void OnChatConnectionClosed()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                MarkSelectedPeerTcpState(false);
            });
        }

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer)
        {
            if (_isPreparingChatTcp)
            {
                AddLog("Chat TCP接続準備中のためスキップします", LogLevel.Debug);
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("RemoteIpAddressがないためChat TCP自動接続をスキップします");
                return;
            }

            ChatConnection chatConnection = GetOrCreateChatConnection();
            if (chatConnection.IsConnected)
            {
                AddLog("Chat TCP接続済みなので再利用");
                peer.IsTcpConnected = true;
                RefreshPeerDisplay(peer);
                return;
            }

            _isPreparingChatTcp = true;

            try
            {
                AddLog($"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{LocalTcpPort}");
                await chatConnection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);
                peer.IsTcpConnected = chatConnection.IsConnected;
                RefreshPeerDisplay(peer);
            }
            finally
            {
                _isPreparingChatTcp = false;
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

        private string ResolveTcpDestinationIp()
        {
            if (PeerList.SelectedItem is PeerInfo peer &&
                !string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog($"TCP送信先: PeerInfo.RemoteIpAddress を使用 {peer.RemoteIpAddress}");
                return peer.RemoteIpAddress;
            }

            AddLog("選択中PeerにRemoteIpAddressがありません", LogLevel.Error);
            return string.Empty;
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

        private void MarkSelectedPeerTcpState(bool isConnected)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                return;
            }

            peer.IsTcpConnected = isConnected;
            RefreshPeerDisplay(peer);
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
