using System;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectManager
    {
        private readonly WiFiDirectListener _listener;
        private readonly WiFiDirectAdvertiser _advertiser;
        private readonly WiFiDirectConnector _connector;
        private readonly WiFiDirectScanner _scanner;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? ConnectionRequested;
        public event Action<PeerInfo>? PeerFound;
        public event Action<PeerInfo>? Connected;

        public WiFiDirectManager()
        {
            _listener = new WiFiDirectListener();
            _advertiser = new WiFiDirectAdvertiser();
            _connector = new WiFiDirectConnector();
            _scanner = new WiFiDirectScanner();

            _listener.LogReceived += OnListenerLogReceived;
            _listener.ConnectionRequested += OnListenerConnectionRequested;

            _advertiser.LogReceived += OnAdvertiserLogReceived;

            _connector.LogReceived += OnConnectorLogReceived;
            _connector.Connected += OnConnectorConnected;

            _scanner.LogReceived += OnScannerLogReceived;
            _scanner.PeerFound += OnScannerPeerFound;
        }

        private void OnListenerLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnAdvertiserLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private async void OnListenerConnectionRequested(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
            ConnectionRequested?.Invoke(peer);

            LogReceived?.Invoke($"Manager: 接続要求元へ接続します {peer.DisplayName}");

            await ConnectAsync(peer);
        }

        private void OnConnectorLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnConnectorConnected(WiFiDirectSession session)
        {
            LogReceived?.Invoke($"Manager: 接続完了 {session.Peer.DisplayName}");
            Connected?.Invoke(session.Peer);
        }

        private void OnScannerLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
        }

        public void Start()
        {
            LogReceived?.Invoke("Manager: Wi-Fi Direct Listener + Advertisement 起動開始");
            _listener.Start();
            _advertiser.Start(listenerRegistered: _listener.IsStarted);
        }

        public async Task StartScanAsync() => await _scanner.StartAssociationEndpointAsync();

        public async Task StartDefaultScanAsync() => await _scanner.StartDefaultAsync();

        public async Task StartAssociationEndpointScanAsync() => await _scanner.StartAssociationEndpointAsync();

        public void StopScan()
        {
            _scanner.Stop();
        }

        public async Task ConnectAsync(PeerInfo peer)
        {
            await _connector.ConnectAsync(peer);
        }
    }
}
