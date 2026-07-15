using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect;

public sealed class WiFiDirectManager
{
    private const int MaximumPendingIncomingApprovals = 4;
    private const int MaximumSessions = 32;
    private readonly object _lifecycleGate = new();
    private readonly object _operationGate = new();
    private readonly object _sessionsGate = new();
    private readonly object _incomingApprovalGate = new();
    private readonly WiFiDirectListener _listener;
    private readonly WiFiDirectAdvertiser _advertiser;
    private readonly WiFiDirectConnector _connector;
    private readonly WiFiDirectScanner _scanner;
    private readonly Dictionary<string, WiFiDirectSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingIncomingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _lifetimeCts = new();
    private bool _isRunning;
    private long _lastIncomingRequestLog;
    private long _lastIncomingRejectionLog;

    public event Action<string>? LogReceived;
    public event Action<PeerInfo>? ConnectionRequested;
    public event Action<PeerInfo>? PeerFound;
    public event Action<PeerInfo>? PeerRemoved;
    public event Action<PeerInfo>? Connected;
    public event Action<WiFiDirectSession>? SessionConnected;
    public event Action<PeerInfo>? Disconnected;

    // Secure by default: an application must explicitly approve each incoming request.
    public Func<PeerInfo, CancellationToken, Task<bool>>? IncomingConnectionApprovalAsync { get; set; }

    public TimeSpan IncomingConnectionApprovalTimeout { get; set; } = TimeSpan.FromSeconds(20);

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
        _scanner.PeerRemoved += OnScannerPeerRemoved;
    }

    public void Start() => Start("", "");

    public void Start(string displayName, string shortSessionId) =>
        Start(displayName, shortSessionId, autonomousGroupOwner: false);

    public void Start(string displayName, string shortSessionId, bool autonomousGroupOwner) =>
        Start(displayName, shortSessionId, autonomousGroupOwner, peerIdentity: "", tcpPort: 0);

    public bool Start(
        string displayName,
        string shortSessionId,
        bool autonomousGroupOwner,
        string peerIdentity,
        int tcpPort)
    {
        lock (_operationGate)
        {
            lock (_lifecycleGate)
            {
                _isRunning = true;
            }

            OnLogReceived("Manager: Wi-Fi Direct Listener + Advertisement 起動開始");
            if (!_listener.Start())
            {
                OnLogReceived("Manager: Listener開始失敗のため広告を開始しません");
                Stop();
                return false;
            }

            bool started = _advertiser.Start(
                listenerRegistered: true,
                displayName,
                shortSessionId,
                autonomousGroupOwner,
                peerIdentity,
                tcpPort);
            if (!started)
            {
                OnLogReceived("Manager: 広告開始失敗のため全Wi-Fi Direct resourceを停止します");
                Stop();
            }

            return started;
        }
    }

    public void RestartAdvertisement(string displayName, string shortSessionId) =>
        RestartAdvertisement(displayName, shortSessionId, autonomousGroupOwner: false);

    public void RestartAdvertisement(string displayName, string shortSessionId, bool autonomousGroupOwner) =>
        RestartAdvertisement(displayName, shortSessionId, autonomousGroupOwner, peerIdentity: "", tcpPort: 0);

    public bool RestartAdvertisement(
        string displayName,
        string shortSessionId,
        bool autonomousGroupOwner,
        string peerIdentity,
        int tcpPort)
    {
        lock (_operationGate)
        {
            OnLogReceived("Manager: Wi-Fi Direct Advertisement 再起動開始");
            if (!IsRunning())
            {
                OnLogReceived("Managerが停止中のため広告を再起動しません");
                return false;
            }

            if (!_listener.Start())
            {
                OnLogReceived("Manager: Listener開始失敗のため広告を再開しません");
                return false;
            }

            _advertiser.Stop();
            return _advertiser.Start(true, displayName, shortSessionId, autonomousGroupOwner, peerIdentity, tcpPort);
        }
    }

    public void Stop()
    {
        lock (_operationGate)
        {
            OnLogReceived("Manager: Wi-Fi Direct 停止開始");
            CancellationTokenSource previousLifetime = DeactivateLifetime();
            CancelAndDisposeLifetime(previousLifetime, "Manager停止");
            _connector.CancelPendingOperations();
            _listener.Stop();
            _scanner.Stop();
            _advertiser.Stop();

            WiFiDirectSession[] sessions;
            lock (_sessionsGate)
            {
                sessions = _sessions.Values.Distinct().ToArray();
                _sessions.Clear();
                foreach (WiFiDirectSession session in sessions)
                {
                    session.Disconnected -= OnSessionDisconnected;
                }
            }

            foreach (WiFiDirectSession session in sessions)
            {
                DisposeSessionSafely(session, "Manager停止");
            }

            OnLogReceived("Manager: Wi-Fi Direct 停止完了");
        }
    }

    public void StopAdvertisement()
    {
        lock (_operationGate)
        {
            OnLogReceived("Manager: Wi-Fi Direct Advertisement stop requested");
            _advertiser.Stop();
        }
    }

    public Task StartScanAsync() => StartAssociationEndpointScanAsync();

    public Task StartDefaultScanAsync()
    {
        lock (_operationGate)
        {
            return IsRunning() ? _scanner.StartDefaultAsync() : Task.CompletedTask;
        }
    }

    public Task StartAssociationEndpointScanAsync()
    {
        lock (_operationGate)
        {
            return IsRunning() ? _scanner.StartAssociationEndpointAsync() : Task.CompletedTask;
        }
    }

    public void StopScan()
    {
        lock (_operationGate)
        {
            _scanner.Stop();
        }
    }

    public async Task<bool> ConnectAsync(PeerInfo peer)
    {
        if (!TryGetLifetimeToken(out CancellationToken lifetimeToken))
        {
            OnLogReceived("Managerが停止中のためWi-Fi Direct接続を開始しません");
            return false;
        }

        bool connected = await _connector.ConnectAsync(peer, lifetimeToken).ConfigureAwait(false);
        return connected && !lifetimeToken.IsCancellationRequested && IsRunning();
    }

    private void OnLogReceived(string message)
    {
        if (IsInternalGateHeldByCurrentThread())
        {
            ThreadPool.QueueUserWorkItem(_ => DispatchLog(message));
            return;
        }

        DispatchLog(message);
    }

    private void DispatchLog(string message)
    {
        Action<string>? handlers = LogReceived;
        if (handlers == null) return;
        foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(message); }
            catch (Exception ex)
            {
                Debug.WriteLine($"WiFiDirectManager log handler failed: {ex}");
            }
        }
    }

    private void OnListenerConnectionRequested(PeerInfo peer)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_incomingApprovalGate)
        {
            if (_lastIncomingRequestLog != 0 &&
                Stopwatch.GetElapsedTime(_lastIncomingRequestLog, now) < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastIncomingRequestLog = now;
        }

        OnLogReceived($"Manager: 接続要求を受信しました {peer.DisplayName}。明示的な承認完了までacceptしません");
    }

    private async void OnIncomingConnectionRequested(
        PeerInfo peer,
        WiFiDirectConnectionRequest request)
    {
        string requestKey = "";
        bool approvalRegistered = false;
        try
        {
            string requestDeviceId = request.DeviceInformation.Id?.Trim() ?? "";
            if (requestDeviceId.Length == 0 || requestDeviceId.Length > 4096 ||
                requestDeviceId.Any(character => char.IsControl(character)) ||
                !string.Equals(requestDeviceId, peer.DeviceId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                LogIncomingRejection("Incoming Wi-Fi Direct request had no trustworthy device identity and was rejected.");
                return;
            }
            requestKey = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(requestDeviceId.ToUpperInvariant())));

            if (!TryGetLifetimeToken(out CancellationToken lifetimeToken))
            {
                LogIncomingRejection("Incoming Wi-Fi Direct request arrived after the manager stopped and was rejected.");
                return;
            }

            string? queueRejection = null;
            lock (_incomingApprovalGate)
            {
                if (_pendingIncomingApprovals.Count >= MaximumPendingIncomingApprovals)
                {
                    queueRejection = "Incoming Wi-Fi Direct approval queue is full; request rejected.";
                }
                else if (!_pendingIncomingApprovals.Add(requestKey))
                {
                    queueRejection = "A Wi-Fi Direct approval for this device is already pending.";
                }
                else
                {
                    approvalRegistered = true;
                }
            }
            if (queueRejection != null)
            {
                LogIncomingRejection(queueRejection);
                return;
            }

            Func<PeerInfo, CancellationToken, Task<bool>>? approval = IncomingConnectionApprovalAsync;
            if (approval == null)
            {
                OnLogReceived("受信接続を拒否: 承認handlerが設定されていません");
                return;
            }

            using var approvalCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            approvalCancellation.CancelAfter(IncomingConnectionApprovalTimeout);

            bool approved;
            try
            {
                approved = await approval(peer, approvalCancellation.Token)
                    .WaitAsync(approvalCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                OnLogReceived("受信接続を拒否: 承認がキャンセルまたはタイムアウトしました");
                return;
            }

            if (!approved)
            {
                OnLogReceived($"受信接続を拒否しました: {peer.DisplayName}");
                return;
            }

            lifetimeToken.ThrowIfCancellationRequested();
            InvokeSafely(ConnectionRequested, peer, nameof(ConnectionRequested));
            await _connector.AcceptIncomingConnectionAsync(peer, request, lifetimeToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnLogReceived($"受信接続処理失敗: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (approvalRegistered)
            {
                lock (_incomingApprovalGate) _pendingIncomingApprovals.Remove(requestKey);
            }
            try
            {
                request.Dispose();
                OnLogReceived("Wi-Fi Direct接続要求Requestを破棄しました");
            }
            catch (Exception ex)
            {
                OnLogReceived($"Wi-Fi Direct接続要求Requestの破棄に失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private bool OnConnectorConnected(WiFiDirectSession session)
    {
        WiFiDirectSession? replaced = null;
        string key = GetSessionKey(session);
        bool rejectedForLimit = false;
        lock (_sessionsGate)
        {
            if (!IsRunning())
            {
                return false;
            }

            if (!_sessions.ContainsKey(key) && _sessions.Count >= MaximumSessions)
            {
                rejectedForLimit = true;
            }
            else
            {
                if (_sessions.Remove(key, out replaced))
                {
                    replaced.Disconnected -= OnSessionDisconnected;
                }

                _sessions[key] = session;
                session.Disconnected += OnSessionDisconnected;
            }
        }

        if (rejectedForLimit)
        {
            OnLogReceived("Wi-Fi Direct session limit reached; new session rejected.");
            return false;
        }

        if (replaced != null)
        {
            DisposeSessionSafely(replaced, "重複session置換");
        }
        OnLogReceived($"Manager: 接続完了 {session.Peer.DisplayName}, Direction={session.Direction}");
        InvokeSafely(SessionConnected, session, nameof(SessionConnected));
        InvokeSafely(Connected, session.Peer, nameof(Connected));
        return true;
    }

    private void OnSessionDisconnected(WiFiDirectSession session)
    {
        bool removed = false;
        lock (_sessionsGate)
        {
            KeyValuePair<string, WiFiDirectSession> entry = _sessions
                .FirstOrDefault(item => ReferenceEquals(item.Value, session));
            if (entry.Value != null)
            {
                _sessions.Remove(entry.Key);
                session.Disconnected -= OnSessionDisconnected;
                removed = true;
            }
        }

        if (!removed)
        {
            return;
        }

        DisposeSessionSafely(session, "切断session");
        OnLogReceived($"Manager: Wi-Fi Direct切断 {session.Peer.DisplayName}");
        InvokeSafely(Disconnected, session.Peer, nameof(Disconnected));
    }

    private void OnScannerPeerFound(PeerInfo peer) => InvokeSafely(PeerFound, peer, nameof(PeerFound));

    private void OnScannerPeerRemoved(PeerInfo peer) => InvokeSafely(PeerRemoved, peer, nameof(PeerRemoved));

    private bool TryGetLifetimeToken(out CancellationToken token)
    {
        lock (_lifecycleGate)
        {
            if (!_isRunning)
            {
                token = new CancellationToken(canceled: true);
                return false;
            }

            token = _lifetimeCts.Token;
            return true;
        }
    }

    private CancellationTokenSource DeactivateLifetime()
    {
        lock (_lifecycleGate)
        {
            _isRunning = false;
            CancellationTokenSource previous = _lifetimeCts;
            _lifetimeCts = new CancellationTokenSource();
            return previous;
        }
    }

    private void CancelAndDisposeLifetime(CancellationTokenSource lifetime, string context)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (Exception ex)
        {
            OnLogReceived($"{context}のキャンセル通知に失敗: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private void DisposeSessionSafely(WiFiDirectSession session, string context)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            OnLogReceived($"{context}でWi-Fi Direct sessionの破棄に失敗: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LogIncomingRejection(string message)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_incomingApprovalGate)
        {
            if (_lastIncomingRejectionLog != 0 &&
                Stopwatch.GetElapsedTime(_lastIncomingRejectionLog, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }
            _lastIncomingRejectionLog = now;
        }
        OnLogReceived(message);
    }

    private bool IsRunning()
    {
        lock (_lifecycleGate)
        {
            return _isRunning;
        }
    }

    private static string GetSessionKey(WiFiDirectSession session)
    {
        PeerInfo peer = session.Peer;
        if (TryNormalizeStablePeerId(peer.PeerId, out string stablePeerId))
        {
            return stablePeerId;
        }

        if (TryNormalizeDiscoveryIdentity(peer.MatchKey, out string discoveryIdentity))
        {
            return $"identity:{discoveryIdentity}";
        }

        if (IPAddress.TryParse(session.RemoteIpAddress, out IPAddress? remoteAddress))
        {
            return $"ip:{remoteAddress}";
        }

        if (!string.IsNullOrWhiteSpace(peer.DeviceId))
        {
            return $"device:{peer.DeviceId.Trim()}";
        }

        return $"peer:{peer.DisplayName}";
    }

    private static bool TryNormalizeStablePeerId(string? peerId, out string normalized)
    {
        string value = peerId?.Trim() ?? "";
        const string prefix = "peer:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(value[prefix.Length..], "N", out Guid parsed))
        {
            normalized = "";
            return false;
        }

        normalized = prefix + parsed.ToString("N");
        return true;
    }

    private static bool TryNormalizeDiscoveryIdentity(string? identity, out string normalized)
    {
        normalized = (identity ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (normalized.Length != 24 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    private void InvokeSafely<T>(Action<T>? handlers, T value, string eventName)
    {
        if (handlers == null)
        {
            return;
        }

        if (IsInternalGateHeldByCurrentThread())
        {
            ThreadPool.QueueUserWorkItem(_ => InvokeSafely(handlers, value, eventName));
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                OnLogReceived($"{eventName} handler失敗: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private bool IsInternalGateHeldByCurrentThread() =>
        Monitor.IsEntered(_operationGate) ||
        Monitor.IsEntered(_lifecycleGate) ||
        Monitor.IsEntered(_sessionsGate) ||
        Monitor.IsEntered(_incomingApprovalGate);
}
