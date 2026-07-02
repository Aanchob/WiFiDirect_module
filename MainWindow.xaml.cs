using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using direct_module.Discovery;
using direct_module.Network;
using Microsoft.UI.Xaml;
using System;

namespace direct_module
{
    public sealed partial class MainWindow : Window
    {
        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;
        private readonly TcpServer _tcpServer = new();
        private readonly TcpClient _tcpClient = new();

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

            _discoveryManager.LogReceived += OnLogReceived;
            _discoveryManager.PeerFound += OnPeerFound;

            _tcpServer.LogReceived += OnLogReceived;
            _tcpServer.MessageReceived += message =>
            {
                AddLog($"受信メッセージ: {message}");
            };

            _tcpClient.LogReceived += OnLogReceived;
        }

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start();

            AddLog("待ち受け開始ボタンを押しました");
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StartScanAsync();
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
            await _tcpServer.StartAsync(LocalTcpPort);
        }

        private async void SendTcpToSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("TCP送信する相手を選択してください");
                return;
            }

            string ipAddress = TargetIpTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                AddLog("相手のIPアドレスを入力してください");
                return;
            }

            if (peer.TcpPort <= 0)
            {
                AddLog("相手のTCPポート番号が不正です");
                return;
            }

            string message = $"Hello from {Environment.MachineName}";

            AddLog($"TCP送信開始: {ipAddress}:{peer.TcpPort}");
            AddLog($"送信先Peer: {peer.DisplayText}");

            await _tcpClient.SendAsync(ipAddress, peer.TcpPort, message);
        }
        private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("接続する相手を選択してください");
                return;
            }

            AddLog($"接続開始: {peer.DisplayText}");

            await _manager.ConnectAsync(peer);
        }

        private async void OnPeerFound(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PeerList.Items.Add(peer);

                AddLog($"Peer追加: {peer.DisplayText}");
            });

            if (peer.DiscoveredByBle)
            {
                AddLog("BLEで相手を発見したため、Wi-Fi Direct探索を開始します");

                await _manager.StartScanAsync();
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

        private void AddLog(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string time = DateTime.Now.ToString("HH:mm:ss.fff");
                LogList.Items.Add($"[{time}] {message}");
            });
        }


    }
}