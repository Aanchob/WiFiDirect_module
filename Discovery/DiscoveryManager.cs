using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Discovery
{
    public class DiscoveryManager
    {
        private readonly BleAdvertiser _advertiser;
        private readonly BleScanner _scanner;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;

        public DiscoveryManager()
        {
            _advertiser = new BleAdvertiser();
            _scanner = new BleScanner();

            _advertiser.LogReceived += OnAdvertiserLogReceived;

            _scanner.LogReceived += OnScannerLogReceived;
            _scanner.PeerFound += OnScannerPeerFound;
        }

        public void StartAdvertise(string displayName, Guid sessionId, int tcpPort, string ipAddress)
        {
            _advertiser.Start(displayName, sessionId, tcpPort, ipAddress);
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
            LogReceived?.Invoke(message);
        }

        private void OnScannerLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
        }
    }
}