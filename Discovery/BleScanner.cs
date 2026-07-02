using System;
using System.Linq;
using System.Text;
using direct_module.WiFiDirect.Models;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public class BleScanner
    {
        private BluetoothLEAdvertisementWatcher? _watcher;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;

        private const ushort ManufacturerId = 0x1234;

        public void Start()
        {
            if (_watcher != null)
            {
                LogReceived?.Invoke("BLEスキャンはすでに開始されています");
                return;
            }

            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += OnReceived;
            _watcher.Stopped += OnStopped;

            LogReceived?.Invoke("BLEスキャン開始");

            _watcher.Start();
        }

        public void Stop()
        {
            if (_watcher == null)
            {
                LogReceived?.Invoke("BLEスキャンは開始されていません");
                return;
            }

            _watcher.Stop();
        }

        private void OnReceived(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            foreach (var data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId != ManufacturerId)
                {
                    continue;
                }

                string payloadText = ReadBufferAsString(data.Data);

                if (!payloadText.StartsWith("DCHAT|"))
                {
                    continue;
                }

                string displayName = payloadText.Substring("DCHAT|".Length);

                PeerInfo peer = new PeerInfo
                {
                    DisplayName = displayName,
                    DeviceId = "",
                    DiscoveredByBle = true,
                    IsConnected = false
                };

                LogReceived?.Invoke($"BLE発見: {peer.DisplayName}");

                PeerFound?.Invoke(peer);
            }
        }

        private static string ReadBufferAsString(IBuffer buffer)
        {
            byte[] bytes = new byte[buffer.Length];

            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);

            return Encoding.UTF8.GetString(bytes);
        }

        private void OnStopped(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            LogReceived?.Invoke($"BLEスキャン停止: {args.Error}");
            _watcher = null;
        }
    }
}