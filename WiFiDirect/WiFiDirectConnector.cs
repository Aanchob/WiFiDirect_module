using System;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectConnector
    {
        private bool _isConnecting;

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
                LogReceived?.Invoke("すでにWi-Fi Direct接続処理中です");
                return;
            }

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct接続失敗: DeviceId が空です");
                return;
            }

            if (peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                LogReceived?.Invoke("PendingRequest DeviceIdを受信しました");
                LogReceived?.Invoke("このDeviceIdでは自動FromIdAsyncしません");
                LogReceived?.Invoke("_PendingRequest付きDeviceIdのため通常接続を中止します");
                return;
            }

            _isConnecting = true;

            try
            {
                LogReceived?.Invoke("FromIdAsync開始");

                WiFiDirectDevice? device = await WiFiDirectDevice.FromIdAsync(peer.DeviceId);

                if (device == null)
                {
                    LogReceived?.Invoke("FromIdAsync結果: null");
                    LogReceived?.Invoke("Wi-Fi Direct接続失敗: WiFiDirectDevice を作成できませんでした");
                    return;
                }

                LogReceived?.Invoke("FromIdAsync成功");

                var endpoints = device.GetConnectionEndpointPairs();

                LogReceived?.Invoke("Wi-Fi Direct接続成功");
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
            catch (Exception ex)
            {
                LogReceived?.Invoke("Wi-Fi Direct接続失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
                LogReceived?.Invoke($"Message: {ex.Message}");
                LogReceived?.Invoke($"Target Name: {peer.DisplayName}");
                LogReceived?.Invoke($"Target DeviceId: {peer.DeviceId}");
            }
            finally
            {
                _isConnecting = false;
            }
        }
    }
}
