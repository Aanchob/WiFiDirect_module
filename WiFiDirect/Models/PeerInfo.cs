namespace direct_module.WiFiDirect.Models;

public enum PeerMatchState
{
    Unmatched,
    Provisional,
    Confirmed,
    Rejected
}

public class PeerInfo
{
    public string DisplayName { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public string PeerId { get; set; } = "";

    public bool DiscoveredByBle { get; set; }

    public bool DiscoveredByWiFiDirect { get; set; }

    public bool IsIncomingConnectionRequest { get; set; }

    public string BleName { get; set; } = "";

    public string WiFiDirectName { get; set; } = "";

    public string MatchKey { get; set; } = "";

    public string DchatInformation { get; set; } = "";

    public string ShortSessionId { get; set; } = "";

    public string RoleKey { get; set; } = "";

    public PeerMatchState MatchState { get; set; } = PeerMatchState.Unmatched;

    public string PendingWiFiDirectDeviceId { get; set; } = "";

    public string PendingWiFiDirectName { get; set; } = "";

    public string PendingWiFiDirectDeviceKind { get; set; } = "";

    public bool? PendingWiFiDirectIsEnabled { get; set; }

    public int MatchScore { get; set; }

    public string MatchReason { get; set; } = "";

    public string WiFiDirectDeviceIdForConnection =>
        !string.IsNullOrWhiteSpace(DeviceId) ? DeviceId : PendingWiFiDirectDeviceId;

    public int TcpPort { get; set; }

    public bool IsConnected { get; set; }

    public bool IsConnectingWiFiDirect { get; set; }

    public bool IsPreparingChatTcp { get; set; }

    public bool IsTcpConnected { get; set; }

    public bool IsHelloVerified { get; set; }

    public bool IsChatReady { get; set; }

    public string StatusText { get; set; } = "";

    public string IpAddress { get; set; } = "";

    public string RemoteIpAddress { get; set; } = "";

    public bool? IsEnabled { get; set; }

    public string DeviceKind { get; set; } = "";

    public bool IsGroupChat { get; set; }

    public bool CanConnect { get; set; }

    public double ConnectButtonOpacity => CanConnect && !IsGroupChat ? 1.0 : 0.25;

    public string SourceText
    {
        get
        {
            if (IsGroupChat)
            {
                return "Group";
            }

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
            if (IsGroupChat)
            {
                return "接続中の相手全員に送信します";
            }

            string bleText = DiscoveredByBle ? "BLE:発見済み" : "BLE:未発見";
            string wifiText = IsConnectingWiFiDirect
                ? "Wi-Fi Direct:接続中"
                : !string.IsNullOrWhiteSpace(WiFiDirectDeviceIdForConnection)
                ? IsConnected ? "Wi-Fi Direct:接続済み" : "Wi-Fi Direct:DeviceIdあり"
                : "Wi-Fi Direct:DeviceIdなし";
            string tcpText = StatusText == "エラー"
                ? "TCP:エラー"
                : IsTcpConnected ? "TCP:接続済み" : IsConnected ? "TCP:準備中" : "TCP:未接続";
            string helloText = IsHelloVerified ? "HELLO:確認済み" : "HELLO:未確認";
            string statusText = !string.IsNullOrWhiteSpace(StatusText)
                ? $"状態:{StatusText}"
                : IsChatReady
                    ? "状態:チャット準備完了"
                    : IsConnected ? "状態:チャット準備中" : "状態:接続前";
            string remoteIpText = !string.IsNullOrWhiteSpace(RemoteIpAddress)
                ? $" / RemoteIp:{RemoteIpAddress}"
                : "";
            string sessionText = !string.IsNullOrWhiteSpace(ShortSessionId)
                ? $" / ShortSessionId:{ShortSessionId}"
                : "";
            string roleKeyText = !string.IsNullOrWhiteSpace(RoleKey)
                ? $" / RoleKey:{RoleKey}"
                : "";
            string bleNameText = !string.IsNullOrWhiteSpace(BleName)
                ? $" / BLE名:{BleName}"
                : "";
            string effectiveWiFiName = !string.IsNullOrWhiteSpace(WiFiDirectName)
                ? WiFiDirectName
                : PendingWiFiDirectName;
            string wifiNameText = !string.IsNullOrWhiteSpace(effectiveWiFiName)
                ? $" / Wi-Fi名:{effectiveWiFiName}"
                : "";
            string kindText = !string.IsNullOrWhiteSpace(DeviceKind)
                ? $" / Kind:{DeviceKind}"
                : "";
            string matchText = MatchState switch
            {
                PeerMatchState.Provisional => " / 照合:仮紐付け",
                PeerMatchState.Confirmed => " / 照合:確認済み",
                PeerMatchState.Rejected => " / 照合:不一致",
                _ => " / 照合:未確認"
            };

            return $"{DisplayName} / {SourceText} / {bleText} / {wifiText} / {tcpText} / {helloText} / {statusText}{remoteIpText}{sessionText}{roleKeyText}{bleNameText}{wifiNameText}{kindText}{matchText}";
        }
    }
}
