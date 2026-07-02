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

            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started ||
                _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created)
            {
                LogReceived?.Invoke("BLEスキャン停止要求");

                _watcher.Stop();
                return;
            }

            LogReceived?.Invoke($"BLEスキャンは停止できない状態です: {_watcher.Status}");
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

                if (!payloadText.StartsWith("DC|"))
                {
                    continue;
                }

                string[] parts = payloadText.Split('|');

                if (parts.Length < 4)
                {
                    LogReceived?.Invoke($"BLE広告形式が不正です: {payloadText}");
                    continue;
                }

                string displayName = parts[1];
                string shortSessionId = parts[2];

                if (!int.TryParse(parts[3], out int tcpPort))
                {
                    LogReceived?.Invoke($"TcpPortの解析に失敗: {parts[3]}");
                    continue;
                }

                PeerInfo peer = new PeerInfo
                {
                    DisplayName = displayName,
                    DeviceId = "",
                    DiscoveredByBle = true,
                    ShortSessionId = shortSessionId,
                    TcpPort = tcpPort,
                    IsConnected = false
                };

                LogReceived?.Invoke($"BLE発見: {peer.DisplayName}, ShortSession={peer.ShortSessionId}, Port={peer.TcpPort}");

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
            sender.Received -= OnReceived;
            sender.Stopped -= OnStopped;

            LogReceived?.Invoke($"BLEスキャン停止: {args.Error}");

            if (_watcher == sender)
            {
                _watcher = null;
            }
        }
    }
}