using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
        private readonly Dictionary<string, DeviceInformation> _devices = new();
        private readonly object _devicesGate = new();
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private DeviceWatcher? _watcher;
        private CancellationTokenSource? _scanTimeoutCts;
        private TaskCompletionSource<bool>? _stopCompletion;
        private WiFiDirectScanSelectorType _currentSelectorType;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;

        public Task StartAsync(int scanSeconds = 30) =>
            StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);

        public Task StartDefaultAsync(int scanSeconds = 30) =>
            StartAsync(WiFiDirectScanSelectorType.Default, scanSeconds);

        public Task StartAssociationEndpointAsync(int scanSeconds = 0) =>
            StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);

        public async Task StartAsync(WiFiDirectScanSelectorType selectorType, int scanSeconds = 30)
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (_watcher != null &&
                    _currentSelectorType == selectorType &&
                    (_watcher.Status == DeviceWatcherStatus.Started ||
                     _watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                {
                    LogReceived?.Invoke($"Wi-Fi Directスキャンは開始済みです: Selector={selectorType}, Status={_watcher.Status}");
                    return;
                }

                if (_watcher != null)
                {
                    LogReceived?.Invoke($"Wi-Fi Directスキャンを切り替えます: {_currentSelectorType} -> {selectorType}");
                    await StopCoreAsync();
                }

                _currentSelectorType = selectorType;
                lock (_devicesGate)
                {
                    _devices.Clear();
                }

                string selector = selectorType == WiFiDirectScanSelectorType.AssociationEndpoint
                    ? WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint)
                    : WiFiDirectDevice.GetDeviceSelector();

                var watcher = DeviceInformation.CreateWatcher(selector);
                _watcher = watcher;
                watcher.Added += OnDeviceAdded;
                watcher.Updated += OnDeviceUpdated;
                watcher.Removed += OnDeviceRemoved;
                watcher.EnumerationCompleted += OnEnumerationCompleted;
                watcher.Stopped += OnStopped;
                watcher.Start();

                if (watcher.Status != DeviceWatcherStatus.Started &&
                    watcher.Status != DeviceWatcherStatus.EnumerationCompleted)
                {
                    throw new InvalidOperationException($"DeviceWatcherを開始できませんでした。Status={watcher.Status}");
                }

                LogReceived?.Invoke($"Wi-Fi Directスキャン開始完了: Selector={selectorType}, Status={watcher.Status}");

                if (scanSeconds > 0)
                {
                    _scanTimeoutCts = new CancellationTokenSource();
                    _ = StopAfterDelayAsync(scanSeconds, _scanTimeoutCts.Token);
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Directスキャン開始失敗: {ex.GetType().Name}: {ex.Message}");
                CleanupWatcher(_watcher);
                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                await StopCoreAsync();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public void Stop()
        {
            DeviceWatcher? watcher = _watcher;
            if (watcher == null)
            {
                return;
            }

            try
            {
                CancelScanTimeout();
                if (watcher.Status == DeviceWatcherStatus.Created ||
                    watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Directスキャン停止失敗: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CleanupWatcher(watcher);
            }
        }

        private async Task StopCoreAsync()
        {
            DeviceWatcher? watcher = _watcher;
            if (watcher == null)
            {
                return;
            }

            CancelScanTimeout();
            try
            {
                if (watcher.Status == DeviceWatcherStatus.Created ||
                    watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopCompletion = completion;
                    watcher.Stop();

                    if (watcher.Status != DeviceWatcherStatus.Stopped &&
                        watcher.Status != DeviceWatcherStatus.Aborted)
                    {
                        Task completed = await Task.WhenAny(completion.Task, Task.Delay(StopTimeout));
                        if (completed != completion.Task)
                        {
                            LogReceived?.Invoke($"Wi-Fi Directスキャン停止待機がタイムアウトしました: Status={watcher.Status}");
                        }
                    }
                }
            }
            finally
            {
                CleanupWatcher(watcher);
                LogReceived?.Invoke($"Wi-Fi Directスキャン停止完了: Status={watcher.Status}");
            }
        }

        private async Task StopAfterDelayAsync(int scanSeconds, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(scanSeconds), cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    await StopAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Directスキャン自動停止失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            lock (_devicesGate)
            {
                _devices[device.Id] = device;
            }

            PublishPeer(device, "Added");
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            DeviceInformation? device;
            lock (_devicesGate)
            {
                if (!_devices.TryGetValue(update.Id, out device))
                {
                    return;
                }

                device.Update(update);
            }

            // Added時点で名前やInformationElementが未確定でも、更新後の完全な候補を再通知する。
            PublishPeer(device, "Updated");
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            lock (_devicesGate)
            {
                _devices.Remove(update.Id);
            }

            LogReceived?.Invoke($"Wi-Fi Direct候補削除: DeviceId={update.Id}");
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            int count;
            lock (_devicesGate)
            {
                count = _devices.Count;
            }

            LogReceived?.Invoke($"Wi-Fi Direct初期列挙完了: Selector={_currentSelectorType}, CandidateCount={count}");
        }

        private void OnStopped(DeviceWatcher sender, object args)
        {
            LogReceived?.Invoke($"Wi-Fi DirectスキャンStopped: Status={sender.Status}");
            if (ReferenceEquals(sender, _watcher))
            {
                _stopCompletion?.TrySetResult(true);
                CleanupWatcher(sender);
            }
            else
            {
                // 古いWatcherの遅延イベントで現在のWatcherを破棄しない。
                DetachWatcher(sender);
            }
        }

        private void PublishPeer(DeviceInformation device, string changeType)
        {
            (string shortSessionId, string dchatInformation) = TryExtractDchatIdentity(device);
            string displayName = string.IsNullOrWhiteSpace(device.Name)
                ? "Unknown Wi-Fi Direct device"
                : device.Name;

            LogReceived?.Invoke(
                $"Wi-Fi Direct候補{changeType}: Name={displayName}, DeviceId={device.Id}, " +
                $"Kind={device.Kind}, IsEnabled={device.IsEnabled}, ShortSessionId={shortSessionId}");

            PeerFound?.Invoke(new PeerInfo
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
                DchatInformation = dchatInformation,
                IsConnected = false
            });
        }

        private void CleanupWatcher(DeviceWatcher? watcher)
        {
            if (watcher == null)
            {
                return;
            }

            DetachWatcher(watcher);
            if (ReferenceEquals(_watcher, watcher))
            {
                _watcher = null;
                _stopCompletion = null;
            }
        }

        private void DetachWatcher(DeviceWatcher watcher)
        {
            watcher.Added -= OnDeviceAdded;
            watcher.Updated -= OnDeviceUpdated;
            watcher.Removed -= OnDeviceRemoved;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            watcher.Stopped -= OnStopped;
        }

        private void CancelScanTimeout()
        {
            CancellationTokenSource? timeout = _scanTimeoutCts;
            _scanTimeoutCts = null;
            if (timeout == null)
            {
                return;
            }

            timeout.Cancel();
            timeout.Dispose();
        }

        private (string ShortSessionId, string DchatInformation) TryExtractDchatIdentity(DeviceInformation device)
        {
            try
            {
                foreach (KeyValuePair<string, object> property in device.Properties)
                {
                    if (property.Value is string text &&
                        TryParseDchatPayload(text, out string sessionId, out string payload))
                    {
                        return (sessionId, payload);
                    }

                    if (property.Value is IBuffer buffer)
                    {
                        string bufferText = ReadBufferAsString(buffer);
                        if (TryParseDchatPayload(bufferText, out string bufferSessionId, out string bufferPayload))
                        {
                            return (bufferSessionId, bufferPayload);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct識別情報の読み取り失敗: {ex.GetType().Name}: {ex.Message}");
            }

            return ("", "");
        }

        private static bool TryParseDchatPayload(
            string text,
            out string shortSessionId,
            out string dchatInformation)
        {
            shortSessionId = "";
            dchatInformation = "";
            if (string.IsNullOrWhiteSpace(text) || !text.Contains("DCHAT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int markerIndex = text.IndexOf("DCHAT", StringComparison.OrdinalIgnoreCase);
            string payload = text[markerIndex..].TrimEnd('\0');
            string[] parts = payload.Split('|');
            if (parts.Length >= 3 && string.Equals(parts[0], "DCHAT", StringComparison.OrdinalIgnoreCase))
            {
                shortSessionId = parts[2].TrimEnd('\0');
                dchatInformation = $"DCHAT|{parts[1]}|{shortSessionId}";
                return !string.IsNullOrWhiteSpace(shortSessionId);
            }

            if (parts.Length >= 2 && string.Equals(parts[0], "DCHAT", StringComparison.OrdinalIgnoreCase))
            {
                shortSessionId = parts[1].TrimEnd('\0');
                dchatInformation = $"DCHAT|{shortSessionId}";
                return !string.IsNullOrWhiteSpace(shortSessionId);
            }

            return false;
        }

        private static string ReadBufferAsString(IBuffer buffer)
        {
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
