using System;
using direct_module.WiFiDirect.Models;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectSession : IDisposable
    {
        public PeerInfo Peer { get; }

        public WiFiDirectDevice Device { get; }

        public WiFiDirectSession(PeerInfo peer, WiFiDirectDevice device)
        {
            Peer = peer;
            Device = device;

            Peer.IsConnected = true;
        }

        public void Dispose()
        {
            Peer.IsConnected = false;
            Device.Dispose();
        }
    }
}