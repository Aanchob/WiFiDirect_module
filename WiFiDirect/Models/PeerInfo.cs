namespace direct_module.WiFiDirect.Models;

public class PeerInfo
{
    public string DisplayName { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public bool DiscoveredByBle { get; set; }

    public bool DiscoveredByWiFiDirect { get; set; }

    public string ShortSessionId { get; set; } = "";

    public int TcpPort { get; set; }

    public bool IsConnected { get; set; }

    public bool IsTcpConnected { get; set; }

    public string IpAddress { get; set; } = "";

    public string RemoteIpAddress { get; set; } = "";

    public bool? IsEnabled { get; set; }

    public string DeviceKind { get; set; } = "";

    public string SourceText
    {
        get
        {
            if (DiscoveredByBle && DiscoveredByWiFiDirect)
            {
                return "BLE + Wi-Fi Direct";
            }

            if (DiscoveredByBle)
            {
                return "BLE";
            }

            return "Wi-Fi Direct";
        }
    }

    public string DisplayText
    {
        get
        {
            string bleText = DiscoveredByBle ? "BLE:発見済み" : "BLE:未発見";
            string wifiText = !string.IsNullOrWhiteSpace(DeviceId)
                ? IsConnected ? "Wi-Fi Direct:接続済み" : "Wi-Fi Direct:DeviceIdあり"
                : "Wi-Fi Direct:DeviceIdなし";
            string tcpText = IsTcpConnected ? "TCP:接続済み" : "TCP:未接続";
            string statusText = IsTcpConnected
                ? "状態:チャット可能"
                : IsConnected ? "状態:TCP準備中" : "状態:接続前";
            string remoteIpText = !string.IsNullOrWhiteSpace(RemoteIpAddress)
                ? $" / RemoteIp:{RemoteIpAddress}"
                : "";
            string ipText = string.IsNullOrWhiteSpace(RemoteIpAddress) && !string.IsNullOrWhiteSpace(IpAddress)
                ? $" / IP:{IpAddress}"
                : "";
            string portText = TcpPort > 0 ? $" / Port:{TcpPort}" : "";
            string sessionText = !string.IsNullOrWhiteSpace(ShortSessionId)
                ? $" / ID:{ShortSessionId}"
                : "";
            string enabledText = IsEnabled.HasValue ? $" / IsEnabled:{IsEnabled.Value}" : "";
            string kindText = !string.IsNullOrWhiteSpace(DeviceKind) ? $" / Kind:{DeviceKind}" : "";

            return $"{DisplayName} / {SourceText} / {bleText} / {wifiText} / {tcpText} / {statusText}{remoteIpText}{ipText}{portText}{sessionText}{enabledText}{kindText}";
        }
    }
}
