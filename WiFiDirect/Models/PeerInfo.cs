using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace direct_module.WiFiDirect.Models;

public class PeerInfo
{
    public string DisplayName { get; set; } = "";

    // Wi-Fi Direct 用
    public string DeviceId { get; set; } = "";

    // BLEで発見した端末かどうか
    public bool DiscoveredByBle { get; set; }

    // BLE広告で共有するセッションID
    public string ShortSessionId { get; set; } = "";

    // TCP通信で使う予定のポート番号
    public int TcpPort { get; set; }

    public bool IsConnected { get; set; }

    public string IpAddress { get; set; } = "";

    public bool? IsEnabled { get; set; }

    public string DeviceKind { get; set; } = "";

    public string SourceText => DiscoveredByBle ? "BLE" : "Wi-Fi Direct";

    public string DisplayText
    {
        get
        {
            string portText = TcpPort > 0
                ? $" / Port:{TcpPort}"
                : "";

            string sessionText = !string.IsNullOrWhiteSpace(ShortSessionId)
                ? $" / ID:{ShortSessionId}"
                : "";

            string ipText = !string.IsNullOrWhiteSpace(IpAddress)
                ? $" / IP:{IpAddress}"
                : "";

            string deviceIdText = !string.IsNullOrWhiteSpace(DeviceId)
                ? " / DeviceId:あり"
                : " / DeviceId:なし";

            string enabledText = IsEnabled.HasValue
                ? $" / IsEnabled:{IsEnabled.Value}"
                : "";

            string kindText = !string.IsNullOrWhiteSpace(DeviceKind)
                ? $" / Kind:{DeviceKind}"
                : "";

            return $"[{SourceText}] {DisplayName}{deviceIdText}{enabledText}{kindText}{portText}{sessionText}{ipText}";
        }
    }
}
