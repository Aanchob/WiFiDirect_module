using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace direct_module.WiFiDirect.Models;

public class PeerInfo
{
    public string DisplayName { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public bool DiscoveredByBle { get; set; }

    public bool DiscoveredByWiFiDirect { get; set; }

    public string BleName { get; set; } = "";

    public string WiFiDirectName { get; set; } = "";

    public string MatchKey { get; set; } = "";

    public string ShortSessionId { get; set; } = "";

    public string RoleKey { get; set; } = "";

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

    public bool CanConnect { get; set; }

    public bool IsGroupChat { get; set; }

    public bool CanDisconnect => !IsGroupChat && (IsConnected || IsTcpConnected || IsChatReady) && !IsConnectingWiFiDirect && !IsPreparingChatTcp;

    public bool ActionButtonEnabled => CanConnect || CanDisconnect;

    public double ActionButtonOpacity => ActionButtonEnabled ? 1.0 : 0.25;

    public string ActionButtonGlyph => CanDisconnect ? "\uE71A" : "\uE8A7";

    public string ActionButtonTooltip => CanDisconnect ? "切断" : "接続";

    public Visibility ActionButtonVisibility => IsGroupChat ? Visibility.Collapsed : Visibility.Visible;

    public Brush ActionButtonBackground => CanDisconnect
        ? new SolidColorBrush(Color.FromArgb(0x2C, 0xFF, 0x5F, 0x5F))
        : new SolidColorBrush(Color.FromArgb(0x2C, 0xB8, 0xFF, 0x6A));

    public Brush ActionButtonBorderBrush => CanDisconnect
        ? new SolidColorBrush(Color.FromArgb(0x77, 0xFF, 0x5F, 0x5F))
        : new SolidColorBrush(Color.FromArgb(0x77, 0xB8, 0xFF, 0x6A));

    public Brush ActionButtonForeground => CanDisconnect
        ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x5F, 0x5F))
        : new SolidColorBrush(Color.FromArgb(0xFF, 0xB8, 0xFF, 0x6A));

    public double ConnectButtonOpacity => CanConnect ? 1.0 : 0.25;

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
                return "グループチャット (複数人接続時のみ有効)";
            }

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
            string wifiNameText = !string.IsNullOrWhiteSpace(WiFiDirectName)
                ? $" / Wi-Fi名:{WiFiDirectName}"
                : "";
            string kindText = !string.IsNullOrWhiteSpace(DeviceKind)
                ? $" / Kind:{DeviceKind}"
                : "";

            return $"{DisplayName} / {SourceText} / {bleText} / {wifiText} / {tcpText} / {helloText} / {statusText}{remoteIpText}{sessionText}{roleKeyText}{bleNameText}{wifiNameText}{kindText}";
        }
    }
}
