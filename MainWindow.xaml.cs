using direct_module.Discovery;
using direct_module.Network;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using System;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow : Window
    {
        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private ChatConnection? _chatConnection;

        private readonly Guid _localSessionId = Guid.NewGuid();
        private const int LocalTcpPort = 50001;

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

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start();
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
            string localIp = LocalNetworkInfo.GetLocalIpv4Address();

            _discoveryManager.StartAdvertise(
                Environment.MachineName,
                _localSessionId,
                LocalTcpPort
            );

            AddLog($"Local IP: {localIp}");
            AddLog($"Local SessionId: {_localSessionId}");
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

        private async void SendTcpToSelected_Click(object sender, RoutedEventArgs e)
        {
            string ipAddress = ResolveTcpDestinationIp();

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                AddLog("送信先IPがありません");
                return;
            }

            string message = MessageTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(message))
            {
                message = $"Hello from {Environment.MachineName}";
            }

            ChatConnection chatConnection = GetOrCreateChatConnection();

            if (!chatConnection.IsConnected)
            {
                await chatConnection.ConnectAsync(ipAddress, LocalTcpPort);
            }
            else
            {
                AddLog("Chat TCP接続済みなので再利用");
            }

            if (!chatConnection.IsConnected)
            {
                AddLog("Chat TCPが未接続のため送信を中止します");
                return;
            }

            await chatConnection.SendAsync(message);
            AddChatMessage($"自分: {message}");
        }

        private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください");
                return;
            }

            if (peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                AddLog("PendingRequest付きDeviceIdのため、この候補では接続しません");
                AddLog("探索後に追加された通常のWi-Fi Direct候補を選択してください");
                return;
            }

            AddLog($"接続開始: {peer.DisplayText}");
            await _manager.ConnectAsync(peer);
            RefreshPeerDisplay(peer);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
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
                PeerList.Items.Add(peer);
                AddLog($"Peer追加: {peer.DisplayText}");

                if (peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("注意: PendingRequest付きDeviceIdです。この候補では通常接続しません。");
                }
            });

            if (peer.DiscoveredByBle)
            {
                AddLog("BLEで相手を発見したため、AssociationEndpoint探索を開始します");
                await _manager.StartAssociationEndpointScanAsync();
            }
        }

        private void OnLogReceived(string message)
        {
            AddLog(message);
        }

        private void OnConnectionRequested(PeerInfo peer)
        {
            AddLog($"接続要求: {peer.DisplayName}");
        }

        private async void OnWiFiDirectConnected(PeerInfo peer)
        {
            AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}");
            RefreshPeerDisplay(peer);
            await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog("Chat TCP受信側接続を保持します");
                ReplaceChatConnection(new ChatConnection());
                _chatConnection?.AttachAcceptedSocket(socket);
            });
        }

        private async System.Threading.Tasks.Task EnsureTcpServerStartedAsync(string reason)
        {
            if (_tcpServer.IsStarted)
            {
                AddLog($"TCP待ち受け確認: すでに開始済み ({reason})");
                return;
            }

            AddLog($"TCP待ち受け自動開始: Port={LocalTcpPort}, Reason={reason}");
            await _tcpServer.StartAsync(LocalTcpPort);
        }

        private string ResolveTcpDestinationIp()
        {
            if (PeerList.SelectedItem is PeerInfo peer)
            {
                if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    AddLog($"TCP送信先: PeerInfo.RemoteIpAddress を使用 {peer.RemoteIpAddress}");
                    return peer.RemoteIpAddress;
                }

                AddLog("選択中PeerにRemoteIpAddressがありません");
            }

            string manualIp = TargetIpTextBox.Text.Trim();

            if (!string.IsNullOrWhiteSpace(manualIp))
            {
                AddLog($"TCP送信先: 手入力IPを使用 {manualIp}");
                return manualIp;
            }

            return string.Empty;
        }

        private ChatConnection GetOrCreateChatConnection()
        {
            if (_chatConnection != null)
            {
                return _chatConnection;
            }

            ReplaceChatConnection(new ChatConnection());
            return _chatConnection!;
        }

        private void ReplaceChatConnection(ChatConnection chatConnection)
        {
            if (_chatConnection != null)
            {
                _chatConnection.LogReceived -= OnLogReceived;
                _chatConnection.MessageReceived -= OnChatMessageReceived;
                _chatConnection.Closed -= OnChatConnectionClosed;
                _chatConnection.Close();
            }

            _chatConnection = chatConnection;
            _chatConnection.LogReceived += OnLogReceived;
            _chatConnection.MessageReceived += OnChatMessageReceived;
            _chatConnection.Closed += OnChatConnectionClosed;
        }

        private void OnChatMessageReceived(string message)
        {
            AddLog($"TCP受信メッセージ: {message}");
            AddChatMessage($"相手: {message}");
        }

        private void OnChatConnectionClosed()
        {
            _chatConnection = null;
        }

        private void ClearStaleWiFiDirectPeers()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                int removed = 0;

                for (int i = PeerList.Items.Count - 1; i >= 0; i--)
                {
                    if (PeerList.Items[i] is not PeerInfo peer)
                    {
                        continue;
                    }

                    if (peer.DiscoveredByBle || peer.IsConnected)
                    {
                        continue;
                    }

                    PeerList.Items.RemoveAt(i);
                    removed++;
                }

                AddLog($"古いWi-Fi Direct候補を削除: {removed}件");
            });
        }

        private void RefreshPeerDisplay(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                for (int i = 0; i < PeerList.Items.Count; i++)
                {
                    if (PeerList.Items[i] is not PeerInfo item)
                    {
                        continue;
                    }

                    bool sameObject = ReferenceEquals(item, peer);
                    bool sameDeviceId = !string.IsNullOrWhiteSpace(peer.DeviceId) &&
                                        string.Equals(item.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase);

                    if (!sameObject && !sameDeviceId)
                    {
                        continue;
                    }

                    PeerList.Items[i] = peer;
                    AddLog($"Peer表示更新: {peer.DisplayText}");
                    return;
                }

                if (peer.IsConnected)
                {
                    PeerList.Items.Add(peer);
                    AddLog($"接続済みPeerを一覧に追加: {peer.DisplayText}");
                    AddLog("受信acceptで作成されたPeerのため、TCP返信先として選択できます");
                }
            });
        }

        private void AddChatMessage(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss");
                MessageList.Items.Add($"[{time}] {message}");
                MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
            });
        }

        private void AddLog(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss.fff");
                string log = $"[{time}] {message}";

                LogTextBox.Text += log + Environment.NewLine;
                MoveLogCaretToEnd();
            });
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.Select(LogTextBox.Text.Length, 0);
        }
    }
}
