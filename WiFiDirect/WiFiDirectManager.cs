using direct_module.WiFiDirect.Models;
using System;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;

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

            _listener.LogReceived += OnLogReceived;
            _listener.ConnectionRequested += OnListenerConnectionRequested;
            _listener.IncomingConnectionRequested += OnIncomingConnectionRequested;

            _advertiser.LogReceived += OnLogReceived;

            _connector.LogReceived += OnLogReceived;
            _connector.Connected += OnConnectorConnected;

            _scanner.LogReceived += OnLogReceived;
            _scanner.PeerFound += OnScannerPeerFound;
        }

        public void Start()
        {
            Start("", "");
        }

        public void Start(string displayName, string shortSessionId)
        {
            LogReceived?.Invoke("Manager: Wi-Fi Direct Listener + Advertisement 起動開始");
            _listener.Start();
            _advertiser.Start(
                listenerRegistered: _listener.IsStarted,
                displayName: displayName,
                shortSessionId: shortSessionId);
        }

        public async Task StartScanAsync()
        {
            await _scanner.StartAssociationEndpointAsync();
        }

        public async Task StartDefaultScanAsync()
        {
            await _scanner.StartDefaultAsync();
        }

        public async Task StartAssociationEndpointScanAsync()
        {
            await _scanner.StartAssociationEndpointAsync();
        }

        public void StopScan()
        {
            _scanner.Stop();
        }

        public async Task ConnectAsync(PeerInfo peer)
        {
            await _connector.ConnectAsync(peer);
        }

        private void OnLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnListenerConnectionRequested(PeerInfo peer)
        {
            ConnectionRequested?.Invoke(peer);

            LogReceived?.Invoke($"Manager: 接続要求を受信しました {peer.DisplayName}");
            LogReceived?.Invoke($"PendingRequest DeviceId: {peer.DeviceId}");
            LogReceived?.Invoke("これは接続要求Accept用なのでPeerListには追加しません");
        }

        private async void OnIncomingConnectionRequested(
            PeerInfo peer,
            WiFiDirectConnectionRequest request)
        {
            LogReceived?.Invoke("受信側で接続要求をacceptします");

            try
            {
                await _connector.AcceptIncomingConnectionAsync(peer, request);
            }
            finally
            {
                request.Dispose();
                LogReceived?.Invoke("Wi-Fi Direct接続要求Requestを破棄しました");
            }
        }

        private void OnConnectorConnected(WiFiDirectSession session)
        {
            LogReceived?.Invoke($"Manager: 接続完了 {session.Peer.DisplayName}");
            Connected?.Invoke(session.Peer);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
        }
    }
}
