using direct_module.WiFiDirect.Models;
using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectScanner
    {
        private DeviceWatcher? _watcher;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;
        public async Task StartAsync(int scanSeconds = 10)
        {
            if (_watcher != null)
            {
                LogReceived?.Invoke("すでに探索中です");
                return;
            }

            string selector = WiFiDirectDevice.GetDeviceSelector();

            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += OnDeviceAdded;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnStopped;

            LogReceived?.Invoke($"探索開始: {scanSeconds}秒間スキャンします");

            LogReceived?.Invoke($"Watcher Status before Start: {_watcher.Status}");

            _watcher.Start();

            LogReceived?.Invoke($"Watcher Status after Start: {_watcher.Status}");

            await Task.Delay(scanSeconds * 1000);

            Stop();
        }

        public void Stop()
        {
            if (_watcher == null)
            {
                LogReceived?.Invoke("探索は開始されていません");
                return;
            }

            if (_watcher.Status == DeviceWatcherStatus.Started ||
                _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
                LogReceived?.Invoke("探索停止");
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            PeerInfo peer = new PeerInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(device.Name)
                    ? "Unknown Wi-Fi Direct device"
                    : device.Name,

                DeviceId = device.Id,
                DiscoveredByBle = false,
                TcpPort = 0,
                ShortSessionId = "",
                IsConnected = false
            };

            LogReceived?.Invoke($"発見: {peer.DisplayName}");
            LogReceived?.Invoke($"Wi-Fi Direct DeviceId: {peer.DeviceId}");

            PeerFound?.Invoke(peer);
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke("初回探索が完了しました。スキャンは継続中です");
        }

        private void OnStopped(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke("探索が停止しました");

            _watcher = null;
        }
    }
}