using direct_module.Database;
using direct_module.Discovery;
using direct_module.Network;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Graphics;
using Windows.Networking.Sockets;
using WinRT.Interop;
using direct_module.Models;

namespace direct_module
{
    public sealed partial class MainWindow : Window
    {
        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private ChatConnection? _chatConnection;
        private bool _isPreparingChatTcp;
        private bool _searchedOnStartup = false;

        private readonly DatabaseService _databaseService;
        private readonly ChatRepository _chatRepository;
        private readonly UserRepository _userRepository;

        private readonly Guid _localSessionId = Guid.NewGuid();
        private const int LocalTcpPort = 50001;

        private string _myDeviceId = "";



        public MainWindow()
        {
            InitializeComponent();

            LocalUserNameText.Text = Environment.MachineName;

            Title = "NOVA Chat";
            ResizeWindow(1440, 920);
            UpdateSelectedPeerDetails(null);

            _databaseService = new DatabaseService();
            _chatRepository = new ChatRepository(_databaseService);

            _userRepository = new UserRepository(_databaseService);
            InitializeMyUser();


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

            this.Activated += MainWindow_Activated;
        }

        private string CreateConversationId(string myDeviceId, string peerDeviceId)
        {
            return string.CompareOrdinal(myDeviceId, peerDeviceId) < 0
                ? $"{myDeviceId}_{peerDeviceId}"
                : $"{peerDeviceId}_{myDeviceId}";
        }

        private void LoadChatHistory(string conversationId)
        {
            var messages = _chatRepository.GetMessages(conversationId);

            void RenderHistory()
            {
                MessageList.Items.Clear();

                foreach (var message in messages)
                {
                    string time = message.SendTime.ToString("HH:mm:ss");
                    string sender = message.IsMine ? "自分" : "相手";
                    MessageList.Items.Add($"[{time}] {sender}: {message.Message}");
                }

                if (MessageList.Items.Count > 0)
                {
                    MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
                }
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                RenderHistory();
                return;
            }

            DispatcherQueue.TryEnqueue(RenderHistory);
        }

        private void ResizeWindow(int width, int height)
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(width, height));
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (_searchedOnStartup)
            {
                return;
            }

            _searchedOnStartup = true;
            this.Activated -= MainWindow_Activated;

            AddLog("起動時の相手探索開始");
            AddLog("Wi-Fi Direct広告+待ち受け開始");
            _manager.Start();

            AddLog("BLE広告開始");
            StartBleAdvertiseCore();

            AddLog("BLEスキャン開始");
            _discoveryManager.StartScan();

            AddLog("AssociationEndpoint探索開始");
            ClearStaleWiFiDirectPeers();
            await _manager.StartAssociationEndpointScanAsync();

            AddLog("起動時の相手探索処理を開始しました");
        }

        private async void SearchPeers_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            await RunWithCooldownAsync(button, async () =>
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
            });
        }

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start();
            AddLog("Wi-Fi Direct広告+待ち受け開始ボタンを押しました");
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
                MarkSelectedPeerTcpState(chatConnection.IsConnected);
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

            PeerInfo? peer = PeerList.SelectedItem as PeerInfo;

            _chatRepository.SaveMessage(new ChatMessage
            {
                ConversationId = peer != null
                    ? CreateConversationId(_myDeviceId, peer.DeviceId)
                    : "",

                SenderId = _myDeviceId,
                SenderName = Environment.MachineName,

                ReceiverId = peer?.DeviceId ?? "",
                ReceiverName = peer?.DisplayName ?? ipAddress,

                Message = message,
                SendTime = DateTime.Now,
                IsMine = true
            });
            AddChatMessage($"自分: {message}");

            MessageTextBox.Text = "";
            MessageTextBox.Focus(FocusState.Programmatic);
        }

        private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください");
                return;
            }

            await ConnectPeerAsync(peer);
        }

        private async void ConnectPeerItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PeerInfo peer })
            {
                AddLog("接続する相手を取得できませんでした");
                return;
            }

            PeerList.SelectedItem = peer;
            UpdateSelectedPeerDetails(peer);
            await ConnectPeerAsync(peer);
        }

        private async System.Threading.Tasks.Task ConnectPeerAsync(PeerInfo peer)
        {
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
            LogTextBox.Text = string.Empty;
        }

        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PeerInfo? peer = PeerList.SelectedItem as PeerInfo;

            UpdateSelectedPeerDetails(peer);

            if (peer == null)
            {
                return;
            }

            // DeviceIdが無い場合は履歴を読み込まない
            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                return;
            }

            // この相手とのConversationIdを作成
            string conversationId = CreateConversationId(_myDeviceId, peer.DeviceId);

            // この相手との履歴だけ表示
            LoadChatHistory(conversationId);
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
                SavePeer(peer); 
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
            SavePeer(peer);
            AddLog($"接続要求: {peer.DisplayName}");
        }

        private async void OnWiFiDirectConnected(PeerInfo peer)
        {
            AddLog($"Wi-Fi Direct接続完了通知: {peer.DisplayText}");
            RefreshPeerDisplay(peer);
            await EnsureTcpServerStartedAsync("Wi-Fi Direct接続完了");
            await PrepareChatTcpConnectionAsync(peer);
            string conversationId =
    CreateConversationId(_myDeviceId, peer.DeviceId);

            LoadChatHistory(conversationId);
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_chatConnection?.IsConnected == true)
                {
                    AddLog("既存のChat TCP接続があるため、後から来たTCP接続は閉じます");
                    socket.Dispose();
                    return;
                }

                AddLog("Chat TCP受信側接続を保持します");
                ReplaceChatConnection(new ChatConnection());

                if (_chatConnection == null)
                {
                    socket.Dispose();
                    return;
                }

                await _chatConnection.AttachAcceptedSocketAsync(socket);
                MarkSelectedPeerTcpState(_chatConnection.IsConnected);
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
            PeerInfo? peer = PeerList.SelectedItem as PeerInfo;

            _chatRepository.SaveMessage(new ChatMessage
            {
                ConversationId = peer != null
                    ? CreateConversationId(_myDeviceId, peer.DeviceId)
                    : "",

                SenderId = peer?.DeviceId ?? "",
                SenderName = peer?.DisplayName ?? "相手",

                ReceiverId = _myDeviceId,
                ReceiverName = Environment.MachineName,

                Message = message,
                SendTime = DateTime.Now,
                IsMine = false
            });

            AddLog("TCP受信メッセージを復号しました");
            AddLog($"受信文字数: {message.Length}");
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
                    UpdateSelectedPeerDetails(peer);
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

                UpdatePeerCount();
                UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
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
                UpdatePeerCount();
                UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
                AddLog($"Peer統合: {existing.DisplayText}");
                return;
            }

            PeerList.Items.Add(incoming);
            UpdatePeerCount();
            if (PeerList.SelectedItem == null)
            {
                PeerList.SelectedItem = incoming;
            }

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
                    UpdatePeerCount();
                    UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
                    AddLog($"Peer表示更新: {item.DisplayText}");
                    return;
                }

                if (peer.IsConnected)
                {
                    PeerList.Items.Add(peer);
                    UpdatePeerCount();
                    if (PeerList.SelectedItem == null)
                    {
                        PeerList.SelectedItem = peer;
                    }

                    AddLog($"接続済みPeerを一覧に追加: {peer.DisplayText}");
                    AddLog("受信acceptで作成されたPeerのため、TCP返信先として選択できます");
                }
            });
        }

        private void UpdatePeerCount()
        {
            PeerCountText.Text = $"検出済み {PeerList.Items.Count} / 接続待機";
        }

        private void UpdateSelectedPeerDetails(PeerInfo? peer)
        {
            if (peer == null)
            {
                SelectedPeerAvatarText.Text = "WD";
                SelectedPeerNameText.Text = "未選択";
                SelectedPeerStatusText.Text = "相手を選択してください";
                SelectedPeerSourceText.Text = "BLE / Wi-Fi Direct の検出状況がここに表示されます。";
                SelectedPeerIpText.Text = "Remote IP: -";
                SelectedPeerSessionText.Text = "Session: -";
                SelectedPeerDeviceText.Text = "DeviceId: -";
                ChatHeaderAvatarText.Text = "WD";
                ChatHeaderTitleText.Text = "Wi-Fi Direct Chat";
                ChatHeaderStatusText.Text = "相手を選択してください";
                SelectedPeerProgress.Value = 10;
                SelectedPeerOnlineDot.Fill = new SolidColorBrush(Colors.Gray);
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(peer.DisplayName)
                ? "Unknown peer"
                : peer.DisplayName;
            string initials = CreateInitials(displayName);
            string remoteIp = !string.IsNullOrWhiteSpace(peer.RemoteIpAddress)
                ? peer.RemoteIpAddress
                : !string.IsNullOrWhiteSpace(peer.IpAddress) ? peer.IpAddress : "-";
            string status = peer.IsTcpConnected
                ? "TCP接続済み"
                : peer.IsConnected ? "Wi-Fi Direct接続済み / TCP準備中" : "接続前";

            SelectedPeerAvatarText.Text = initials;
            SelectedPeerNameText.Text = displayName;
            SelectedPeerStatusText.Text = status;
            SelectedPeerSourceText.Text = peer.DisplayText;
            SelectedPeerIpText.Text = $"Remote IP: {remoteIp}";
            SelectedPeerSessionText.Text = $"Session: {(string.IsNullOrWhiteSpace(peer.ShortSessionId) ? "-" : peer.ShortSessionId)} / Port: {(peer.TcpPort > 0 ? peer.TcpPort : LocalTcpPort)}";
            SelectedPeerDeviceText.Text = $"DeviceId: {(string.IsNullOrWhiteSpace(peer.DeviceId) ? "-" : peer.DeviceId)}";
            ChatHeaderAvatarText.Text = initials;
            ChatHeaderTitleText.Text = displayName;
            ChatHeaderStatusText.Text = status;
            SelectedPeerProgress.Value = peer.IsTcpConnected ? 100 : peer.IsConnected ? 72 : peer.DiscoveredByBle || peer.DiscoveredByWiFiDirect ? 38 : 10;
            SelectedPeerOnlineDot.Fill = new SolidColorBrush(peer.IsTcpConnected || peer.IsConnected ? Colors.LawnGreen : Colors.Gray);
        }

        private static string CreateInitials(string displayName)
        {
            string trimmed = displayName.Trim();

            if (trimmed.Length == 0)
            {
                return "WD";
            }

            return trimmed.Length == 1
                ? trimmed.ToUpperInvariant()
                : trimmed.Substring(0, 2).ToUpperInvariant();
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
        private void MessageTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SendTcpToSelected_Click(SendMessageButton, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            // 後でファイル選択機能を実装
        }
        private async System.Threading.Tasks.Task RunWithCooldownAsync(
            Button button,
            Func<System.Threading.Tasks.Task> action,
            int cooldownMilliseconds = 3000)
        {
            if (!button.IsEnabled)
            {
                return;
            }

            button.IsEnabled = false;

            try
            {
                await action();
            }
            finally
            {
                await System.Threading.Tasks.Task.Delay(cooldownMilliseconds);
                button.IsEnabled = true;
            }
        }
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // 後で設定画面を開く
        }

        /// <summary>
        /// 相手情報をUsersテーブルへ保存
        /// </summary>
        private void SavePeer(PeerInfo peer)
        {
            if (string.IsNullOrWhiteSpace(peer.DeviceId))
                return;

            User user = new User
            {
                DeviceId = peer.DeviceId,
                MachineName = peer.DisplayName,
                DisplayName = peer.DisplayName,
                CreatedAt = DateTime.Now
            };

            _userRepository.Save(user);

            AddLog($"Usersテーブルへ保存: {peer.DisplayName}");
        }

        /// <summary>
        /// 自分のDeviceIdを取得（なければ新規作成）
        /// </summary>
        private void InitializeMyUser()
        {
            var user = _userRepository.GetByMachineName(Environment.MachineName);

            if (user == null)
            {
                user = new User
                {
                    DeviceId = Guid.NewGuid().ToString(),
                    MachineName = Environment.MachineName,
                    DisplayName = Environment.MachineName,
                    CreatedAt = DateTime.Now
                };

                _userRepository.Save(user);
            }

            _myDeviceId = user.DeviceId;

            AddLog($"自分のDeviceId: {_myDeviceId}");
        }
    }
}
