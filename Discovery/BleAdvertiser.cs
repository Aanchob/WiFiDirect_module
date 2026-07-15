using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public class BleAdvertiser
    {
        private readonly object _gate = new();
        private BluetoothLEAdvertisementPublisher? _publisher;

        public event Action<string>? LogReceived;

        // 自分たちのアプリ用の識別子
        private const ushort ManufacturerId = 0x1234;

        public void Start(string displayName, Guid sessionId, int tcpPort)
        {
            byte[] payloadBytes;
            BluetoothLEAdvertisementPublisher? attemptedPublisher = null;
            try
            {
                payloadBytes = BleAdvertisementPayload.Create(displayName, sessionId, tcpPort);
            }
            catch (Exception ex)
            {
                SafeLog($"BLE広告payload作成失敗: {ex.Message}");
                return;
            }

            try
            {
                string logMessage;
                lock (_gate)
                {
                    if (_publisher != null &&
                        (_publisher.Status == BluetoothLEAdvertisementPublisherStatus.Aborted ||
                         _publisher.Status == BluetoothLEAdvertisementPublisherStatus.Stopped))
                    {
                        _publisher.StatusChanged -= OnStatusChanged;
                        _publisher = null;
                    }

                    if (_publisher != null)
                    {
                        logMessage = "BLE広告はすでに開始されています";
                    }
                    else
                    {
                        var advertisement = new BluetoothLEAdvertisement();
                        using var writer = new DataWriter();
                        writer.WriteBytes(payloadBytes);
                        advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData
                        {
                            CompanyId = ManufacturerId,
                            Data = writer.DetachBuffer()
                        });

                        var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
                        attemptedPublisher = publisher;
                        publisher.StatusChanged += OnStatusChanged;
                        _publisher = publisher;
                        publisher.Start();
                        logMessage = $"BLE広告開始要求: Version={BleAdvertisementPayload.CurrentVersion}, Bytes={payloadBytes.Length}";
                    }
                }
                SafeLog(logMessage);
            }
            catch (Exception ex)
            {
                if (attemptedPublisher != null)
                {
                    ReleasePublisher(attemptedPublisher);
                }
                SafeLog($"BLE広告開始失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Stop()
        {
            BluetoothLEAdvertisementPublisher? publisher;
            lock (_gate)
            {
                publisher = _publisher;
                if (publisher != null)
                {
                    publisher.StatusChanged -= OnStatusChanged;
                    _publisher = null;
                }
            }

            if (publisher == null)
            {
                SafeLog("BLE広告は開始されていません");
                return;
            }

            try
            {
                publisher.Stop();
                SafeLog("BLE広告停止");
            }
            catch (Exception ex)
            {
                SafeLog($"BLE広告停止失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnStatusChanged(
            BluetoothLEAdvertisementPublisher sender,
            BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
        {
            if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
            {
                ReleasePublisher(sender);
            }
            QueueLog(
                $"BLE広告状態: {args.Status}, Error: {args.Error}" +
                (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted
                    ? " (再開始可能な状態に戻しました)"
                    : ""));
        }

        private void ReleasePublisher(BluetoothLEAdvertisementPublisher publisher)
        {
            lock (_gate)
            {
                publisher.StatusChanged -= OnStatusChanged;
                if (ReferenceEquals(_publisher, publisher))
                {
                    _publisher = null;
                }
            }
        }

        private void QueueLog(string message)
        {
            ThreadPool.QueueUserWorkItem(_ => SafeLog(message));
        }

        private void SafeLog(string message)
        {
            Action<string>? handlers = LogReceived;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
            {
                try
                {
                    handler(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"BleAdvertiser log handler failed: {ex}");
                }
            }
        }
    }
}
