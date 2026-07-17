using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;
using Windows.Storage.Streams;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectAdvertiser
    {
        private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(10);
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private WiFiDirectAdvertisementPublisher? _publisher;
        private TaskCompletionSource<WiFiDirectAdvertisementPublisherStatus>? _statusChanged;
        private string _displayName = "";
        private string _shortSessionId = "";
        private bool _autonomousGroupOwner;

        public event Action<string>? LogReceived;

        public async Task StartAsync(
            bool listenerRegistered,
            string displayName = "",
            string shortSessionId = "",
            bool autonomousGroupOwner = false)
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (_publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started &&
                    string.Equals(_displayName, displayName, StringComparison.Ordinal) &&
                    string.Equals(_shortSessionId, shortSessionId, StringComparison.Ordinal) &&
                    _autonomousGroupOwner == autonomousGroupOwner)
                {
                    LogReceived?.Invoke("Wi-Fi Direct Advertisement はすでに開始済みです");
                    return;
                }

                if (_publisher != null)
                {
                    await StopCoreAsync();
                }

                var publisher = new WiFiDirectAdvertisementPublisher();
                _publisher = publisher;
                publisher.StatusChanged += OnStatusChanged;
                publisher.Advertisement.ListenStateDiscoverability =
                    WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                publisher.Advertisement.IsAutonomousGroupOwnerEnabled = autonomousGroupOwner;
                publisher.Advertisement.SupportedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);

                TryAddAppInformationElement(publisher.Advertisement, displayName, shortSessionId);

                LogReceived?.Invoke($"Advertisement開始要求: AutonomousGO={autonomousGroupOwner}, Listener={listenerRegistered}");
                _statusChanged = CreateStatusCompletionSource();
                publisher.Start();

                if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    LogReceived?.Invoke("Advertisement開始完了: Started");
                    SaveConfiguration(displayName, shortSessionId, autonomousGroupOwner);
                    return;
                }

                WiFiDirectAdvertisementPublisherStatus status = await WaitForStatusAsync(
                    publisher,
                    WiFiDirectAdvertisementPublisherStatus.Started,
                    StateChangeTimeout);

                if (status != WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    throw new InvalidOperationException($"Advertisementを開始できませんでした。Status={status}");
                }

                LogReceived?.Invoke("Advertisement開始完了: Started");
                SaveConfiguration(displayName, shortSessionId, autonomousGroupOwner);
            }
            catch
            {
                CleanupPublisher(_publisher);
                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task RestartAsync(
            bool listenerRegistered,
            string displayName = "",
            string shortSessionId = "",
            bool autonomousGroupOwner = false)
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                await StopCoreAsync();

                var publisher = new WiFiDirectAdvertisementPublisher();
                _publisher = publisher;
                publisher.StatusChanged += OnStatusChanged;
                publisher.Advertisement.ListenStateDiscoverability =
                    WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                publisher.Advertisement.IsAutonomousGroupOwnerEnabled = autonomousGroupOwner;
                publisher.Advertisement.SupportedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);
                TryAddAppInformationElement(publisher.Advertisement, displayName, shortSessionId);

                LogReceived?.Invoke($"Advertisement再開始要求: AutonomousGO={autonomousGroupOwner}, Listener={listenerRegistered}");
                _statusChanged = CreateStatusCompletionSource();
                publisher.Start();

                WiFiDirectAdvertisementPublisherStatus status = publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started
                    ? publisher.Status
                    : await WaitForStatusAsync(publisher, WiFiDirectAdvertisementPublisherStatus.Started, StateChangeTimeout);

                if (status != WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    throw new InvalidOperationException($"Advertisementを再開始できませんでした。Status={status}");
                }

                LogReceived?.Invoke("Advertisement再開始完了: Started");
                SaveConfiguration(displayName, shortSessionId, autonomousGroupOwner);
            }
            catch
            {
                CleanupPublisher(_publisher);
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
            WiFiDirectAdvertisementPublisher? publisher = _publisher;
            if (publisher == null)
            {
                return;
            }

            try
            {
                publisher.Stop();
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Advertisement停止失敗: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CleanupPublisher(publisher);
            }
        }

        private async Task StopCoreAsync()
        {
            WiFiDirectAdvertisementPublisher? publisher = _publisher;
            if (publisher == null)
            {
                return;
            }

            try
            {
                if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started ||
                    publisher.Status == WiFiDirectAdvertisementPublisherStatus.Created)
                {
                    _statusChanged = CreateStatusCompletionSource();
                    publisher.Stop();

                    if (publisher.Status != WiFiDirectAdvertisementPublisherStatus.Stopped)
                    {
                        await WaitForStatusAsync(
                            publisher,
                            WiFiDirectAdvertisementPublisherStatus.Stopped,
                            StateChangeTimeout);
                    }
                }

                LogReceived?.Invoke($"Advertisement停止完了: {publisher.Status}");
            }
            finally
            {
                CleanupPublisher(publisher);
            }
        }

        private async Task<WiFiDirectAdvertisementPublisherStatus> WaitForStatusAsync(
            WiFiDirectAdvertisementPublisher publisher,
            WiFiDirectAdvertisementPublisherStatus expectedStatus,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (publisher.Status != expectedStatus)
            {
                if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                {
                    return publisher.Status;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Advertisement状態待機がタイムアウトしました。Expected={expectedStatus}, Actual={publisher.Status}");
                }

                TaskCompletionSource<WiFiDirectAdvertisementPublisherStatus> completion =
                    _statusChanged ??= CreateStatusCompletionSource();
                Task completed = await Task.WhenAny(completion.Task, Task.Delay(remaining));
                if (completed != completion.Task)
                {
                    throw new TimeoutException($"Advertisement状態待機がタイムアウトしました。Expected={expectedStatus}, Actual={publisher.Status}");
                }

                _statusChanged = CreateStatusCompletionSource();
            }

            return publisher.Status;
        }

        private static TaskCompletionSource<WiFiDirectAdvertisementPublisherStatus> CreateStatusCompletionSource()
        {
            return new TaskCompletionSource<WiFiDirectAdvertisementPublisherStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void SaveConfiguration(string displayName, string shortSessionId, bool autonomousGroupOwner)
        {
            _displayName = displayName;
            _shortSessionId = shortSessionId;
            _autonomousGroupOwner = autonomousGroupOwner;
        }

        private void TryAddAppInformationElement(
            WiFiDirectAdvertisement advertisement,
            string displayName,
            string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId))
            {
                LogReceived?.Invoke("Wi-Fi Direct InformationElement追加を省略: ShortSessionIdなし");
                return;
            }

            try
            {
                string payload = string.IsNullOrWhiteSpace(displayName)
                    ? $"DCHAT|{shortSessionId}"
                    : $"DCHAT|{displayName}|{shortSessionId}";
                var informationElement = new WiFiDirectInformationElement
                {
                    Oui = CreateBuffer(new byte[] { 0x44, 0x43, 0x48 }),
                    OuiType = 1,
                    Value = CreateBuffer(Encoding.UTF8.GetBytes(payload))
                };

                advertisement.InformationElements.Add(informationElement);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct InformationElement追加失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IBuffer CreateBuffer(byte[] bytes)
        {
            var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }

        private void OnStatusChanged(
            WiFiDirectAdvertisementPublisher sender,
            WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
        {
            LogReceived?.Invoke($"Advertisement状態変更: Status={args.Status}, Error={args.Error}");
            _statusChanged?.TrySetResult(args.Status);

            if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            {
                CleanupPublisher(sender);
            }
        }

        private void CleanupPublisher(WiFiDirectAdvertisementPublisher? publisher)
        {
            if (publisher == null)
            {
                return;
            }

            publisher.StatusChanged -= OnStatusChanged;
            if (ReferenceEquals(_publisher, publisher))
            {
                _publisher = null;
                _statusChanged = null;
                _displayName = "";
                _shortSessionId = "";
                _autonomousGroupOwner = false;
            }
        }
    }
}
