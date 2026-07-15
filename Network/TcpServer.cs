using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;

namespace direct_module.Network
{
    public sealed class TcpServer
    {
        private const int MaximumAcceptedConnectionsPerMinute = 120;
        private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RejectionLogInterval = TimeSpan.FromSeconds(5);
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _startGate = new(1, 1);
        private readonly Queue<long> _recentAcceptedConnections = new();
        private StreamSocketListener? _listener;
        private StreamSocketListener? _pendingListener;
        private CancellationTokenSource? _startCancellation;
        private int _generation;
        private long _lastRejectionLog;

        public event Action<string>? LogReceived;
        public event Action<StreamSocket>? ConnectionAccepted;

        public bool IsStarted
        {
            get { lock (_stateLock) return _listener != null; }
        }

        public Task StartAsync(int port) => StartAsync(port, CancellationToken.None);

        public async Task RestartAsync(int port, CancellationToken cancellationToken)
        {
            // A Wi-Fi Direct session creates (or replaces) a virtual network
            // adapter after discovery has already started. Rebind so the listener
            // is registered on the newly available interface as well.
            Stop();
            await StartAsync(port, cancellationToken).ConfigureAwait(false);
        }

        public async Task StartAsync(int port, CancellationToken cancellationToken)
        {
            if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            int requestGeneration;
            lock (_stateLock) requestGeneration = _generation;

            await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            StreamSocketListener? localListener = null;
            CancellationTokenSource? localCancellation = null;
            try
            {
                bool alreadyStarted;
                lock (_stateLock)
                {
                    if (requestGeneration != _generation) return;
                    alreadyStarted = _listener != null;
                    if (!alreadyStarted)
                    {
                        localListener = new StreamSocketListener();
                        localListener.ConnectionReceived += OnConnectionReceived;
                        localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        localCancellation.CancelAfter(BindTimeout);
                        _pendingListener = localListener;
                        _startCancellation = localCancellation;
                    }
                }

                if (alreadyStarted)
                {
                    SafeLog("TCP server is already started.");
                    return;
                }

                await localListener!.BindServiceNameAsync(port.ToString(CultureInfo.InvariantCulture))
                    .AsTask(localCancellation!.Token)
                    .ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (requestGeneration != _generation || localCancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(localCancellation.Token);
                    }
                    _listener = localListener;
                    _pendingListener = null;
                    _startCancellation = null;
                    localListener = null;
                }

                SafeLog($"TCP server started on port {port}.");
            }
            catch (OperationCanceledException ex) when (localCancellation?.IsCancellationRequested == true)
            {
                SafeLog("TCP server start was canceled or timed out.");
                lock (_stateLock)
                {
                    if (requestGeneration != _generation) return;
                }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Timed out while binding the TCP chat listener.", ex);
            }
            catch (Exception ex)
            {
                SafeLog($"TCP server start failed: {ex.GetType().Name}: {ex.Message}");
                lock (_stateLock)
                {
                    if (requestGeneration != _generation) return;
                }
                throw;
            }
            finally
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pendingListener, localListener)) _pendingListener = null;
                    if (ReferenceEquals(_startCancellation, localCancellation)) _startCancellation = null;
                }

                DisposeListener(localListener);
                localCancellation?.Dispose();
                _startGate.Release();
            }
        }

        public void Stop()
        {
            StreamSocketListener? listener;
            StreamSocketListener? pending;
            CancellationTokenSource? cancellation;
            lock (_stateLock)
            {
                _generation++;
                listener = _listener;
                pending = _pendingListener;
                cancellation = _startCancellation;
                _listener = null;
                _pendingListener = null;
                _startCancellation = null;
                _recentAcceptedConnections.Clear();
                _lastRejectionLog = 0;
            }

            try { cancellation?.Cancel(); }
            catch (Exception ex)
            {
                SafeLog($"TCP server cancellation failed: {ex.GetType().Name}: {ex.Message}");
            }
            DisposeListener(listener);
            if (!ReferenceEquals(listener, pending)) DisposeListener(pending);
            // StartAsync owns and disposes the cancellation source after the bind observes cancellation.
            SafeLog("TCP server stopped.");
        }

        private void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StreamSocket socket = args.Socket;
            bool accept;
            bool logRejection = false;
            lock (_stateLock)
            {
                accept = ReferenceEquals(sender, _listener) &&
                         IsWithinAcceptanceRateLocked(Stopwatch.GetTimestamp());
                if (!accept) logRejection = ShouldLogRejectionLocked(Stopwatch.GetTimestamp());
            }

            if (!accept)
            {
                DisposeSocket(socket);
                if (logRejection)
                {
                    SafeLog("An incoming TCP connection was rejected because the server is stopped or rate limited.");
                }
                return;
            }

            Action<StreamSocket>? handler = ConnectionAccepted;
            if (handler == null || handler.GetInvocationList().Length != 1)
            {
                DisposeSocket(socket);
                SafeLog("An incoming TCP socket was discarded because it did not have exactly one owner.");
                return;
            }

            try
            {
                handler(socket);
            }
            catch (Exception ex)
            {
                DisposeSocket(socket);
                SafeLog($"TCP connection handoff failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool IsWithinAcceptanceRateLocked(long now)
        {
            while (_recentAcceptedConnections.Count > 0 &&
                   Stopwatch.GetElapsedTime(_recentAcceptedConnections.Peek(), now) >= TimeSpan.FromMinutes(1))
            {
                _recentAcceptedConnections.Dequeue();
            }
            if (_recentAcceptedConnections.Count >= MaximumAcceptedConnectionsPerMinute) return false;
            _recentAcceptedConnections.Enqueue(now);
            return true;
        }

        private bool ShouldLogRejectionLocked(long now)
        {
            if (_lastRejectionLog != 0 &&
                Stopwatch.GetElapsedTime(_lastRejectionLog, now) < RejectionLogInterval)
            {
                return false;
            }
            _lastRejectionLog = now;
            return true;
        }

        private void DisposeListener(StreamSocketListener? listener)
        {
            if (listener == null) return;
            try { listener.ConnectionReceived -= OnConnectionReceived; }
            catch { }
            try { listener.Dispose(); }
            catch { }
        }

        private static void DisposeSocket(StreamSocket socket)
        {
            try { socket.Dispose(); }
            catch { }
        }

        private void SafeLog(string message)
        {
            Action<string>? handlers = LogReceived;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
            {
                try { handler(message); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"TcpServer log handler failed: {ex}");
                }
            }
        }
    }
}
