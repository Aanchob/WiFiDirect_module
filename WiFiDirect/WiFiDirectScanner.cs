using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public enum WiFiDirectScanSelectorType
    {
        Default,
        AssociationEndpoint
    }

    public class WiFiDirectScanner
    {
        private readonly Dictionary<string, DeviceInformation> _devices = new();
        private DeviceWatcher? _watcher;
        private CancellationTokenSource? _scanTimeoutCts;
        private WiFiDirectScanSelectorType _currentSelectorType;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;

        public Task StartAsync(int scanSeconds = 30)
        {
            return StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);
        }

        public Task StartDefaultAsync(int scanSeconds = 30)
        {
            return StartAsync(WiFiDirectScanSelectorType.Default, scanSeconds);
        }

        public Task StartAssociationEndpointAsync(int scanSeconds = 30)
        {
            return StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);
        }

        public Task StartAsync(WiFiDirectScanSelectorType selectorType, int scanSeconds = 30)
        {
            if (_watcher != null)
            {
                if (_watcher.Status == DeviceWatcherStatus.Started ||
                    _watcher.Status == DeviceWatcherStatus.Created)
                {
                    LogReceived?.Invoke($"Wi-Fi Direct探索はすでに起動中です: Status={_watcher.Status}");
                    return Task.CompletedTask;
                }

                LogReceived?.Invoke($"前回のWi-Fi Direct探索を整理します: Status={_watcher.Status}");
                CleanupWatcher();
            }

            _currentSelectorType = selectorType;
            _devices.Clear();

            try
            {
                string selector = selectorType == WiFiDirectScanSelectorType.AssociationEndpoint
                    ? WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint)
                    : WiFiDirectDevice.GetDeviceSelector();

                LogReceived?.Invoke("Wi-Fi Direct探索開始");
                LogReceived?.Invoke($"Selector Type: {selectorType}");
                LogReceived?.Invoke($"Selector: {selector}");

                _watcher = DeviceInformation.CreateWatcher(selector);
                _watcher.Added += OnDeviceAdded;
                _watcher.Updated += OnDeviceUpdated;
                _watcher.Removed += OnDeviceRemoved;
                _watcher.EnumerationCompleted += OnEnumerationCompleted;
                _watcher.Stopped += OnStopped;

                LogReceived?.Invoke($"Watcher Status before Start: {_watcher.Status}");
                LogReceived?.Invoke($"探索時間: {scanSeconds}秒");

                _watcher.Start();

                LogReceived?.Invoke($"Watcher Status after Start: {_watcher.Status}");

                _scanTimeoutCts = new CancellationTokenSource();
                _ = StopAfterDelayAsync(scanSeconds, _scanTimeoutCts.Token);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct探索エラー: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                CleanupWatcher();
            }

            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (_watcher == null)
            {
                LogReceived?.Invoke("Wi-Fi Direct探索は開始されていません");
                return;
            }

            LogReceived?.Invoke($"Wi-Fi Direct探索停止要求: Status={_watcher.Status}");

            try
            {
                CancelScanTimeout();

                if (_watcher.Status == DeviceWatcherStatus.Created ||
                    _watcher.Status == DeviceWatcherStatus.Started ||
                    _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcher.Stop();
                }

                if (_watcher.Status == DeviceWatcherStatus.Stopped ||
                    _watcher.Status == DeviceWatcherStatus.Aborted ||
                    _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    CleanupWatcher();
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct探索停止エラー: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                CleanupWatcher();
            }
        }

        private async Task StopAfterDelayAsync(int scanSeconds, CancellationToken cancellationToken)
        {
            if (scanSeconds <= 0)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(scanSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LogReceived?.Invoke($"{scanSeconds}秒経過したのでWi-Fi Direct探索を停止します");
            Stop();
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            _devices[device.Id] = device;

            LogReceived?.Invoke("Added");
            LogReceived?.Invoke("---- Wi-Fi Direct Candidate ----");
            LogReceived?.Invoke($"Selector Type: {_currentSelectorType}");
            LogReceived?.Invoke($"Name: {FormatName(device.Name)}");
            LogReceived?.Invoke($"Id: {device.Id}");
            LogReceived?.Invoke($"Kind: {device.Kind}");
            LogReceived?.Invoke($"IsEnabled: {device.IsEnabled}");
            LogReceived?.Invoke("IsEnabledで除外せずPeerFoundへ流します");

            PeerInfo peer = new PeerInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(device.Name)
                    ? "Unknown Wi-Fi Direct device"
                    : device.Name,
                DeviceId = device.Id,
                DeviceKind = device.Kind.ToString(),
                IsEnabled = device.IsEnabled,
                DiscoveredByBle = false,
                DiscoveredByWiFiDirect = true,
                TcpPort = 0,
                ShortSessionId = "",
                IsConnected = false
            };

            PeerFound?.Invoke(peer);
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            LogReceived?.Invoke("Updated");
            LogReceived?.Invoke($"Updated Id: {update.Id}");

            if (_devices.TryGetValue(update.Id, out DeviceInformation? device))
            {
                device.Update(update);
                LogReceived?.Invoke($"Name: {FormatName(device.Name)}");
                LogReceived?.Invoke($"Kind: {device.Kind}");
                LogReceived?.Invoke($"IsEnabled: {device.IsEnabled}");
            }
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            LogReceived?.Invoke("Removed");
            LogReceived?.Invoke($"Removed Id: {update.Id}");
            _devices.Remove(update.Id);
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke("EnumerationCompleted");
            LogReceived?.Invoke($"Wi-Fi Direct初回探索完了: Status={sender.Status}, CandidateCount={_devices.Count}");
        }

        private void OnStopped(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke("Stopped");
            LogReceived?.Invoke($"Wi-Fi Direct探索停止: Status={sender.Status}");
            CleanupWatcher();
        }

        private void CleanupWatcher()
        {
            CancelScanTimeout();

            if (_watcher == null)
            {
                return;
            }

            _watcher.Added -= OnDeviceAdded;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher.Removed -= OnDeviceRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnStopped;
            _watcher = null;
        }

        private void CancelScanTimeout()
        {
            if (_scanTimeoutCts == null)
            {
                return;
            }

            _scanTimeoutCts.Cancel();
            _scanTimeoutCts.Dispose();
            _scanTimeoutCts = null;
        }

        private static string FormatName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "(empty)" : name;
        }
    }
}
