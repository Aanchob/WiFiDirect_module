using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using direct_module.WiFiDirect.Models;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public class BleScanner
    {
        private BluetoothLEAdvertisementWatcher? _watcher;

        private readonly HashSet<string> _foundPeerKeys = new();

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;

        private const ushort ManufacturerId = 0x1234;

        public void Start()
        {
            if (_watcher != null)
            {
                LogReceived?.Invoke($"BLEスキャンはすでに作成されています: Status={_watcher.Status}");
                return;
            }

            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };

                _watcher.Received += OnReceived;
                _watcher.Stopped += OnStopped;

                LogReceived?.Invoke($"BLEスキャン開始要求: Status={_watcher.Status}");

                _watcher.Start();

                LogReceived?.Invoke($"BLEスキャン開始後: Status={_watcher.Status}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"BLEスキャン開始失敗: {ex.GetType().Name}");
                LogReceived?.Invoke(ex.Message);

                if (_watcher != null)
                {
                    _watcher.Received -= OnReceived;
                    _watcher.Stopped -= OnStopped;
                    _watcher = null;
                }
            }
        }

        public void Stop()
        {
            if (_watcher == null)
            {
                LogReceived?.Invoke("BLEスキャンは開始されていません");
                return;
            }

            try
            {
                LogReceived?.Invoke($"BLEスキャン停止要求: Status={_watcher.Status}");

                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started ||
                    _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created)
                {
                    _watcher.Stop();
                }
                else
                {
                    LogReceived?.Invoke($"BLEスキャンは停止不要です: Status={_watcher.Status}");

                    _watcher.Received -= OnReceived;
                    _watcher.Stopped -= OnStopped;
                    _watcher = null;
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"BLEスキャン停止失敗: {ex.GetType().Name}");
                LogReceived?.Invoke(ex.Message);

                _watcher.Received -= OnReceived;
                _watcher.Stopped -= OnStopped;
                _watcher = null;
            }
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

                if (parts.Length < 5)
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

                string ipAddress = parts[4];

                string peerKey = $"{displayName}|{shortSessionId}|{tcpPort}|{ipAddress}";

                if (_foundPeerKeys.Contains(peerKey))
                {
                    continue;
                }

                _foundPeerKeys.Add(peerKey);

                PeerInfo peer = new PeerInfo
                {
                    DisplayName = displayName,
                    DeviceId = "",
                    DiscoveredByBle = true,
                    ShortSessionId = shortSessionId,
                    TcpPort = tcpPort,
                    IpAddress = ipAddress,
                    IsConnected = false
                };

                LogReceived?.Invoke(
                    $"BLE発見: {peer.DisplayName}, ID={peer.ShortSessionId}, Port={peer.TcpPort}, IP={peer.IpAddress}"
                );

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