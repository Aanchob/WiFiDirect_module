using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using direct_module.Discovery;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace direct_module
{
    public sealed partial class MainWindow : Window
    {
        private readonly List<PeerInfo> _peers = new();

        private readonly DiscoveryManager _discoveryManager;
        private readonly WiFiDirectManager _manager;

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
        }

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start();

            AddLog("待ち受け開始ボタンを押しました");
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _manager.StartScan();
        }

        private void StartBleAdvertise_Click(object sender, RoutedEventArgs e)
        {
            _discoveryManager.StartAdvertise(Environment.MachineName);
        }

        private void StartBleScan_Click(object sender, RoutedEventArgs e)
        {
            _discoveryManager.StartScan();
        }

        private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
        {
            int index = PeerList.SelectedIndex;

            if (index < 0)
            {
                AddLog("接続する相手を選択してください");
                return;
            }

            if (index >= _peers.Count)
            {
                AddLog("選択された相手情報が見つかりません");
                return;
            }

            PeerInfo peer = _peers[index];

            AddLog($"接続開始: {peer.DisplayName}");

            await _manager.ConnectAsync(peer);
        }

        private void OnPeerFound(PeerInfo peer)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _peers.Add(peer);

                PeerList.Items.Add(peer.DisplayName);
            });
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