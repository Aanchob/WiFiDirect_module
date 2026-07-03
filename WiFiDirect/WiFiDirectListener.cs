using System;
using Windows.Devices.WiFiDirect;
using direct_module.WiFiDirect.Models;

namespace direct_module.WiFiDirect;

public class WiFiDirectListener
{
    private readonly WiFiDirectConnectionListener _listener;
    private bool _isStarted;

    public event Action<string>? LogReceived;
    public event Action<PeerInfo>? ConnectionRequested;

    public WiFiDirectListener()
    {
        _listener = new WiFiDirectConnectionListener();
    }

    public void Start()
    {
        if (_isStarted)
        {
            LogReceived?.Invoke("Wi-Fi Direct Listener はすでに起動中です");
            LogReceived?.Invoke("二重起動防止: Start要求を無視しました");
            return;
        }

        try
        {
            LogReceived?.Invoke("Wi-Fi Direct Listener 起動");
            LogReceived?.Invoke("Listener作成成功");

            _listener.ConnectionRequested += OnConnectionRequested;
            LogReceived?.Invoke("ConnectionRequestedイベント登録済み");

            _isStarted = true;
            LogReceived?.Invoke("Wi-Fi Direct接続要求待ち受け中");
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Wi-Fi Direct Listener起動失敗: {ex.GetType().Name}");
            LogReceived?.Invoke($"Message: {ex.Message}");
        }
    }

    private void OnConnectionRequested(
        WiFiDirectConnectionListener sender,
        WiFiDirectConnectionRequestedEventArgs args)
    {
        try
        {
            using WiFiDirectConnectionRequest request = args.GetConnectionRequest();
            var deviceInfo = request.DeviceInformation;

            LogReceived?.Invoke("接続要求を受信");
            LogReceived?.Invoke($"Request Name: {FormatName(deviceInfo.Name)}");
            LogReceived?.Invoke($"Request Id: {deviceInfo.Id}");
            LogReceived?.Invoke($"Request Kind: {deviceInfo.Kind}");
            LogReceived?.Invoke($"Request IsEnabled: {deviceInfo.IsEnabled}");

            PeerInfo peer = new PeerInfo
            {
                DisplayName = string.IsNullOrWhiteSpace(deviceInfo.Name)
                    ? "Unknown Wi-Fi Direct device"
                    : deviceInfo.Name,
                DeviceId = deviceInfo.Id,
                DeviceKind = deviceInfo.Kind.ToString(),
                IsEnabled = deviceInfo.IsEnabled,
                DiscoveredByBle = false,
                IsConnected = false
            };

            ConnectionRequested?.Invoke(peer);
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"ConnectionRequested処理失敗: {ex.GetType().Name}");
            LogReceived?.Invoke($"Message: {ex.Message}");
        }
    }

    private static string FormatName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "(empty)" : name;
    }
}
