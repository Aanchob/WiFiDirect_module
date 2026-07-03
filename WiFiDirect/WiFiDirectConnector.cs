using System;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectConnector
    {
        private bool _isConnecting;
        private bool _isAcceptingIncomingConnection;

        public event Action<string>? LogReceived;
        public event Action<WiFiDirectSession>? Connected;

        public async Task ConnectAsync(PeerInfo peer)
        {
            LogReceived?.Invoke("Wi-Fi Direct接続開始");
            LogReceived?.Invoke($"Target Name: {peer.DisplayName}");
            LogReceived?.Invoke($"Target DeviceId: {peer.DeviceId}");
            LogReceived?.Invoke($"Target Kind: {peer.DeviceKind}");
            LogReceived?.Invoke($"Target IsEnabled: {peer.IsEnabled}");

            if (_isConnecting)
            {
                LogReceived?.Invoke("すでにWi-Fi Direct通常接続処理中です");
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct接続失敗: DeviceId が空です");
                return;
            }

            if (IsPendingRequestDeviceId(peer.DeviceId))
            {
                LogReceived?.Invoke("_PendingRequest付きDeviceIdのため通常接続を中止します");
                LogReceived?.Invoke("このDeviceIdは受信要求accept専用です");
                return;
            }

            _isConnecting = true;

            try
            {
                WiFiDirectDevice? device = await CreateDeviceFromIdAsync(peer.DeviceId, "FromIdAsync", "Target");

                if (device == null)
                {
                    LogReceived?.Invoke("Wi-Fi Direct接続失敗: WiFiDirectDevice を作成できませんでした");
                    return;
                }

                CompleteConnection(peer, device, "Wi-Fi Direct接続成功");
            }
            catch (Exception ex)
            {
                LogFailure("Wi-Fi Direct接続失敗", ex, "Target", peer);
            }
            finally
            {
                _isConnecting = false;
            }
        }

        public async Task AcceptIncomingConnectionAsync(
            PeerInfo peer,
            WiFiDirectConnectionRequest request)
        {
            LogReceived?.Invoke("Wi-Fi Direct接続要求Accept開始");
            LogReceived?.Invoke($"Request Name: {peer.DisplayName}");
            LogReceived?.Invoke($"Request DeviceId: {peer.DeviceId}");
            LogReceived?.Invoke($"Request Kind: {peer.DeviceKind}");
            LogReceived?.Invoke($"Request IsEnabled: {peer.IsEnabled}");

            if (_isAcceptingIncomingConnection)
            {
                LogReceived?.Invoke("すでにWi-Fi Direct接続要求Accept処理中です");
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("接続要求Accept失敗: DeviceId が空です");
                return;
            }

            if (!IsPendingRequestDeviceId(peer.DeviceId))
            {
                LogReceived?.Invoke("通常DeviceIdなのでAcceptIncomingConnectionAsyncでは処理しません");
                return;
            }

            _isAcceptingIncomingConnection = true;

            try
            {
                string requestDeviceId = request.DeviceInformation.Id;
                LogReceived?.Invoke($"保持中Request DeviceId: {requestDeviceId}");

                WiFiDirectDevice? device = await CreateDeviceFromIdAsync(
                    requestDeviceId,
                    "接続要求Accept FromIdAsync",
                    "Request");

                if (device == null)
                {
                    LogReceived?.Invoke("接続要求Accept失敗: FromIdAsync が null を返しました");
                    return;
                }

                CompleteConnection(peer, device, "接続要求Accept成功");
            }
            catch (Exception ex)
            {
                LogFailure("Wi-Fi Direct接続要求Accept失敗", ex, "Request", peer);
            }
            finally
            {
                _isAcceptingIncomingConnection = false;
            }
        }

        private async Task<WiFiDirectDevice?> CreateDeviceFromIdAsync(
            string deviceId,
            string operationName,
            string logPrefix)
        {
            bool completed = false;

            LogReceived?.Invoke($"{operationName}開始");
            _ = Task.Delay(10000).ContinueWith(_ =>
            {
                if (!completed)
                {
                    LogReceived?.Invoke($"10秒経過: {operationName}がまだ完了していません");
                    LogReceived?.Invoke($"{logPrefix} DeviceId: {deviceId}");
                }
            });

            try
            {
                WiFiDirectDevice? device = await WiFiDirectDevice.FromIdAsync(deviceId);
                completed = true;

                LogReceived?.Invoke(device == null
                    ? $"{operationName}結果: null"
                    : $"{operationName}成功");

                return device;
            }
            finally
            {
                completed = true;
            }
        }

        private void CompleteConnection(PeerInfo peer, WiFiDirectDevice device, string successMessage)
        {
            var endpoints = device.GetConnectionEndpointPairs();

            LogReceived?.Invoke(successMessage);
            LogReceived?.Invoke($"Endpoint数: {endpoints.Count}");

            foreach (var endpoint in endpoints)
            {
                LogReceived?.Invoke("---- Wi-Fi Direct Endpoint ----");
                LogReceived?.Invoke($"LocalHostName: {endpoint.LocalHostName.DisplayName}");
                LogReceived?.Invoke($"LocalServiceName: {endpoint.LocalServiceName}");
                LogReceived?.Invoke($"RemoteHostName: {endpoint.RemoteHostName.DisplayName}");
                LogReceived?.Invoke($"RemoteServiceName: {endpoint.RemoteServiceName}");

                if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    peer.RemoteIpAddress = endpoint.RemoteHostName.DisplayName;
                    LogReceived?.Invoke($"Wi-Fi Direct RemoteIpAddress保存: {peer.RemoteIpAddress}");
                }
            }

            peer.IsConnected = true;

            var session = new WiFiDirectSession(peer, device);
            Connected?.Invoke(session);
        }

        private void LogFailure(string title, Exception ex, string prefix, PeerInfo peer)
        {
            LogReceived?.Invoke(title);
            LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
            LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
            LogReceived?.Invoke($"Message: {ex.Message}");
            LogReceived?.Invoke($"{prefix} Name: {peer.DisplayName}");
            LogReceived?.Invoke($"{prefix} DeviceId: {peer.DeviceId}");
        }

        private static bool IsPendingRequestDeviceId(string deviceId)
        {
            return deviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);
        }
    }
}
