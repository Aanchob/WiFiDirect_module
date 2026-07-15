using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Services;
using direct_module.WiFiDirect.Models;

namespace direct_module.Network
{
    public sealed class BroadcastDeliveryResult
    {
        public string PeerId { get; init; } = "";
        public bool IsSuccess { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    public sealed class BroadcastResult
    {
        public IReadOnlyList<BroadcastDeliveryResult> Deliveries { get; init; } = Array.Empty<BroadcastDeliveryResult>();
        public bool AnySucceeded => Deliveries.Any(delivery => delivery.IsSuccess);
        public bool AllSucceeded => Deliveries.Count > 0 && Deliveries.All(delivery => delivery.IsSuccess);
        public IReadOnlyList<string> FailedPeerIds => Deliveries
            .Where(delivery => !delivery.IsSuccess)
            .Select(delivery => delivery.PeerId)
            .ToList();
    }

    public sealed class ChatConnectionManager
    {
        private const int DefaultMaximumConnections = 32;
        private const int MaximumUnverifiedConnections = 8;
        private const int MaximumRememberedMessageIds = 4096;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(30);

        private readonly List<ChatConnection> _connections = new();
        private readonly HashSet<string> _receivedMessageIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _receivedMessageIdOrder = new();
        private readonly object _lock = new();
        private readonly int _maximumConnections;
        private CancellationTokenSource? _keepAliveCancellation;
        private Task? _keepAliveTask;
        private bool _keepAliveShouldRun;
        private string _keepAliveSenderId = "";
        private string _keepAliveSenderName = "";
        private string _keepAliveShortSessionId = "";

        public ChatConnectionManager(int maximumConnections = DefaultMaximumConnections)
        {
            if (maximumConnections <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
            _maximumConnections = maximumConnections;
        }

        public event Action<string>? LogReceived;
        public event Action<ChatMessage, ChatConnection>? MessageReceived;
        public event Action<ChatConnection>? ConnectionDisconnected;
        public event Action? ConnectionsChanged;

        public IReadOnlyList<ChatConnection> Connections
        {
            get { lock (_lock) return _connections.ToList(); }
        }

        public int ConnectedCount
        {
            get { lock (_lock) return _connections.Count(connection => connection.IsConnected); }
        }

        public int MaximumConnections => _maximumConnections;

        public void AddConnection(ChatConnection connection)
        {
            TryAddConnection(connection);
        }

        public bool TryAddConnection(ChatConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            bool added;
            lock (_lock)
            {
                if (_connections.Contains(connection)) return true;
                added = _connections.Count < _maximumConnections &&
                        _connections.Count(candidate => !candidate.IsHelloVerified) < MaximumUnverifiedConnections;
                if (added) _connections.Add(connection);
            }

            if (!added)
            {
                string reason = $"Chat connection limit reached (total={_maximumConnections}, unverified={MaximumUnverifiedConnections}); connection rejected.";
                SafeLog(reason);
                connection.Reject(reason);
                return false;
            }

            connection.LogReceived += OnConnectionLogReceived;
            connection.MessageReceived += OnConnectionMessageReceived;
            connection.Disconnected += OnConnectionDisconnected;
            connection.IdentityVerified += OnConnectionIdentityVerified;
            SafeLog($"ChatConnectionManager: 接続追加 Peer={connection.PeerName}, Count={ConnectedCount}");
            NotifyConnectionsChanged();
            if (connection.IsHelloVerified) OnConnectionIdentityVerified(connection);
            return true;
        }

        public void RemoveConnection(ChatConnection connection)
        {
            bool removed;
            lock (_lock) removed = _connections.Remove(connection);
            if (!removed) return;

            connection.LogReceived -= OnConnectionLogReceived;
            connection.MessageReceived -= OnConnectionMessageReceived;
            connection.Disconnected -= OnConnectionDisconnected;
            connection.IdentityVerified -= OnConnectionIdentityVerified;
            SafeLog($"ChatConnectionManager: 接続削除 Peer={connection.PeerName}, Count={ConnectedCount}");
            NotifyConnectionsChanged();
        }

        public ChatConnection? FindByPeerId(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return null;
            lock (_lock)
            {
                return FindBestConnection(_connections.Where(connection =>
                    string.Equals(GetRemotePeerId(connection), peerId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public ChatConnection? GetConnectionByPeerId(string peerId) => FindByPeerId(peerId);

        public ChatConnection? FindByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId)) return null;
            lock (_lock)
            {
                List<ChatConnection> candidates = _connections.Where(connection =>
                    string.Equals(connection.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count <= 1) return candidates.FirstOrDefault();

                string[] stablePeerIds = candidates
                    .Select(GetRemotePeerId)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return stablePeerIds.Length == 1 && candidates.All(connection =>
                        string.IsNullOrWhiteSpace(GetRemotePeerId(connection)) ||
                        string.Equals(GetRemotePeerId(connection), stablePeerIds[0], StringComparison.OrdinalIgnoreCase))
                    ? FindBestConnection(candidates)
                    : null;
            }
        }

        public ChatConnection? GetConnectionByShortSessionId(string shortSessionId) =>
            FindByShortSessionId(shortSessionId);

        public ChatConnection? FindByRemoteIpAddress(string remoteIpAddress)
        {
            if (string.IsNullOrWhiteSpace(remoteIpAddress)) return null;
            lock (_lock)
            {
                List<ChatConnection> candidates = _connections.Where(connection =>
                    string.Equals(connection.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count <= 1) return candidates.FirstOrDefault();
                string[] stablePeerIds = candidates.Select(GetRemotePeerId)
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return stablePeerIds.Length == 1 ? FindBestConnection(candidates) : null;
            }
        }

        public ChatConnection? GetConnectionByRemoteIpAddress(string remoteIpAddress) =>
            FindByRemoteIpAddress(remoteIpAddress);

        public bool HasConnectionForPeer(PeerInfo peer) => FindForPeer(peer) != null;
        public bool IsPreparingForPeer(PeerInfo peer) => FindForPeer(peer)?.IsPreparing == true;

        public ChatConnection? FindForPeer(PeerInfo peer)
        {
            ArgumentNullException.ThrowIfNull(peer);
            ChatConnection? exact = FindByPeerId(peer.PeerId);
            if (exact != null || IsStablePeerId(peer.PeerId)) return exact;

            return FindByPeerId(peer.DeviceId) ??
                   FindByShortSessionId(peer.ShortSessionId) ??
                   FindByRemoteIpAddress(peer.RemoteIpAddress);
        }

        public void StartKeepAlive(string senderId, string senderName, string shortSessionId)
        {
            ChatMessageValidator.ValidateOutbound(new ChatMessage
            {
                Type = "ping",
                SenderId = senderId,
                SenderName = senderName,
                ShortSessionId = shortSessionId,
                Body = ""
            });

            lock (_lock)
            {
                _keepAliveShouldRun = true;
                _keepAliveSenderId = senderId;
                _keepAliveSenderName = senderName;
                _keepAliveShortSessionId = shortSessionId;
                if (_keepAliveCancellation != null) return;
                _keepAliveCancellation = new CancellationTokenSource();
                _keepAliveTask = RunKeepAliveAsync(_keepAliveCancellation.Token);
            }

            SafeLog("定期ping開始");
        }

        public void StopKeepAlive() => _ = StopKeepAliveAsync();

        public async Task StopKeepAliveAsync()
        {
            CancellationTokenSource? cancellation;
            Task? task;
            lock (_lock)
            {
                _keepAliveShouldRun = false;
                cancellation = _keepAliveCancellation;
                task = _keepAliveTask;
            }

            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            if (task != null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            bool restarted = false;
            lock (_lock)
            {
                if (ReferenceEquals(_keepAliveCancellation, cancellation))
                {
                    _keepAliveCancellation = null;
                    _keepAliveTask = null;
                    if (_keepAliveShouldRun)
                    {
                        _keepAliveCancellation = new CancellationTokenSource();
                        _keepAliveTask = RunKeepAliveAsync(_keepAliveCancellation.Token);
                        restarted = true;
                    }
                }
            }

            cancellation.Dispose();
            SafeLog(restarted ? "定期ping再開" : "定期ping停止");
        }

        public async Task CloseAllAsync(CancellationToken cancellationToken = default)
        {
            await StopKeepAliveAsync().ConfigureAwait(false);
            List<ChatConnection> connections;
            lock (_lock) connections = _connections.ToList();
            try
            {
                await Task.WhenAll(connections.Select(connection =>
                    connection.CloseAsync(cancellationToken))).ConfigureAwait(false);
            }
            finally
            {
                foreach (ChatConnection connection in connections) RemoveConnection(connection);
            }
        }

        public Task<BroadcastResult> BroadcastAsync(ChatMessage message) =>
            BroadcastExceptAsync(message, null, CancellationToken.None);

        public Task<BroadcastResult> BroadcastAsync(ChatMessage message, CancellationToken cancellationToken) =>
            BroadcastExceptAsync(message, null, cancellationToken);

        public Task<BroadcastResult> BroadcastExceptAsync(ChatMessage message, ChatConnection? exceptConnection) =>
            BroadcastExceptAsync(message, exceptConnection, CancellationToken.None);

        public async Task<BroadcastResult> BroadcastExceptAsync(
            ChatMessage message,
            ChatConnection? exceptConnection,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            List<ChatConnection> targets;
            lock (_lock)
            {
                targets = _connections
                    .Where(connection => connection.IsConnected && connection.IsReady && connection != exceptConnection)
                    .ToList();
            }

            SafeLog($"ChatConnectionManager: Broadcast開始 TargetCount={targets.Count}, MessageId={message.MessageId}");
            BroadcastDeliveryResult[] deliveries = await Task.WhenAll(targets.Select(connection =>
                SendBroadcastSafelyAsync(connection, message, cancellationToken))).ConfigureAwait(false);
            BroadcastResult result = new() { Deliveries = deliveries };
            SafeLog("ChatConnectionManager: Broadcast完了");
            return result;
        }

        private async Task<BroadcastDeliveryResult> SendBroadcastSafelyAsync(
            ChatConnection connection,
            ChatMessage message,
            CancellationToken cancellationToken)
        {
            string peerId = !string.IsNullOrWhiteSpace(connection.BoundRemotePeerId)
                ? connection.BoundRemotePeerId
                : connection.PeerId;
            try
            {
                await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return new BroadcastDeliveryResult { PeerId = peerId, IsSuccess = true };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                SafeLog($"Broadcast message rejected before sending: {ex.Message}");
                return new BroadcastDeliveryResult { PeerId = peerId, ErrorMessage = ex.Message };
            }
            catch (InvalidOperationException ex)
            {
                SafeLog($"Broadcast message is not valid in the current protocol state: {ex.Message}");
                return new BroadcastDeliveryResult { PeerId = peerId, ErrorMessage = ex.Message };
            }
            catch (Exception ex)
            {
                SafeLog($"Broadcast failed for {GetConnectionPeerName(connection)}: {ex.Message}");
                connection.Close();
                return new BroadcastDeliveryResult { PeerId = peerId, ErrorMessage = ex.Message };
            }
        }

        public bool MarkMessageSeen(string messageId)
        {
            if (!Guid.TryParse(messageId, out Guid parsedMessageId)) return false;
            messageId = parsedMessageId.ToString("N");
            lock (_lock)
            {
                if (!_receivedMessageIds.Add(messageId)) return false;
                _receivedMessageIdOrder.Enqueue(messageId);
                while (_receivedMessageIdOrder.Count > MaximumRememberedMessageIds)
                {
                    _receivedMessageIds.Remove(_receivedMessageIdOrder.Dequeue());
                }

                return true;
            }
        }

        private async Task RunKeepAliveAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(PingInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await SendKeepAlivePingAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"Keepalive tick failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task SendKeepAlivePingAsync(CancellationToken cancellationToken)
        {
            List<ChatConnection> targets;
            string senderId;
            string senderName;
            string shortSessionId;
            lock (_lock)
            {
                targets = _connections.Where(IsKeepAliveTarget).ToList();
                senderId = _keepAliveSenderId;
                senderName = _keepAliveSenderName;
                shortSessionId = _keepAliveShortSessionId;
            }

            long now = Stopwatch.GetTimestamp();
            await Task.WhenAll(targets.Select(connection => KeepAliveConnectionAsync(
                connection, senderId, senderName, shortSessionId, now, cancellationToken))).ConfigureAwait(false);
        }

        private async Task KeepAliveConnectionAsync(
            ChatConnection connection,
            string senderId,
            string senderName,
            string shortSessionId,
            long now,
            CancellationToken cancellationToken)
        {
            try
            {
                if (IsPongTimedOut(connection, now))
                {
                    SafeLog($"PONGタイムアウト: Peer={GetConnectionPeerName(connection)}");
                    connection.Close();
                    return;
                }

                if (!connection.IsPingWaiting)
                    await connection.SendPingAsync(senderId, senderName, shortSessionId).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SafeLog($"Keepalive failed for {GetConnectionPeerName(connection)}: {ex.Message}");
                connection.Close();
            }
        }

        private static bool IsKeepAliveTarget(ChatConnection connection) =>
            connection.IsReady && connection.IsHelloVerified && connection.IsConnected && !connection.IsPreparing;

        private static bool IsPongTimedOut(ChatConnection connection, long now)
        {
            long lastPing = connection.LastPingTimestamp;
            long lastPong = connection.LastPongTimestamp;
            return lastPing != 0 && connection.IsPingWaiting &&
                   (lastPong == 0 || lastPong < lastPing) &&
                   Stopwatch.GetElapsedTime(lastPing, now) > PongTimeout;
        }

        private void OnConnectionLogReceived(string message) => SafeLog(message);

        private void OnConnectionMessageReceived(ChatMessage message, ChatConnection connection)
        {
            string type = message.Type?.Trim() ?? "";
            if (!connection.IsHelloVerified && !string.Equals(type, "hello", StringComparison.OrdinalIgnoreCase))
            {
                SafeLog("Application message rejected before HELLO verification.");
                return;
            }

            if (!MarkMessageSeen(message.MessageId))
            {
                SafeLog($"Invalid or duplicate MessageId ignored: {message.MessageId}");
                if (string.Equals(type, "hello", StringComparison.OrdinalIgnoreCase)) connection.Close();
                return;
            }

            NotifyMessageReceived(message, connection);
        }

        private void OnConnectionIdentityVerified(ChatConnection connection)
        {
            if (!connection.IsConnected || !connection.IsHelloVerified ||
                string.IsNullOrWhiteSpace(connection.BoundRemotePeerId))
            {
                return;
            }

            List<ChatConnection> duplicates;
            lock (_lock)
            {
                duplicates = _connections
                    .Where(candidate => candidate != connection && candidate.IsHelloVerified && IsSamePeer(candidate, connection))
                    .ToList();
            }

            foreach (ChatConnection duplicate in duplicates)
            {
                ChatConnection keep = ChoosePreferredConnection(connection, duplicate);
                ChatConnection close = ReferenceEquals(keep, connection) ? duplicate : connection;
                SafeLog($"Duplicate logical peer connection closed: Peer={GetConnectionPeerName(close)}");
                close.Close();
                if (ReferenceEquals(close, connection)) break;
            }
        }

        private static bool IsSamePeer(ChatConnection first, ChatConnection second)
        {
            string firstPeerId = GetRemotePeerId(first);
            string secondPeerId = GetRemotePeerId(second);
            bool firstHasStableId = !string.IsNullOrWhiteSpace(firstPeerId);
            bool secondHasStableId = !string.IsNullOrWhiteSpace(secondPeerId);
            if (firstHasStableId || secondHasStableId)
            {
                return firstHasStableId && secondHasStableId &&
                       string.Equals(firstPeerId, secondPeerId, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(first.ShortSessionId) &&
                   !string.IsNullOrWhiteSpace(second.ShortSessionId) &&
                   string.Equals(first.ShortSessionId, second.ShortSessionId, StringComparison.OrdinalIgnoreCase);
        }

        private static ChatConnection ChoosePreferredConnection(ChatConnection first, ChatConnection second)
        {
            string firstLocalStableId = first.LocalPeerId?.Trim() ?? "";
            string secondLocalStableId = second.LocalPeerId?.Trim() ?? "";
            if (HasConflictingNonEmptyValues(firstLocalStableId, secondLocalStableId))
                return ChooseByInstanceId(first, second);
            string localStableId = !string.IsNullOrWhiteSpace(firstLocalStableId)
                ? firstLocalStableId
                : secondLocalStableId;
            string firstRemoteStableId = GetRemotePeerId(first);
            string secondRemoteStableId = GetRemotePeerId(second);
            string remoteStableId = !string.IsNullOrWhiteSpace(firstRemoteStableId)
                ? firstRemoteStableId
                : secondRemoteStableId;
            if (!string.IsNullOrWhiteSpace(localStableId) && !string.IsNullOrWhiteSpace(remoteStableId))
            {
                bool preferInitiator = string.Compare(localStableId, remoteStableId, StringComparison.OrdinalIgnoreCase) < 0;
                if (first.IsInitiatorConnection == preferInitiator && second.IsInitiatorConnection != preferInitiator) return first;
                if (second.IsInitiatorConnection == preferInitiator && first.IsInitiatorConnection != preferInitiator) return second;
            }

            string firstLocalShortId = first.LocalShortSessionId?.Trim() ?? "";
            string secondLocalShortId = second.LocalShortSessionId?.Trim() ?? "";
            if (HasConflictingNonEmptyValues(firstLocalShortId, secondLocalShortId))
                return ChooseByInstanceId(first, second);
            string localShortId = !string.IsNullOrWhiteSpace(firstLocalShortId)
                ? firstLocalShortId
                : secondLocalShortId;
            string remoteShortId = !string.IsNullOrWhiteSpace(first.ShortSessionId)
                ? first.ShortSessionId
                : second.ShortSessionId;
            if (!string.IsNullOrWhiteSpace(localShortId) && !string.IsNullOrWhiteSpace(remoteShortId))
            {
                bool preferInitiator = string.Compare(localShortId, remoteShortId, StringComparison.OrdinalIgnoreCase) < 0;
                if (first.IsInitiatorConnection == preferInitiator && second.IsInitiatorConnection != preferInitiator) return first;
                if (second.IsInitiatorConnection == preferInitiator && first.IsInitiatorConnection != preferInitiator) return second;
            }

            if (first.IsReady != second.IsReady) return first.IsReady ? first : second;
            if (first.IsConnected != second.IsConnected) return first.IsConnected ? first : second;
            return ChooseByInstanceId(first, second);
        }

        private static bool HasConflictingNonEmptyValues(string first, string second) =>
            first.Length > 0 && second.Length > 0 &&
            !string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        private static ChatConnection ChooseByInstanceId(ChatConnection first, ChatConnection second) =>
            first.InstanceId <= second.InstanceId ? first : second;

        private static bool IsStablePeerId(string? peerId)
        {
            const string prefix = "peer:";
            string candidate = peerId?.Trim() ?? "";
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   Guid.TryParseExact(candidate[prefix.Length..], "N", out _);
        }

        private void OnConnectionDisconnected(ChatConnection connection)
        {
            try
            {
                NotifyConnectionDisconnected(connection);
            }
            finally
            {
                RemoveConnection(connection);
            }
        }

        private static ChatConnection? FindBestConnection(IEnumerable<ChatConnection> connections) =>
            connections.OrderByDescending(connection => connection.IsReady)
                .ThenByDescending(connection => connection.IsConnected)
                .FirstOrDefault();

        private static string GetConnectionPeerName(ChatConnection connection) =>
            !string.IsNullOrWhiteSpace(connection.PeerName) ? connection.PeerName :
            !string.IsNullOrWhiteSpace(connection.RemoteIpAddress) ? connection.RemoteIpAddress : GetRemotePeerId(connection);

        private static string GetRemotePeerId(ChatConnection connection) =>
            !string.IsNullOrWhiteSpace(connection.BoundRemotePeerId)
                ? connection.BoundRemotePeerId.Trim()
                : connection.PeerId?.Trim() ?? "";

        private void SafeLog(string message)
        {
            Action<string>? handlers = LogReceived;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
            {
                try { handler(message); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ChatConnectionManager log handler failed: {ex}");
                }
            }
        }

        private void NotifyConnectionsChanged()
        {
            Action? handlers = ConnectionsChanged;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
            {
                try { handler(); }
                catch (Exception ex)
                {
                    SafeLog($"Connections-changed callback failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void NotifyMessageReceived(ChatMessage message, ChatConnection connection)
        {
            Action<ChatMessage, ChatConnection>? handlers = MessageReceived;
            if (handlers == null) return;
            foreach (Action<ChatMessage, ChatConnection> handler in
                     handlers.GetInvocationList().Cast<Action<ChatMessage, ChatConnection>>())
            {
                try { handler(message, connection); }
                catch (Exception ex)
                {
                    SafeLog($"Message dispatch failed without terminating the transport: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void NotifyConnectionDisconnected(ChatConnection connection)
        {
            Action<ChatConnection>? handlers = ConnectionDisconnected;
            if (handlers == null) return;
            foreach (Action<ChatConnection> handler in handlers.GetInvocationList().Cast<Action<ChatConnection>>())
            {
                try { handler(connection); }
                catch (Exception ex)
                {
                    SafeLog($"Connection-disconnected callback failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
