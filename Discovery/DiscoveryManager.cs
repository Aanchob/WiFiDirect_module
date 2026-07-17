using direct_module.WiFiDirect.Models;
using System;
using System.Threading.Tasks;

namespace direct_module.Discovery
{
    public class DiscoveryManager
    {
        private readonly BleAdvertiser _advertiser;
        private readonly BleScanner _scanner;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;
        public event Action<BleConnectionRequest>? ConnectionRequestReceived;

        public DiscoveryManager()
        {
            _advertiser = new BleAdvertiser();
            _scanner = new BleScanner();
            _advertiser.LogReceived += OnLogReceived;
            _scanner.LogReceived += OnLogReceived;
            _scanner.PeerFound += OnScannerPeerFound;
            _scanner.ConnectionRequestReceived += OnConnectionRequestReceived;
        }

        public Task StartAdvertiseAsync(string displayName, Guid sessionId, int tcpPort) =>
            _advertiser.StartAsync(displayName, sessionId, tcpPort);

        public Task SendConnectionRequestAsync(string targetShortSessionId) =>
            _advertiser.PublishConnectionRequestAsync(targetShortSessionId);

        public void StopAdvertise() => _advertiser.Stop();

        public void StartScan() => _scanner.Start();

        public void StopScan() => _scanner.Stop();

        private void OnLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
        }

        private void OnConnectionRequestReceived(BleConnectionRequest request)
        {
            ConnectionRequestReceived?.Invoke(request);
        }
    }
}
