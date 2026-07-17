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
        private readonly HashSet<string> _foundRequestKeys = new();
        private readonly object _foundPeerKeysLock = new();

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;
        public event Action<BleConnectionRequest>? ConnectionRequestReceived;

        private const ushort ManufacturerId = 0x1234;

        public void Start()
        {
            lock (_foundPeerKeysLock)
            {
                _foundPeerKeys.Clear();
                _foundRequestKeys.Clear();
            }

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

                if (_watcher != null)
                {
                    _watcher.Received -= OnReceived;
                    _watcher.Stopped -= OnStopped;
                    _watcher = null;
                }
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

                if (payloadText.StartsWith("DR|", StringComparison.Ordinal))
                {
                    HandleConnectionRequest(payloadText);
                    continue;
                }

                bool isVersion2 = payloadText.StartsWith("D2|", StringComparison.Ordinal);
                if (!isVersion2 && !payloadText.StartsWith("DC|", StringComparison.Ordinal))
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
                string roleKey = isVersion2 ? parts[2] : parts.Length >= 5 ? parts[4] : "";
                if (isVersion2 && roleKey.Length < 4)
                {
                    LogReceived?.Invoke($"BLE D2広告のRoleKeyが不正です: {payloadText}");
                    continue;
                }

                string shortSessionId = isVersion2 && roleKey.Length >= 4
                    ? roleKey[..4]
                    : parts[2];
                string portText = parts[3];

                if (!int.TryParse(portText, out int tcpPort))
                {
                    LogReceived?.Invoke($"TcpPortの解析に失敗: {portText}");
                    continue;
                }

                //string ipAddress = parts[4];

                string peerKey = $"{displayName}|{shortSessionId}|{tcpPort}";

                lock (_foundPeerKeysLock)
                {
                    if (!_foundPeerKeys.Add(peerKey))
                    {
                        continue;
                    }

                }

                PeerInfo peer = new PeerInfo
                {
                    DisplayName = displayName,
                    BleName = displayName,
                    DeviceId = "",
                    DiscoveredByBle = true,
                    ShortSessionId = shortSessionId,
                    RoleKey = roleKey,
                    MatchKey = shortSessionId,
                    TcpPort = tcpPort,
                    IpAddress = "",
                    IsConnected = false
                };

                LogReceived?.Invoke(
                    $"BLE Peer発見: Name={peer.DisplayName}, ShortSessionId={peer.ShortSessionId}, Port={peer.TcpPort}, RoleKey={peer.RoleKey}"
                );

                PeerFound?.Invoke(peer);
            }
        }

        private void HandleConnectionRequest(string payloadText)
        {
            string[] parts = payloadText.Split('|');
            if (parts.Length < 4 ||
                string.IsNullOrWhiteSpace(parts[1]) ||
                string.IsNullOrWhiteSpace(parts[2]))
            {
                LogReceived?.Invoke($"BLE接続依頼形式が不正です: {payloadText}");
                return;
            }

            string requestId = parts.Length >= 5 ? parts[4] : "legacy";
            string requestKey = $"{parts[1]}|{parts[2]}|{parts[3]}|{requestId}";
            lock (_foundPeerKeysLock)
            {
                if (!_foundRequestKeys.Add(requestKey))
                {
                    return;
                }
            }

            var request = new BleConnectionRequest
            {
                SourceShortSessionId = parts[1],
                TargetShortSessionId = parts[2],
                SourceRoleKey = parts[3],
                RequestId = requestId
            };
            LogReceived?.Invoke(
                $"BLE接続依頼受信: Source={request.SourceShortSessionId}, Target={request.TargetShortSessionId}");
            ConnectionRequestReceived?.Invoke(request);
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
