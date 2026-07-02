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
    public Guid SessionId { get; set; }

    public string ShortSessionId { get; set; } = "";

    // TCP通信で使う予定のポート番号
    public int TcpPort { get; set; }

    public bool IsConnected { get; set; }
}