using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace direct_module
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private DeviceWatcher? _watcher;
        private readonly List<PeerInfo> _peers = new();
        private readonly WiFiDirectManager _manager;

        public MainWindow()
        {
            InitializeComponent();

            _manager = new WiFiDirectManager();

            _manager.LogReceived += OnLogReceived;
            _manager.ConnectionRequested += OnConnectionRequested;
        }

        private void StartListener_Click(object sender, RoutedEventArgs e)
        {
            _manager.Start();

            AddLog("待ち受け開始ボタンを押しました");
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_watcher != null)
            {
                AddLog("すでに探索中です");
                return;
            }

            string selector = WiFiDirectDevice.GetDeviceSelector();

            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += Watcher_Added;
            _watcher.EnumerationCompleted += Watcher_EnumerationCompleted;

            _watcher.Start();

            AddLog("探索開始");
        }

        private void Watcher_Added(DeviceWatcher sender, DeviceInformation device)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PeerInfo peer = new PeerInfo
                {
                    DisplayName = string.IsNullOrWhiteSpace(device.Name)
                        ? "Unknown Wi-Fi Direct device"
                        : device.Name,

                    DeviceId = device.Id,
                    IsConnected = false
                };

                _peers.Add(peer);

                PeerList.Items.Add(peer.DisplayName);

                LogList.Items.Add($"発見: {peer.DisplayName}");
            });
        }

        private void Watcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            AddLog("探索が完了しました");
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
                LogList.Items.Add(message);
            });
        }
    }
}