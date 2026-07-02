using System;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public class BleAdvertiser
    {
        private BluetoothLEAdvertisementPublisher? _publisher;

        public event Action<string>? LogReceived;

        // 自分たちのアプリ用の識別子
        private const ushort ManufacturerId = 0x1234;

        public void Start(string displayName, Guid sessionId, int tcpPort)
        {
            if (_publisher != null)
            {
                LogReceived?.Invoke("BLE広告はすでに開始されています");
                return;
            }

            var advertisement = new BluetoothLEAdvertisement();

            string shortName = Shorten(displayName, 4);
            string shortSessionId = sessionId.ToString("N")[..4];

            string payloadText = $"DC|{shortName}|{shortSessionId}|{tcpPort}";
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadText);

            LogReceived?.Invoke($"BLE payload bytes: {payloadBytes.Length}");

            var writer = new DataWriter();
            writer.WriteBytes(payloadBytes);

            var manufacturerData = new BluetoothLEManufacturerData
            {
                CompanyId = ManufacturerId,
                Data = writer.DetachBuffer()
            };

            advertisement.ManufacturerData.Add(manufacturerData);

            _publisher = new BluetoothLEAdvertisementPublisher(advertisement);
            _publisher.StatusChanged += OnStatusChanged;

            LogReceived?.Invoke($"BLE広告開始要求: {payloadText}");

            _publisher.Start();
        }

        public void Stop()
        {
            if (_publisher == null)
            {
                LogReceived?.Invoke("BLE広告は開始されていません");
                return;
            }

            _publisher.Stop();
            _publisher.StatusChanged -= OnStatusChanged;
            _publisher = null;

            LogReceived?.Invoke("BLE広告停止");
        }

        private void OnStatusChanged(
            BluetoothLEAdvertisementPublisher sender,
            BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
        {
            LogReceived?.Invoke($"BLE広告状態: {args.Status}, Error: {args.Error}");
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "PC";
            }

            return text.Length <= maxLength
                ? text
                : text[..maxLength];
        }
    }
}