using System;
using System.Linq;
using System.Diagnostics;
using direct_module.WiFiDirect.Models;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public enum WiFiDirectConnectionDirection
    {
        Outgoing,
        Incoming
    }

    public class WiFiDirectSession : IDisposable
    {
        private readonly object _gate = new();
        private bool _disposed;
        private bool _disconnectNotified;

        public PeerInfo Peer { get; }

        public WiFiDirectDevice Device { get; }

        public WiFiDirectConnectionDirection Direction { get; }

        public string RemoteIpAddress { get; }

        public event Action<WiFiDirectSession>? Disconnected;

        public WiFiDirectSession(
            PeerInfo peer,
            WiFiDirectDevice device,
            WiFiDirectConnectionDirection direction,
            string remoteIpAddress)
        {
            Peer = peer;
            Device = device;
            Direction = direction;
            RemoteIpAddress = remoteIpAddress;
            Device.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }

            Device.Dispose();
        }

        private void OnConnectionStatusChanged(WiFiDirectDevice sender, object args)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            WiFiDirectConnectionStatus status;
            try
            {
                status = sender.ConnectionStatus;
            }
            catch (Exception)
            {
                return;
            }

            if (status == WiFiDirectConnectionStatus.Connected)
            {
                return;
            }

            Action<WiFiDirectSession>? disconnected;
            lock (_gate)
            {
                if (_disposed || _disconnectNotified)
                {
                    return;
                }

                _disconnectNotified = true;
                disconnected = Disconnected;
            }

            if (disconnected == null) return;
            foreach (Action<WiFiDirectSession> handler in
                     disconnected.GetInvocationList().Cast<Action<WiFiDirectSession>>())
            {
                try { handler(this); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WiFiDirectSession disconnect handler failed: {ex}");
                }
            }
        }
    }
}
