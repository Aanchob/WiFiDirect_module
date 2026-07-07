using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.WiFiDirect.Models;

namespace direct_module.Network
{
    public class ChatConnectionManager
    {
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(30);

        private readonly List<ChatConnection> _connections = new();
        private readonly HashSet<string> _receivedMessageIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private CancellationTokenSource? _keepAliveCancellation;
        private Task? _keepAliveTask;
        private string _keepAliveSenderId = "";
        private string _keepAliveSenderName = "";
        private string _keepAliveShortSessionId = "";

        public event Action<string>? LogReceived;
        public event Action<ChatMessage, ChatConnection>? MessageReceived;
        public event Action<ChatConnection>? ConnectionDisconnected;
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

        public ChatConnection? GetConnectionByPeerId(string peerId)
        {
            return FindByPeerId(peerId);
        }

        public ChatConnection? FindByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId))
            {
                return null;
            }

            lock (_lock)
            {
                return _connections.FirstOrDefault(connection =>
                    string.Equals(connection.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public ChatConnection? GetConnectionByShortSessionId(string shortSessionId)
        {
            return FindByShortSessionId(shortSessionId);
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

        public ChatConnection? GetConnectionByRemoteIpAddress(string remoteIpAddress)
        {
            return FindByRemoteIpAddress(remoteIpAddress);
        }

        public bool HasConnectionForPeer(PeerInfo peer)
        {
            return FindForPeer(peer) != null;
        }

        public bool IsPreparingForPeer(PeerInfo peer)
        {
            return FindForPeer(peer)?.IsPreparing == true;
        }

        public ChatConnection? FindForPeer(PeerInfo peer)
        {
            return FindByShortSessionId(peer.ShortSessionId) ??
                   FindByPeerId(GetPeerConnectionId(peer)) ??
                   FindByRemoteIpAddress(peer.RemoteIpAddress) ??
                   FindByPeerId(peer.DeviceId);
        }

        public void StartKeepAlive(string senderId, string senderName, string shortSessionId)
        {
            lock (_lock)
            {
                _keepAliveSenderId = senderId;
                _keepAliveSenderName = senderName;
                _keepAliveShortSessionId = shortSessionId;

                if (_keepAliveCancellation != null)
                {
                    return;
                }

                _keepAliveCancellation = new CancellationTokenSource();
                _keepAliveTask = RunKeepAliveAsync(_keepAliveCancellation.Token);
            }

            LogReceived?.Invoke("定期ping開始");
        }

        public void StopKeepAlive()
        {
            CancellationTokenSource? cancellation;

            lock (_lock)
            {
                cancellation = _keepAliveCancellation;
                _keepAliveCancellation = null;
                _keepAliveTask = null;
            }

            if (cancellation == null)
            {
                return;
            }

            cancellation.Cancel();
            cancellation.Dispose();
            LogReceived?.Invoke("定期ping停止");
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
                    .Where(connection => connection.IsConnected && connection.IsReady && connection != exceptConnection)
                    .ToList();
            }

            LogReceived?.Invoke($"Host Broadcast対象Connection数: {targets.Count}");
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

        private async Task RunKeepAliveAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(PingInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await SendKeepAlivePingAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task SendKeepAlivePingAsync()
        {
            List<ChatConnection> targets;
            string senderId;
            string senderName;
            string shortSessionId;

            lock (_lock)
            {
                targets = _connections
                    .Where(IsKeepAliveTarget)
                    .ToList();
                senderId = _keepAliveSenderId;
                senderName = _keepAliveSenderName;
                shortSessionId = _keepAliveShortSessionId;
            }

            DateTime now = DateTime.Now;

            foreach (ChatConnection connection in targets)
            {
                if (IsPongTimedOut(connection, now))
                {
                    LogReceived?.Invoke($"PONGタイムアウト: Peer={GetConnectionPeerName(connection)}");
                    LogReceived?.Invoke($"PONGタイムアウトにより切断扱い: Peer={GetConnectionPeerName(connection)}");
                    connection.Close();
                    continue;
                }

                if (connection.IsPingWaiting)
                {
                    continue;
                }

                LogReceived?.Invoke($"定期PING送信: Peer={GetConnectionPeerName(connection)}");
                await connection.SendPingAsync(senderId, senderName, shortSessionId);
            }
        }

        private static bool IsKeepAliveTarget(ChatConnection connection)
        {
            return connection.IsReady &&
                   connection.IsHelloVerified &&
                   connection.IsConnected &&
                   !connection.IsPreparing;
        }

        private static bool IsPongTimedOut(ChatConnection connection, DateTime now)
        {
            return connection.LastPingAt.HasValue &&
                   connection.IsPingWaiting &&
                   (!connection.LastPongAt.HasValue || connection.LastPongAt.Value < connection.LastPingAt.Value) &&
                   now - connection.LastPingAt.Value > PongTimeout;
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
            LogReceived?.Invoke($"ChatConnectionManagerから切断Connectionを削除: Peer={connection.PeerName}");
            ConnectionDisconnected?.Invoke(connection);
            RemoveConnection(connection);
        }

        private static string GetConnectionPeerName(ChatConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.PeerName))
            {
                return connection.PeerName;
            }

            if (!string.IsNullOrWhiteSpace(connection.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return connection.PeerId;
        }

        private static string GetPeerConnectionId(PeerInfo peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.ShortSessionId))
            {
                return peer.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                return peer.DeviceId;
            }

            if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                return peer.RemoteIpAddress;
            }

            return peer.DisplayName;
        }
    }
}
