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

            return $"[{SourceText}] {DisplayName}{portText}{sessionText}{ipText}";
        }
    }
}