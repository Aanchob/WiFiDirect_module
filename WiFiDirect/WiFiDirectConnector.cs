using System;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectConnector
    {
        public event Action<string>? LogReceived;
        public event Action<WiFiDirectSession>? Connected;

        public async Task ConnectAsync(PeerInfo peer)
        {
            LogReceived?.Invoke("Wi-Fi Direct接続開始");
            LogReceived?.Invoke($"Target Name: {peer.DisplayName}");
            LogReceived?.Invoke($"Target DeviceId: {peer.DeviceId}");

            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("接続失敗: DeviceId が空です");
                return;
            }

            try
            {
                LogReceived?.Invoke("FromIdAsync開始");

                WiFiDirectDevice? device = await WiFiDirectDevice.FromIdAsync(peer.DeviceId);

                if (device == null)
                {
                    LogReceived?.Invoke("FromIdAsync結果: null");
                    LogReceived?.Invoke("接続失敗: WiFiDirectDevice を作成できませんでした");
                    return;
                }

                LogReceived?.Invoke("FromIdAsync成功");

                var endpoints = device.GetConnectionEndpointPairs();

                LogReceived?.Invoke($"Endpoint数: {endpoints.Count}");

                foreach (var endpoint in endpoints)
                {
                    LogReceived?.Invoke("---- Wi-Fi Direct Endpoint ----");
                    LogReceived?.Invoke($"LocalHostName: {endpoint.LocalHostName}");
                    LogReceived?.Invoke($"LocalServiceName: {endpoint.LocalServiceName}");
                    LogReceived?.Invoke($"RemoteHostName: {endpoint.RemoteHostName}");
                    LogReceived?.Invoke($"RemoteServiceName: {endpoint.RemoteServiceName}");
                }

                peer.IsConnected = true;

                var session = new WiFiDirectSession(peer, device);
                Connected?.Invoke(session);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Wi-Fi Direct接続失敗: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }
    }
}
