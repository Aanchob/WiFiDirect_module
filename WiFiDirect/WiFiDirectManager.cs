using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectManager
    {
        private readonly WiFiDirectListener _listener;
        private readonly WiFiDirectConnector _connector;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? ConnectionRequested;

        public WiFiDirectManager()
        {
            _listener = new WiFiDirectListener();
            _connector = new WiFiDirectConnector();

            _listener.LogReceived += OnListenerLogReceived;
            _listener.ConnectionRequested += OnListenerConnectionRequested;

            _connector.LogReceived += OnConnectorLogReceived;
            _connector.Connected += OnConnectorConnected;
        }

        private void OnListenerLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private async void OnListenerConnectionRequested(PeerInfo peer)
        {
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
        }

        public void Start()
        {
            _listener.Start();
        }
        public async Task ConnectAsync(PeerInfo peer)
        {
            await _connector.ConnectAsync(peer);
        }
    }
}
