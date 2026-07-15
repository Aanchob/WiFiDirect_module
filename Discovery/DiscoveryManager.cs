using System;
using System.Linq;
using System.Diagnostics;
using direct_module.WiFiDirect.Models;

namespace direct_module.Discovery
{
    public class DiscoveryManager
    {
        private readonly BleAdvertiser _advertiser;
        private readonly BleScanner _scanner;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;
        public event Action<PeerInfo>? PeerRemoved;

        public DiscoveryManager()
        {
            _advertiser = new BleAdvertiser();
            _scanner = new BleScanner();

            _advertiser.LogReceived += OnAdvertiserLogReceived;

            _scanner.LogReceived += OnScannerLogReceived;
            _scanner.PeerFound += OnScannerPeerFound;
            _scanner.PeerRemoved += OnScannerPeerRemoved;
        }

        public void StartAdvertise(string displayName, Guid sessionId, int tcpPort)
        {
            _advertiser.Start(displayName, sessionId, tcpPort);
        }

        public void StopAdvertise()
        {
            _advertiser.Stop();
        }

        public void StartScan()
        {
            _scanner.Start();
        }

        public void StopScan()
        {
            _scanner.Stop();
        }

        private void OnAdvertiserLogReceived(string message)
        {
            InvokeSafely(LogReceived, message);
        }

        private void OnScannerLogReceived(string message)
        {
            InvokeSafely(LogReceived, message);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            InvokeSafely(PeerFound, peer);
        }

        private void OnScannerPeerRemoved(PeerInfo peer)
        {
            InvokeSafely(PeerRemoved, peer);
        }

        private static void InvokeSafely<T>(Action<T>? handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
            {
                try { handler(value); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DiscoveryManager event handler failed: {ex}");
                }
            }
        }
    }
}
