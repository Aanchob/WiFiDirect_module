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
        public async Task StartAsync(int scanSeconds = 30)
        {
            if (_watcher != null)
            {
                LogReceived?.Invoke("すでに探索中です");
                return;
            }

            string selector = WiFiDirectDevice.GetDeviceSelector(
                WiFiDirectDeviceSelectorType.AssociationEndpoint
            );
            LogReceived?.Invoke($"Wi-Fi Direct Selector Type: AssociationEndpoint");
            LogReceived?.Invoke($"Wi-Fi Direct Selector: {selector}");


            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += OnDeviceAdded;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnStopped;

            LogReceived?.Invoke($"探索開始: {scanSeconds}秒間スキャンします");

            _watcher.Start();

            LogReceived?.Invoke($"Watcher Status after Start: {_watcher.Status}");

            await Task.Delay(scanSeconds * 1000);

            LogReceived?.Invoke($"{scanSeconds}秒経過したので探索を停止します");

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
            LogReceived?.Invoke("---- Wi-Fi Direct Candidate ----");
            LogReceived?.Invoke($"Name: {device.Name}");
            LogReceived?.Invoke($"Id: {device.Id}");
            LogReceived?.Invoke($"Kind: {device.Kind}");
            LogReceived?.Invoke($"IsEnabled: {device.IsEnabled}");

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

            PeerFound?.Invoke(peer);
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke($"初回探索完了: Status={sender.Status}");
        }

        private void OnStopped(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke($"Wi-Fi Direct探索停止: Status={sender.Status}");

            _watcher = null;
        }
    }
}