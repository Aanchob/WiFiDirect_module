using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace direct_module.WiFiDirect.Models;

public class PeerInfo
{
    /// <summary>
    /// 相手に表示する名前
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Wi-Fi Direct の DeviceId
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 接続済みか
    /// </summary>
    public bool IsConnected { get; set; }
}
