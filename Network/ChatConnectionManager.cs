using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace direct_module.Network
{
    public class ChatConnectionManager
    {
        private readonly List<ChatConnection> _connections = new();
        private readonly HashSet<string> _receivedMessageIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public event Action<string>? LogReceived;
        public event Action<ChatMessage, ChatConnection>? MessageReceived;
        public event Action? ConnectionsChanged;

        public IReadOnlyList<ChatConnection> Connections
        {
            get
            {
                lock (_lock)
                {
                    return _connections.ToList();
                }
            }
        }

        public int ConnectedCount
        {
            get
            {
                lock (_lock)
                {
                    return _connections.Count(connection => connection.IsConnected);
                }
            }
        }

        public void AddConnection(ChatConnection connection)
        {
            lock (_lock)
            {
                if (_connections.Contains(connection))
                {
                    return;
                }

                _connections.Add(connection);
            }

            connection.LogReceived += OnConnectionLogReceived;
            connection.MessageReceived += OnConnectionMessageReceived;
            connection.Disconnected += OnConnectionDisconnected;

            LogReceived?.Invoke($"ChatConnectionManager: 接続追加 Peer={connection.PeerName}, Count={ConnectedCount}");
            ConnectionsChanged?.Invoke();
        }

        public void RemoveConnection(ChatConnection connection)
        {
            bool removed;

            lock (_lock)
            {
                removed = _connections.Remove(connection);
            }

            if (!removed)
            {
                return;
            }

            connection.LogReceived -= OnConnectionLogReceived;
            connection.MessageReceived -= OnConnectionMessageReceived;
            connection.Disconnected -= OnConnectionDisconnected;

            LogReceived?.Invoke($"ChatConnectionManager: 接続削除 Peer={connection.PeerName}, Count={ConnectedCount}");
            ConnectionsChanged?.Invoke();
        }

        public ChatConnection? FindByPeerId(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId))
            {
                return null;
            }

            lock (_lock)
            {
                return _connections.FirstOrDefault(connection =>
                    string.Equals(connection.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public ChatConnection? FindByRemoteIpAddress(string remoteIpAddress)
        {
            if (string.IsNullOrWhiteSpace(remoteIpAddress))
            {
                return null;
            }

            lock (_lock)
            {
                return _connections.FirstOrDefault(connection =>
                    string.Equals(connection.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase));
            }
        }

        public async Task BroadcastAsync(ChatMessage message)
        {
            await BroadcastExceptAsync(message, null);
        }

        public async Task BroadcastExceptAsync(ChatMessage message, ChatConnection? exceptConnection)
        {
            List<ChatConnection> targets;

            lock (_lock)
            {
                targets = _connections
                    .Where(connection => connection.IsConnected && connection != exceptConnection)
                    .ToList();
            }

            LogReceived?.Invoke($"ChatConnectionManager: Broadcast開始 TargetCount={targets.Count}, MessageId={message.MessageId}");

            foreach (ChatConnection connection in targets)
            {
                await connection.SendAsync(message);
            }

            LogReceived?.Invoke("ChatConnectionManager: Broadcast完了");
        }

        public bool MarkMessageSeen(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return true;
            }

            lock (_lock)
            {
                return _receivedMessageIds.Add(messageId);
            }
        }

        private void OnConnectionLogReceived(string message)
        {
            LogReceived?.Invoke(message);
        }

        private void OnConnectionMessageReceived(ChatMessage message, ChatConnection connection)
        {
            if (!MarkMessageSeen(message.MessageId))
            {
                LogReceived?.Invoke($"重複メッセージを無視: MessageId={message.MessageId}");
                return;
            }

            MessageReceived?.Invoke(message, connection);
        }

        private void OnConnectionDisconnected(ChatConnection connection)
        {
            RemoveConnection(connection);
        }
    }
}
