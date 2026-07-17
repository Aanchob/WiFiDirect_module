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
    public event Action<PeerInfo, WiFiDirectConnectionRequest>? IncomingConnectionRequested;

    public bool IsStarted => _isStarted;

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

    public void Stop()
    {
        if (!_isStarted)
        {
            return;
        }

        _listener.ConnectionRequested -= OnConnectionRequested;
        _isStarted = false;
        LogReceived?.Invoke("Wi-Fi Direct Listener停止");
    }

    private void OnConnectionRequested(
        WiFiDirectConnectionListener sender,
        WiFiDirectConnectionRequestedEventArgs args)
    {
        WiFiDirectConnectionRequest? request = null;

        try
        {
            request = args.GetConnectionRequest();
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
                WiFiDirectName = string.IsNullOrWhiteSpace(deviceInfo.Name)
                    ? "Unknown Wi-Fi Direct device"
                    : deviceInfo.Name,
                DeviceId = deviceInfo.Id,
                DeviceKind = deviceInfo.Kind.ToString(),
                IsEnabled = deviceInfo.IsEnabled,
                DiscoveredByBle = false,
                DiscoveredByWiFiDirect = true,
                IsIncomingConnectionRequest = true,
                IsConnected = false
            };

            ConnectionRequested?.Invoke(peer);

            if (IncomingConnectionRequested == null)
            {
                LogReceived?.Invoke("IncomingConnectionRequested購読なし: 接続要求を破棄します");
                request.Dispose();
                return;
            }

            // このrequestはaccept処理が終わるまで破棄しない。
            // _PendingRequestのFromIdAsyncは、元のWiFiDirectConnectionRequestを保持した状態で行う。
            IncomingConnectionRequested.Invoke(peer, request);
            request = null;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"ConnectionRequested処理失敗: {ex.GetType().Name}");
            LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
            LogReceived?.Invoke($"Message: {ex.Message}");
            request?.Dispose();
        }
    }

    private static string FormatName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "(empty)" : name;
    }
}
