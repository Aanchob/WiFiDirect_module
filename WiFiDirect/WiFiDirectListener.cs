using System;
using Windows.Devices.WiFiDirect;
using Windows.Networking.Proximity;
using direct_module.WiFiDirect.Models;

namespace direct_module.WiFiDirect;

public class WiFiDirectListener
{
    private readonly WiFiDirectConnectionListener _listener;
    public event Action<string>? LogReceived;
    public event Action<PeerInfo>? ConnectionRequested;

    public WiFiDirectListener()
    {
        _listener = new WiFiDirectConnectionListener();
    }

    private bool _isStarted;

    public void Start()
    {
        if (_isStarted)
        {
            LogReceived?.Invoke("Wi-Fi Direct Listener はすでに起動中です");
            return;
        }

        _listener.ConnectionRequested += OnConnectionRequested;
        _isStarted = true;

        LogReceived?.Invoke("Wi-Fi Direct Listener 起動");
        LogReceived?.Invoke("Wi-Fi Direct 接続要求待ち受け中");
    }

    private void OnConnectionRequested(
    WiFiDirectConnectionListener sender,
    WiFiDirectConnectionRequestedEventArgs args)
    {
        using WiFiDirectConnectionRequest request = args.GetConnectionRequest();

        var deviceInfo = request.DeviceInformation;

        PeerInfo peer = new PeerInfo
        {
            DisplayName = string.IsNullOrWhiteSpace(deviceInfo.Name)
            ? "Unknown Wi-Fi Direct device"
            : deviceInfo.Name,

            DeviceId = deviceInfo.Id,

            IsConnected = false
        };

        LogReceived?.Invoke($"接続要求を受信: {peer.DisplayName}");

        ConnectionRequested?.Invoke(peer);
    }
}