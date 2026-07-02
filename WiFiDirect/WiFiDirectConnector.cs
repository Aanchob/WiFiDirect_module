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
            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("接続失敗: DeviceId が空です");
                return;
            }

            LogReceived?.Invoke($"接続開始: {peer.DisplayName}");

            WiFiDirectDevice? device = await WiFiDirectDevice.FromIdAsync(peer.DeviceId);

            if (device == null)
            {
                LogReceived?.Invoke("接続失敗: WiFiDirectDevice を作成できませんでした");
                return;
            }

            var endpoints = device.GetConnectionEndpointPairs();

            LogReceived?.Invoke($"接続成功: {peer.DisplayName}");
            LogReceived?.Invoke($"Endpoint数: {endpoints.Count}");

            foreach (var endpoint in endpoints)
            {
                LogReceived?.Invoke($"LocalHostName: {endpoint.LocalHostName}");
                LogReceived?.Invoke($"LocalServiceName: {endpoint.LocalServiceName}");
                LogReceived?.Invoke($"RemoteHostName: {endpoint.RemoteHostName}");
                LogReceived?.Invoke($"RemoteServiceName: {endpoint.RemoteServiceName}");
            }

            var session = new WiFiDirectSession(peer, device);

            Connected?.Invoke(session);
        }
    }
}