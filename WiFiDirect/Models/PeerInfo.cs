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

    public bool IsConnected { get; set; }
}