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
        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly List<string> _logLines = new();
        private ChatConnection? _chatConnection;
        private bool _isPreparingChatTcp;

        private readonly Guid _localSessionId = Guid.NewGuid();
        private const int LocalTcpPort = 50001;
        private const int MaxLogLines = 500;

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

        private async void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            AddLog("相手探索開始");
            AddLog("Wi-Fi Direct広告+待ち受け開始");
            _manager.Start();

            AddLog("BLE広告開始");
            StartBleAdvertiseCore();

            AddLog("BLEスキャン開始");
            _discoveryManager.StartScan();

            AddLog("AssociationEndpoint探索開始");
            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();

            AddLog("相手探索処理を開始しました");
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
            StartBleAdvertiseCore();
        }

        private void StartBleAdvertiseCore()
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
            var totalWatch = Stopwatch.StartNew();
            SendMessageButton.IsEnabled = false;

            try
            {
                AddLog("SendMessage_Click開始");

                var stepWatch = Stopwatch.StartNew();
                string message = MessageTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = $"Hello from {Environment.MachineName}";
                }
                AddLog($"入力メッセージ取得完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                string ipAddress = ResolveTcpDestinationIp();
                AddLog($"選択Peer/IP取得完了: {stepWatch.ElapsedMilliseconds}ms");
                AddLog($"Peer RemoteIpAddress: {ipAddress}");

                if (string.IsNullOrWhiteSpace(ipAddress))
                {
                    AddLog("送信先IPがありません");
                    return;
                }

                ChatConnection chatConnection = GetOrCreateChatConnection();
                AddLog($"ChatConnection接続状態: {chatConnection.IsConnected}");

                if (!chatConnection.IsConnected)
                {
                    AddLog("SendAsync内でConnectが必要か: True");
                    AddLog("Chat TCP未接続のため接続します");
                    await chatConnection.ConnectAsync(ipAddress, LocalTcpPort);
                    MarkSelectedPeerTcpState(chatConnection.IsConnected);
                }
                else
                {
                    AddLog("SendAsync内でConnectが必要か: False");
                    AddLog("Chat TCP接続済みなので再利用");
                }

                if (!chatConnection.IsConnected)
                {
                    AddLog("Chat TCPが未接続のため送信を中止します");
                    return;
                }

                stepWatch.Restart();
                AddLog("ChatConnection.SendAsync呼び出し開始");
                await chatConnection.SendAsync(message);
                AddLog($"ChatConnection.SendAsync呼び出し完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                AddLog("MessageList自分表示開始");
                AddChatMessage($"自分: {message}");
                AddLog($"MessageList自分表示完了: {stepWatch.ElapsedMilliseconds}ms");
            }
            finally
            {
                SendMessageButton.IsEnabled = true;
                AddLog($"SendMessage_Click完了 合計: {totalWatch.ElapsedMilliseconds}ms");
            }
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
                AddLog("_PendingRequest付きDeviceIdのため通常接続を中止します");
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                AddLog("選択中PeerにWi-Fi Direct DeviceIdがありません");
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
                AddLog("BLEで相手を発見したため、AssociationEndpoint探索を開始します");
                await _manager.StartAssociationEndpointScanAsync();
            }
        }

        private void OnLogReceived(string message)
        {
            AddLog(message, ClassifyLogMessage(message));
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
            await PrepareChatTcpConnectionAsync(peer);
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_chatConnection?.IsConnected == true)
                {
                    AddLog("既存のChat TCP接続があるため、後から来たTCP接続は閉じます");
                    socket.Dispose();
                    return;
                }

                AddLog("Chat TCP受信側接続を保持します");
                ReplaceChatConnection(new ChatConnection());
                _chatConnection?.AttachAcceptedSocket(socket);
                MarkSelectedPeerTcpState(true);
            });
        }

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer)
        {
            if (_isPreparingChatTcp)
            {
                AddLog("Chat TCP事前接続はすでに処理中です");
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("Chat TCP事前接続をスキップ: RemoteIpAddressがありません");
                return;
            }

            ChatConnection chatConnection = GetOrCreateChatConnection();

            if (chatConnection.IsConnected)
            {
                AddLog("Chat TCP事前接続済みなので再利用");
                peer.IsTcpConnected = true;
                RefreshPeerDisplay(peer);
                return;
            }

            _isPreparingChatTcp = true;

            try
            {
                AddLog("Chat TCP事前接続開始");
                AddLog($"RemoteIpAddress: {peer.RemoteIpAddress}");
                await chatConnection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);

                if (chatConnection.IsConnected)
                {
                    peer.IsTcpConnected = true;
                    AddLog("Chat TCP事前接続成功");
                    AddLog("Chat TCP受信ループ開始");
                    RefreshPeerDisplay(peer);
                }
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
            MarkSelectedPeerTcpState(false);
        }

        private void MarkSelectedPeerTcpState(bool isConnected)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (PeerList.SelectedItem is PeerInfo peer)
                {
                    peer.IsTcpConnected = isConnected;
                    RefreshPeerDisplay(peer);
                }
            });
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

        private void AddOrMergePeer(PeerInfo incoming)
        {
            if (incoming.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                AddLog("PendingRequest DeviceIdを受信しました");
                AddLog("これは接続要求Accept用なので通常PeerListには追加しません");
                return;
            }

            for (int i = 0; i < PeerList.Items.Count; i++)
            {
                if (PeerList.Items[i] is not PeerInfo existing)
                {
                    continue;
                }

                if (!IsSamePeer(existing, incoming))
                {
                    continue;
                }

                MergePeer(existing, incoming);
                PeerList.Items[i] = existing;
                AddLog($"Peer統合: {existing.DisplayText}");
                return;
            }

            PeerList.Items.Add(incoming);
            AddLog($"Peer追加: {incoming.DisplayText}");
        }

        private static bool IsSamePeer(PeerInfo left, PeerInfo right)
        {
            if (!string.IsNullOrWhiteSpace(left.DeviceId) &&
                string.Equals(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(left.DisplayName) &&
                   !string.IsNullOrWhiteSpace(right.DisplayName) &&
                   string.Equals(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        private static void MergePeer(PeerInfo target, PeerInfo source)
        {
            target.DiscoveredByBle |= source.DiscoveredByBle;
            target.DiscoveredByWiFiDirect |= source.DiscoveredByWiFiDirect || !source.DiscoveredByBle;
            target.IsConnected |= source.IsConnected;
            target.IsTcpConnected |= source.IsTcpConnected;

            if (string.IsNullOrWhiteSpace(target.DeviceId))
            {
                target.DeviceId = source.DeviceId;
            }

            if (string.IsNullOrWhiteSpace(target.RemoteIpAddress))
            {
                target.RemoteIpAddress = source.RemoteIpAddress;
            }

            if (string.IsNullOrWhiteSpace(target.IpAddress))
            {
                target.IpAddress = source.IpAddress;
            }

            if (target.TcpPort <= 0)
            {
                target.TcpPort = source.TcpPort;
            }

            if (string.IsNullOrWhiteSpace(target.ShortSessionId))
            {
                target.ShortSessionId = source.ShortSessionId;
            }

            if (!target.IsEnabled.HasValue)
            {
                target.IsEnabled = source.IsEnabled;
            }

            if (string.IsNullOrWhiteSpace(target.DeviceKind))
            {
                target.DeviceKind = source.DeviceKind;
            }
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

                    if (!ReferenceEquals(item, peer) && !IsSamePeer(item, peer))
                    {
                        continue;
                    }

                    MergePeer(item, peer);
                    PeerList.Items[i] = item;
                    AddLog($"Peer表示更新: {item.DisplayText}");
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

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LogLevel effectiveLevel = level == LogLevel.Info
                    ? ClassifyLogMessage(message)
                    : level;

                if (effectiveLevel == LogLevel.Debug && ShowDebugLogCheckBox?.IsChecked != true)
                {
                    return;
                }

                string time = DateTime.Now.ToString("HH:mm:ss.fff");
                string log = $"[{time}] [{effectiveLevel}] {message}";

                _logLines.Add(log);

                if (_logLines.Count > MaxLogLines)
                {
                    int removeCount = _logLines.Count - MaxLogLines;
                    _logLines.RemoveRange(0, removeCount);
                }

                LogTextBox.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
                MoveLogCaretToEnd();
            });
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

        private static bool IsDebugLogMessage(string message)
        {
            string[] debugKeywords =
            {
                "Selector:",
                "Watcher Status",
                "Added",
                "Updated",
                "Removed",
                "EnumerationCompleted",
                "Stopped",
                "Kind:",
                "IsEnabled:",
                "InformationElements.Count",
                "LegacySettings.IsEnabled",
                "ListenStateDiscoverability",
                "LocalServiceName",
                "RemoteServiceName",
                "Socket.",
                "Socket作成",
                "DataWriter",
                "DataReader",
                "WriteUInt32",
                "WriteBytes",
                "StoreAsync",
                "FlushAsync",
                "平文Bytes",
                "暗号化後Bytes",
                "送信Bytes",
                "送信フレームBytes",
                "受信Bytes",
                "復号後Bytes",
                "length読み取り",
                "本文読み取り",
                "受信予定Bytes",
                "Decrypt",
                "MessageCrypto",
                "SendMessage_Click",
                "入力メッセージ",
                "選択Peer/IP",
                "ChatConnection接続状態",
                "SendAsync内でConnect",
                "MessageList",
                "Local IP:",
                "Local SessionId:",
                "Local TCP Port:",
                "蟷ｳ譁③ytes",
                "證怜捷蛹門ｾ沓ytes",
                "騾∽ｿ｡Bytes",
                "騾∽ｿ｡繝輔Ξ繝ｼ繝Bytes",
                "length隱ｭ縺ｿ蜿悶ｊ",
                "譛ｬ譁・ｪｭ縺ｿ蜿悶ｊ"
            };

            return debugKeywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
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
                "中止",
                "ありません",
                "読み取り不足",
                "本文不足",
                "螟ｱ謨・",
                "繧ｨ繝ｩ繝ｼ",
                "萓句､・",
                "荳肴ｭ｣",
                "蛻・妙"
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
                "謌仙粥",
                "螳御ｺ・",
                "謗･邯壽ｸ医∩",
                "騾∽ｿ｡謌仙粥",
                "蜿嶺ｿ｡",
                "RemoteIpAddress菫晏ｭ・"
            };

            return successKeywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.Select(LogTextBox.Text.Length, 0);
        }
    }
}
