using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public class BleAdvertiser
    {
        private const ushort ManufacturerId = 0x1234;
        private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan RequestAdvertisementDuration = TimeSpan.FromSeconds(10);
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private BluetoothLEAdvertisementPublisher? _publisher;
        private TaskCompletionSource<BluetoothLEAdvertisementPublisherStatus>? _statusChanged;
        private CancellationTokenSource? _requestRestoreCts;
        private string _normalPayload = "";
        private string _sourceShortSessionId = "";
        private string _sourceRoleKey = "";

        public event Action<string>? LogReceived;

        public async Task StartAsync(string displayName, Guid sessionId, int tcpPort)
        {
            string shortName = ShortenUtf8(displayName, 7);
            _sourceShortSessionId = sessionId.ToString("N")[..4];
            _sourceRoleKey = sessionId.ToString("N")[..8];
            // RoleKeyの先頭4文字がShortSessionIdなので重複送信せず、端末名を7 bytesまで載せる。
            _normalPayload = $"D2|{shortName}|{_sourceRoleKey}|{tcpPort}";
            CancelRequestRestore();

            await _lifecycleGate.WaitAsync();
            try
            {
                await ReplacePayloadCoreAsync(_normalPayload);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task PublishConnectionRequestAsync(string targetShortSessionId)
        {
            if (string.IsNullOrWhiteSpace(_normalPayload) ||
                string.IsNullOrWhiteSpace(_sourceShortSessionId) ||
                string.IsNullOrWhiteSpace(targetShortSessionId))
            {
                throw new InvalidOperationException("BLE接続依頼を送る前に通常広告を開始してください");
            }

            CancelRequestRestore();
            var restoreCts = new CancellationTokenSource();
            _requestRestoreCts = restoreCts;
            string requestId = Guid.NewGuid().ToString("N")[..4];
            string requestPayload = $"DR|{_sourceShortSessionId}|{targetShortSessionId}|{_sourceRoleKey}|{requestId}";

            await _lifecycleGate.WaitAsync();
            try
            {
                await ReplacePayloadCoreAsync(requestPayload);
                LogReceived?.Invoke($"BLE接続依頼送信: TargetShortSessionId={targetShortSessionId}");
            }
            finally
            {
                _lifecycleGate.Release();
            }

            _ = RestoreNormalAdvertisementAsync(restoreCts.Token);
        }

        public void Stop()
        {
            CancelRequestRestore();
            BluetoothLEAdvertisementPublisher? publisher = _publisher;
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
                LogReceived?.Invoke($"BLE広告停止失敗: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CleanupPublisher(publisher);
            }
        }

        private async Task RestoreNormalAdvertisementAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(RequestAdvertisementDuration, cancellationToken);
                await _lifecycleGate.WaitAsync(cancellationToken);
                try
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await ReplacePayloadCoreAsync(_normalPayload);
                        LogReceived?.Invoke("BLE接続依頼広告を通常広告へ戻しました");
                    }
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"BLE通常広告の復元失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task ReplacePayloadCoreAsync(string payloadText)
        {
            await StopCoreAsync();

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadText);
            if (payloadBytes.Length > 27)
            {
                throw new InvalidOperationException($"BLE payloadが長すぎます: {payloadBytes.Length} bytes");
            }

            var advertisement = new BluetoothLEAdvertisement();
            var writer = new DataWriter();
            writer.WriteBytes(payloadBytes);
            advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData
            {
                CompanyId = ManufacturerId,
                Data = writer.DetachBuffer()
            });

            var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
            _publisher = publisher;
            publisher.StatusChanged += OnStatusChanged;
            _statusChanged = CreateStatusCompletionSource();
            LogReceived?.Invoke($"BLE広告開始要求: {payloadText}");
            publisher.Start();

            BluetoothLEAdvertisementPublisherStatus status = await WaitForStatusAsync(
                publisher,
                BluetoothLEAdvertisementPublisherStatus.Started,
                StateTimeout);
            if (status != BluetoothLEAdvertisementPublisherStatus.Started)
            {
                throw new InvalidOperationException($"BLE広告を開始できませんでした。Status={status}");
            }

            LogReceived?.Invoke($"BLE広告開始完了: {payloadText}");
        }

        private async Task StopCoreAsync()
        {
            BluetoothLEAdvertisementPublisher? publisher = _publisher;
            if (publisher == null)
            {
                return;
            }

            try
            {
                if (publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started ||
                    publisher.Status == BluetoothLEAdvertisementPublisherStatus.Waiting ||
                    publisher.Status == BluetoothLEAdvertisementPublisherStatus.Created)
                {
                    _statusChanged = CreateStatusCompletionSource();
                    publisher.Stop();
                    await WaitForStatusAsync(
                        publisher,
                        BluetoothLEAdvertisementPublisherStatus.Stopped,
                        StateTimeout);
                }
            }
            finally
            {
                CleanupPublisher(publisher);
            }
        }

        private async Task<BluetoothLEAdvertisementPublisherStatus> WaitForStatusAsync(
            BluetoothLEAdvertisementPublisher publisher,
            BluetoothLEAdvertisementPublisherStatus expectedStatus,
            TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (publisher.Status != expectedStatus)
            {
                if (publisher.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
                {
                    return publisher.Status;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"BLE広告状態待機タイムアウト: Expected={expectedStatus}, Actual={publisher.Status}");
                }

                TaskCompletionSource<BluetoothLEAdvertisementPublisherStatus> completion =
                    _statusChanged ??= CreateStatusCompletionSource();
                Task completed = await Task.WhenAny(completion.Task, Task.Delay(remaining));
                if (completed != completion.Task)
                {
                    throw new TimeoutException($"BLE広告状態待機タイムアウト: Expected={expectedStatus}, Actual={publisher.Status}");
                }

                _statusChanged = CreateStatusCompletionSource();
            }

            return publisher.Status;
        }

        private void OnStatusChanged(
            BluetoothLEAdvertisementPublisher sender,
            BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
        {
            LogReceived?.Invoke($"BLE広告状態: {args.Status}, Error: {args.Error}");
            _statusChanged?.TrySetResult(args.Status);
        }

        private void CleanupPublisher(BluetoothLEAdvertisementPublisher publisher)
        {
            publisher.StatusChanged -= OnStatusChanged;
            if (ReferenceEquals(_publisher, publisher))
            {
                _publisher = null;
                _statusChanged = null;
            }
        }

        private void CancelRequestRestore()
        {
            CancellationTokenSource? restore = _requestRestoreCts;
            _requestRestoreCts = null;
            if (restore == null)
            {
                return;
            }

            restore.Cancel();
            restore.Dispose();
        }

        private static TaskCompletionSource<BluetoothLEAdvertisementPublisherStatus> CreateStatusCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static string ShortenUtf8(string text, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "PC";
            }

            var result = new StringBuilder();
            foreach (Rune rune in text.EnumerateRunes())
            {
                string value = rune.ToString();
                if (Encoding.UTF8.GetByteCount(result.ToString()) + Encoding.UTF8.GetByteCount(value) > maxBytes)
                {
                    break;
                }

                result.Append(value);
            }

            return result.Length == 0 ? "PC" : result.ToString();
        }
    }
}
