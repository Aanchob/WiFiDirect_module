using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Storage.Streams;

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

        public async Task StartAsync(WiFiDirectScanSelectorType selectorType, int scanSeconds = 30)
        {
            if (_watcher != null)
            {
                LogReceived?.Invoke($"Wi-Fi Direct探索はすでに起動中です: Status={_watcher.Status}");
                return;
            }

            _currentSelectorType = selectorType;
            _devices.Clear();

            string selector = selectorType == WiFiDirectScanSelectorType.AssociationEndpoint
                ? WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint)
                : WiFiDirectDevice.GetDeviceSelector();

            LogReceived?.Invoke("Wi-Fi Direct探索開始");
            LogReceived?.Invoke($"Selector Type: {selectorType}");
            LogReceived?.Invoke($"Selector: {selector}");

            try
            {
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

                await Task.Delay(scanSeconds * 1000);

                LogReceived?.Invoke($"{scanSeconds}秒経過したのでWi-Fi Direct探索を停止します");
                Stop();
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct探索エラー: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                CleanupWatcher();
            }
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
                if (_watcher.Status == DeviceWatcherStatus.Created ||
                    _watcher.Status == DeviceWatcherStatus.Started ||
                    _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    _watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct探索停止エラー: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                CleanupWatcher();
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            _devices[device.Id] = device;

            string shortSessionId = TryExtractShortSessionId(device);
            string displayName = string.IsNullOrWhiteSpace(device.Name)
                ? "Unknown Wi-Fi Direct device"
                : device.Name;

            LogReceived?.Invoke("Added");
            LogReceived?.Invoke("---- Wi-Fi Direct Candidate ----");
            LogReceived?.Invoke($"Selector Type: {_currentSelectorType}");
            LogReceived?.Invoke($"Name: {FormatName(device.Name)}");
            LogReceived?.Invoke($"Id: {device.Id}");
            LogReceived?.Invoke($"Kind: {device.Kind}");
            LogReceived?.Invoke($"IsEnabled: {device.IsEnabled}");
            LogReceived?.Invoke(string.IsNullOrWhiteSpace(shortSessionId)
                ? "Wi-Fi Direct InformationElement読み取り不可: ShortSessionIdなし"
                : $"Wi-Fi Direct InformationElement取得: ShortSessionId={shortSessionId}");
            LogReceived?.Invoke("IsEnabledで除外せずPeerFoundへ流します");
            LogReceived?.Invoke($"Wi-Fi Direct Candidate発見: Name={displayName}, DeviceId={device.Id}");

            PeerInfo peer = new PeerInfo
            {
                DisplayName = displayName,
                WiFiDirectName = displayName,
                DeviceId = device.Id,
                DeviceKind = device.Kind.ToString(),
                IsEnabled = device.IsEnabled,
                DiscoveredByBle = false,
                DiscoveredByWiFiDirect = true,
                TcpPort = 0,
                ShortSessionId = shortSessionId,
                MatchKey = shortSessionId,
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

        private string TryExtractShortSessionId(DeviceInformation device)
        {
            try
            {
                foreach (var property in device.Properties)
                {
                    if (property.Value is string text && TryParseDchatPayload(text, out string sessionId))
                    {
                        return sessionId;
                    }

                    if (property.Value is IBuffer buffer)
                    {
                        string textFromBuffer = ReadBufferAsString(buffer);
                        if (TryParseDchatPayload(textFromBuffer, out string sessionIdFromBuffer))
                        {
                            return sessionIdFromBuffer;
                        }
                    }
                }

                LogReceived?.Invoke("Wi-Fi Direct InformationElement読み取り不可: DeviceInformation.PropertiesにDCHATなし");
                return "";
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("Wi-Fi Direct InformationElement読み取り失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                return "";
            }
        }

        private static bool TryParseDchatPayload(string text, out string shortSessionId)
        {
            shortSessionId = "";

            if (string.IsNullOrWhiteSpace(text) || !text.Contains("DCHAT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] parts = text.Split('|');

            if (parts.Length >= 3 && parts[0] == "DCHAT")
            {
                shortSessionId = parts[2];
                return !string.IsNullOrWhiteSpace(shortSessionId);
            }

            if (parts.Length >= 2 && parts[0] == "DCHAT")
            {
                shortSessionId = parts[1];
                return !string.IsNullOrWhiteSpace(shortSessionId);
            }

            return false;
        }

        private static string ReadBufferAsString(IBuffer buffer)
        {
            byte[] bytes = new byte[buffer.Length];

            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);

            return Encoding.UTF8.GetString(bytes);
        }

        private static string FormatName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "(empty)" : name;
        }
    }
}
