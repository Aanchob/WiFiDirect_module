using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
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
        private readonly List<WiFiDirectSession> _sessions = new();
        private readonly object _sessionsGate = new();

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

        public Task StartAsync() => StartAsync("", "");

        public Task StartAsync(string displayName, string shortSessionId) =>
            StartAsync(displayName, shortSessionId, autonomousGroupOwner: false);

        public async Task StartAsync(string displayName, string shortSessionId, bool autonomousGroupOwner)
        {
            LogReceived?.Invoke("Manager: Wi-Fi Direct Listener + Advertisement 開始");
            _listener.Start();
            await _advertiser.StartAsync(
                listenerRegistered: _listener.IsStarted,
                displayName: displayName,
                shortSessionId: shortSessionId,
                autonomousGroupOwner: autonomousGroupOwner);
        }

        public Task RestartAdvertisementAsync(string displayName, string shortSessionId) =>
            RestartAdvertisementAsync(displayName, shortSessionId, autonomousGroupOwner: false);

        public async Task RestartAdvertisementAsync(
            string displayName,
            string shortSessionId,
            bool autonomousGroupOwner)
        {
            LogReceived?.Invoke("Manager: Wi-Fi Direct Advertisement 再開始");
            _listener.Start();
            await _advertiser.RestartAsync(
                listenerRegistered: _listener.IsStarted,
                displayName: displayName,
                shortSessionId: shortSessionId,
                autonomousGroupOwner: autonomousGroupOwner);
        }

        public void Stop()
        {
            _scanner.Stop();
            _advertiser.Stop();
            _listener.Stop();
            List<WiFiDirectSession> sessions;
            lock (_sessionsGate)
            {
                sessions = new List<WiFiDirectSession>(_sessions);
                _sessions.Clear();
            }

            foreach (WiFiDirectSession session in sessions)
            {
                session.Dispose();
            }
        }

        public Task StopAdvertisementAsync() => _advertiser.StopAsync();

        public Task StartScanAsync() => _scanner.StartAssociationEndpointAsync();

        public Task StartDefaultScanAsync() => _scanner.StartDefaultAsync();

        public Task StartAssociationEndpointScanAsync() => _scanner.StartAssociationEndpointAsync();

        public Task StopScanAsync() => _scanner.StopAsync();

        public Task ConnectAsync(PeerInfo peer) => _connector.ConnectAsync(peer);

        private void OnLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnListenerConnectionRequested(PeerInfo peer)
        {
            ConnectionRequested?.Invoke(peer);
            LogReceived?.Invoke($"Manager: 接続要求を受信しました: {peer.DisplayName}");
            LogReceived?.Invoke($"PendingRequest DeviceId: {peer.DeviceId}");
        }

        private async void OnIncomingConnectionRequested(
            PeerInfo peer,
            WiFiDirectConnectionRequest request)
        {
            try
            {
                await _connector.AcceptIncomingConnectionAsync(peer, request);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"接続要求のAccept失敗: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                request.Dispose();
            }
        }

        private void OnConnectorConnected(WiFiDirectSession session)
        {
            lock (_sessionsGate)
            {
                _sessions.Add(session);
            }
            LogReceived?.Invoke($"Manager: Wi-Fi Direct接続完了: {session.Peer.DisplayName}");
            Connected?.Invoke(session.Peer);
        }

        private void OnScannerPeerFound(PeerInfo peer)
        {
            PeerFound?.Invoke(peer);
        }
    }
}
