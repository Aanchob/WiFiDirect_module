using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace direct_module.WiFiDirect.Models;

public sealed class PeerInfo : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _peerId = "";
    private string _deviceId = "";
    private bool _discoveredByBle;
    private bool _discoveredByWiFiDirect;
    private string _bleName = "";
    private string _wiFiDirectName = "";
    private string _matchKey = "";
    private string _shortSessionId = "";
    private string _roleKey = "";
    private int _tcpPort;
    private bool _isConnected;
    private bool _isConnectingWiFiDirect;
    private bool _isPreparingChatTcp;
    private bool _isTcpConnected;
    private bool _isHelloVerified;
    private bool _isChatReady;
    private string _statusText = "";
    private string _ipAddress = "";
    private string _remoteIpAddress = "";
    private bool? _isEnabled;
    private string _deviceKind = "";
    private bool _isGroupChat;
    private bool _canConnect;
    private DateTimeOffset _lastSeenAtUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string PeerId { get => _peerId; set => SetProperty(ref _peerId, value); }
    public string DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }
    public bool DiscoveredByBle { get => _discoveredByBle; set => SetProperty(ref _discoveredByBle, value); }
    public bool DiscoveredByWiFiDirect { get => _discoveredByWiFiDirect; set => SetProperty(ref _discoveredByWiFiDirect, value); }
    public string BleName { get => _bleName; set => SetProperty(ref _bleName, value); }
    public string WiFiDirectName { get => _wiFiDirectName; set => SetProperty(ref _wiFiDirectName, value); }
    public string MatchKey { get => _matchKey; set => SetProperty(ref _matchKey, value); }
    public string ShortSessionId { get => _shortSessionId; set => SetProperty(ref _shortSessionId, value); }
    public string RoleKey { get => _roleKey; set => SetProperty(ref _roleKey, value); }
    public int TcpPort { get => _tcpPort; set => SetProperty(ref _tcpPort, value); }
    public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }
    public bool IsConnectingWiFiDirect { get => _isConnectingWiFiDirect; set => SetProperty(ref _isConnectingWiFiDirect, value); }
    public bool IsPreparingChatTcp { get => _isPreparingChatTcp; set => SetProperty(ref _isPreparingChatTcp, value); }
    public bool IsTcpConnected { get => _isTcpConnected; set => SetProperty(ref _isTcpConnected, value); }
    public bool IsHelloVerified { get => _isHelloVerified; set => SetProperty(ref _isHelloVerified, value); }
    public bool IsChatReady { get => _isChatReady; set => SetProperty(ref _isChatReady, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string IpAddress { get => _ipAddress; set => SetProperty(ref _ipAddress, value); }
    public string RemoteIpAddress { get => _remoteIpAddress; set => SetProperty(ref _remoteIpAddress, value); }
    public bool? IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string DeviceKind { get => _deviceKind; set => SetProperty(ref _deviceKind, value); }
    public bool IsGroupChat { get => _isGroupChat; set => SetProperty(ref _isGroupChat, value); }
    public bool CanConnect { get => _canConnect; set => SetProperty(ref _canConnect, value); }
    public DateTimeOffset LastSeenAtUtc { get => _lastSeenAtUtc; set => SetProperty(ref _lastSeenAtUtc, value); }

    public double ConnectButtonOpacity => CanConnect && !IsGroupChat ? 1.0 : 0.25;

    public string SourceText
    {
        get
        {
            if (IsGroupChat) return "Group";
            if (DiscoveredByBle && DiscoveredByWiFiDirect) return "BLE + Wi-Fi Direct";
            if (DiscoveredByBle) return "BLE";
            return DiscoveredByWiFiDirect ? "Wi-Fi Direct" : "TCP";
        }
    }

    public string DisplayText
    {
        get
        {
            if (IsGroupChat) return "接続中の相手全員に送信します";

            string bleText = DiscoveredByBle ? "BLE:発見済み" : "BLE:未発見";
            string wifiText = IsConnectingWiFiDirect
                ? "Wi-Fi Direct:接続中"
                : !string.IsNullOrWhiteSpace(DeviceId)
                    ? IsConnected ? "Wi-Fi Direct:接続済み" : "Wi-Fi Direct:DeviceIdあり"
                    : "Wi-Fi Direct:DeviceIdなし";
            string tcpText = StatusText == "エラー"
                ? "TCP:エラー"
                : IsTcpConnected ? "TCP:接続済み" : IsConnected ? "TCP:準備中" : "TCP:未接続";
            string helloText = IsHelloVerified ? "HELLO:確認済み" : "HELLO:未確認";
            string statusText = !string.IsNullOrWhiteSpace(StatusText)
                ? $"状態:{StatusText}"
                : IsChatReady ? "状態:チャット準備完了" : IsConnected ? "状態:チャット準備中" : "状態:接続前";
            string remoteIpText = Value("RemoteIp", RemoteIpAddress);
            string sessionText = Value("ShortSessionId", ShortSessionId);
            string roleKeyText = Value("RoleKey", RoleKey);
            string bleNameText = Value("BLE名", BleName);
            string wifiNameText = Value("Wi-Fi名", WiFiDirectName);
            string kindText = Value("Kind", DeviceKind);

            return $"{DisplayName} / {SourceText} / {bleText} / {wifiText} / {tcpText} / {helloText} / {statusText}{remoteIpText}{sessionText}{roleKeyText}{bleNameText}{wifiNameText}{kindText}";
        }
    }

    private static string Value(string label, string value)
        => string.IsNullOrWhiteSpace(value) ? "" : $" / {label}:{value}";

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectButtonOpacity)));
    }
}
