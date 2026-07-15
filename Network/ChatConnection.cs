using direct_module.Crypto;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public enum ChatConnectionDirection
    {
        Unknown,
        Inbound,
        Outbound
    }

    /// <summary>
    /// Owns one framed chat transport.  All reads, writes and handshake operations are
    /// bounded and are cancelled when the connection is closed.
    /// </summary>
    public sealed class ChatConnection
    {
        public const uint MaxMessageBytes = 1024 * 1024;
        private const int MaxHandshakeFrameLength = 4096;
        private const int AesGcmOverheadBytes = AesGcmMessageCrypto.OverheadSizeBytes;
        private const int DefaultMaximumQueuedMessages = 256;
        private const long DefaultMaximumQueuedBytes = 8L * 1024 * 1024;
        private const int DefaultMaximumMessagesPerMinute = 6000;
        private const long DefaultMaximumBytesPerMinute = 256L * 1024 * 1024;
        public const int MaximumRelayHopCount = ChatMessageValidator.MaximumRelayHopCount;
        private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultHelloVerificationTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultIdentityApprovalTimeout = TimeSpan.FromMinutes(2);
        private static readonly byte[] HandshakeMagic = Encoding.ASCII.GetBytes("NOVA-ECDH-3|");
        private static readonly JsonSerializerOptions MessageJsonOptions = new()
        {
            MaxDepth = 8,
            PropertyNameCaseInsensitive = false
        };

        private readonly bool _useEcdhHandshake;
        private static long _nextInstanceId;
        private readonly object _stateLock = new();
        private readonly object _cryptoLock = new();
        private readonly object _sendQueueLock = new();
        private readonly SemaphoreSlim _connectionAttemptGate = new(1, 1);
        private readonly Queue<PendingSend> _highPrioritySends = new();
        private readonly Queue<PendingSend> _normalPrioritySends = new();
        private readonly ChatMessageRateLimiter _messageTypeRateLimiter = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private IMessageCrypto _messageCrypto;
        private bool _ownsMessageCrypto;
        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;
        private CancellationTokenSource? _connectionCancellation;
        private CancellationTokenSource? _pendingConnectCancellation;
        private StreamSocket? _pendingConnectSocket;
        private Task? _sendLoopTask;
        private Task? _receiveLoopTask;
        private TaskCompletionSource<bool> _helloVerified = NewHelloCompletion();
        private volatile bool _isConnected;
        private volatile bool _isReceiveLoopStarted;
        private volatile bool _isSendLoopStarted;
        private volatile bool _isHelloVerified;
        private volatile bool _isReady;
        private bool _disconnectNotified;
        private volatile bool _acceptingSends;
        private volatile bool _permanentlyRejected;
        private int _connectionGeneration;
        private int _closedGeneration;
        private int _operationEpoch;
        private int _connectionAttemptCount;
        private int _queuedMessageCount;
        private long _queuedBytes;
        private int _consecutiveHighPrioritySends;
        private long _receiveWindowStartedAt = Stopwatch.GetTimestamp();
        private int _receiveWindowMessageCount;
        private long _receiveWindowBytes;
        private string _boundPeerId = "";
        private string _boundShortSessionId = "";
        private bool _remoteIdentityStaged;
        private bool _helloReceived;
        private string _receivedHelloMessageId = "";
        private string _receivedHelloPeerId = "";
        private string _receivedHelloSenderName = "";
        private string _receivedHelloShortSessionId = "";
        private bool _localHelloEnqueued;
        private bool _localHelloSent;
        private int _localHelloGeneration = -1;
        private volatile bool _isPingWaiting;
        private long _lastPingTimestamp;
        private long _lastPongTimestamp;

        public ChatConnection()
            : this(new NoOpMessageCrypto(), useEcdhHandshake: true, ownsMessageCrypto: true)
        {
        }

        internal ChatConnection(IMessageCrypto messageCrypto)
            : this(messageCrypto, useEcdhHandshake: false, ownsMessageCrypto: false)
        {
        }

        private ChatConnection(IMessageCrypto messageCrypto, bool useEcdhHandshake, bool ownsMessageCrypto)
        {
            _messageCrypto = messageCrypto ?? throw new ArgumentNullException(nameof(messageCrypto));
            _useEcdhHandshake = useEcdhHandshake;
            _ownsMessageCrypto = ownsMessageCrypto;
            InstanceId = Interlocked.Increment(ref _nextInstanceId);
        }

        internal long InstanceId { get; }

        public string PeerId { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string RemoteIpAddress { get; set; } = "";
        public string ShortSessionId { get; set; } = "";
        public bool IsPreparing { get; set; }
        public DateTime? LastPingAt { get; set; }
        public DateTime? LastPongAt { get; set; }
        public DateTime? LastResponseAt { get; set; }
        public bool IsPingWaiting
        {
            get => _isPingWaiting;
            set => _isPingWaiting = value;
        }

        /// <summary>
        /// Setting this after validating HELLO also binds subsequent message identity to
        /// the current PeerId and ShortSessionId.
        /// </summary>
        public bool IsHelloVerified
        {
            get => _isHelloVerified;
            private set
            {
                string? rejection = null;
                bool notifyIdentityVerified = false;
                lock (_stateLock)
                {
                    if (_isHelloVerified == value)
                    {
                        return;
                    }
                    if (value && (!_isConnected || string.IsNullOrWhiteSpace(PeerId) || string.IsNullOrWhiteSpace(ShortSessionId)))
                    {
                        rejection = "HELLO identity cannot be verified on a closed connection or without PeerId and ShortSessionId.";
                        _isHelloVerified = false;
                        _isReady = false;
                    }
                    else if (value && !_remoteIdentityStaged)
                    {
                        rejection = "HELLO identity must be staged before verification is completed.";
                        _isHelloVerified = false;
                        _isReady = false;
                    }
                    else
                    {
                        _isHelloVerified = value;
                        if (value)
                        {
                            _boundPeerId = PeerId.Trim();
                            _boundShortSessionId = ShortSessionId.Trim();
                            _helloVerified.TrySetResult(true);
                            notifyIdentityVerified = true;
                        }
                        else
                        {
                            _isReady = false;
                        }
                    }
                }

                if (rejection != null) SafeLog(rejection);
                if (notifyIdentityVerified) InvokeConnectionHandlersSafely(IdentityVerified, nameof(IdentityVerified));
            }
        }

        public bool IsReady
        {
            get => _isReady;
            set => _isReady = value && _isHelloVerified && _isConnected;
        }

        public bool IsConnected => _isConnected;
        public CancellationToken LifetimeToken
        {
            get
            {
                lock (_stateLock)
                    return _connectionCancellation?.Token ?? new CancellationToken(canceled: true);
            }
        }
        public bool IsReceiveLoopStarted => _isReceiveLoopStarted;
        public bool IsInitiatorConnection { get; private set; }
        public ChatConnectionDirection Direction { get; private set; }
        public bool IsInbound => Direction == ChatConnectionDirection.Inbound;
        public bool IsOutbound => Direction == ChatConnectionDirection.Outbound;
        public bool ShouldRelayGroupMessages => IsInbound;
        public bool IsCryptographicIdentityVerified { get; private set; }
        public string RemotePublicKeyFingerprint { get; private set; } = "";
        public string RemoteIdentityFingerprint { get; private set; } = "";
        public string ExpectedRemotePublicKeyFingerprint { get; private set; } = "";
        public string ExpectedRemoteIdentityFingerprint { get; private set; } = "";
        public bool RequireAuthenticatedRemoteIdentity
        {
            get => true;
            set
            {
                if (!value)
                    throw new InvalidOperationException("Authenticated remote identity verification cannot be disabled.");
            }
        }
        public EcdsaChatIdentity? LocalHandshakeIdentity { get; set; }
        public string ExpectedRemotePeerId { get; private set; } = "";
        public string ExpectedRemoteShortSessionId { get; private set; } = "";
        public string BoundRemotePeerId { get { lock (_stateLock) return _boundPeerId; } }
        public string BoundRemoteShortSessionId { get { lock (_stateLock) return _boundShortSessionId; } }
        public string LocalPeerId { get; private set; } = "";
        public string LocalPeerName { get; private set; } = "";
        public string LocalShortSessionId { get; private set; } = "";

        /// <summary>
        /// Optional application trust callback. Returning false aborts the handshake.
        /// Configure this, or ExpectedRemotePublicKeyFingerprint, to authenticate ECDH.
        /// </summary>
        public Func<ChatConnection, string, bool>? RemotePublicKeyVerifier { get; set; }

        /// <summary>
        /// Authorizes the stable ECDSA identity fingerprint after its signature over
        /// the ephemeral ECDH key has been verified.
        /// </summary>
        public Func<ChatConnection, string, bool>? RemoteIdentityVerifier { get; set; }

        public TimeSpan ConnectTimeout { get; set; } = DefaultConnectTimeout;
        public TimeSpan HandshakeTimeout { get; set; } = DefaultHandshakeTimeout;
        public TimeSpan ReadTimeout { get; set; } = DefaultReadTimeout;
        public TimeSpan WriteTimeout { get; set; } = DefaultWriteTimeout;
        public TimeSpan HelloVerificationTimeout { get; set; } = DefaultHelloVerificationTimeout;
        public TimeSpan IdentityApprovalTimeout { get; set; } = DefaultIdentityApprovalTimeout;
        public int MaximumQueuedMessages { get; set; } = DefaultMaximumQueuedMessages;
        public long MaximumQueuedBytes { get; set; } = DefaultMaximumQueuedBytes;
        public int MaximumMessagesPerMinute { get; set; } = DefaultMaximumMessagesPerMinute;
        public long MaximumBytesPerMinute { get; set; } = DefaultMaximumBytesPerMinute;

        public event Action<ChatMessage, ChatConnection>? MessageReceived;
        public event Action<string>? LogReceived;
        public event Action<ChatConnection>? Disconnected;
        public event Action<ChatConnection>? IdentityVerified;
        public event Action? Closed;

        /// <summary>
        /// Pins the discovery identity expected in the remote HELLO. A non-empty
        /// fingerprint additionally authenticates the ephemeral ECDH exchange.
        /// Call before ConnectAsync/AttachAcceptedSocketAsync.
        /// </summary>
        public void SetExpectedRemoteIdentity(string peerId, string shortSessionId, string publicKeyFingerprint = "")
        {
            if (!string.IsNullOrWhiteSpace(peerId))
                ChatMessageValidator.ValidateStablePeerId(peerId, nameof(peerId));
            if (!string.IsNullOrWhiteSpace(shortSessionId))
                ChatMessageValidator.ValidateShortSessionId(shortSessionId, nameof(shortSessionId));
            if (!string.IsNullOrWhiteSpace(publicKeyFingerprint) && !IsSha256Fingerprint(publicKeyFingerprint))
                throw new ArgumentException("A SHA-256 public-key fingerprint is required.", nameof(publicKeyFingerprint));
            lock (_stateLock)
            {
                if (_isConnected || _connectionAttemptCount != 0)
                    throw new InvalidOperationException("Expected identity must be configured before connecting.");
                ExpectedRemotePeerId = peerId?.Trim() ?? "";
                ExpectedRemoteShortSessionId = shortSessionId?.Trim() ?? "";
                ExpectedRemotePublicKeyFingerprint = publicKeyFingerprint?.Trim() ?? "";
            }
        }

        public void SetExpectedRemoteCryptographicIdentity(string stableIdentityFingerprint)
        {
            if (!IsSha256Fingerprint(stableIdentityFingerprint))
                throw new ArgumentException("A SHA-256 stable-identity fingerprint is required.", nameof(stableIdentityFingerprint));
            lock (_stateLock)
            {
                if (_isConnected || _connectionAttemptCount != 0)
                    throw new InvalidOperationException("Expected identity must be configured before connecting.");
                ExpectedRemoteIdentityFingerprint = stableIdentityFingerprint?.Trim() ?? "";
                RequireAuthenticatedRemoteIdentity = true;
            }
        }

        /// <summary>
        /// Atomically validates and binds the identity supplied by HELLO. The caller
        /// remains responsible for deciding when the connection is application-ready.
        /// </summary>
        public bool TryBindVerifiedRemoteIdentity(ChatMessage hello, out string error)
        {
            if (!TryStageRemoteIdentity(hello, out error)) return false;
            if (!CompleteHelloVerification())
            {
                error = "The staged HELLO identity could not be completed.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates and stages HELLO identity fields without opening the application
        /// message gate. This permits an asynchronous user/TOFU decision to occur safely.
        /// Call CompleteHelloVerification only after that decision succeeds.
        /// </summary>
        public bool TryStageRemoteIdentity(ChatMessage hello, out string error)
        {
            error = "";
            if (hello == null || !IsMessageType(hello, "hello"))
            {
                error = "A HELLO message is required.";
                return false;
            }

            try { ValidateHello(hello); }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
                return false;
            }

            if ((!string.IsNullOrWhiteSpace(ExpectedRemotePeerId) &&
                 !string.Equals(ExpectedRemotePeerId, hello.SenderId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(ExpectedRemoteShortSessionId) &&
                 !string.Equals(ExpectedRemoteShortSessionId, hello.ShortSessionId, StringComparison.OrdinalIgnoreCase)))
            {
                error = "HELLO identity did not match the expected discovery identity.";
                return false;
            }

            lock (_stateLock)
            {
                if (!_isConnected || !_helloReceived)
                {
                    error = "The HELLO identity was not received on the active connection.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(LocalPeerId) &&
                    string.Equals(LocalPeerId, hello.SenderId, StringComparison.OrdinalIgnoreCase))
                {
                    error = "A remote HELLO cannot claim the local stable peer identity.";
                    return false;
                }
                if (!string.Equals(_receivedHelloMessageId, hello.MessageId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_receivedHelloPeerId, hello.SenderId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_receivedHelloSenderName, hello.SenderName, StringComparison.Ordinal) ||
                    !string.Equals(_receivedHelloShortSessionId, hello.ShortSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The HELLO identity does not match the frame received on this connection.";
                    return false;
                }
                if (_remoteIdentityStaged)
                {
                    bool sameIdentity =
                        string.Equals(_boundPeerId, hello.SenderId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(_boundShortSessionId, hello.ShortSessionId, StringComparison.OrdinalIgnoreCase);
                    if (!sameIdentity)
                        error = "A different HELLO identity was already staged on this connection.";
                    return sameIdentity;
                }

                PeerId = hello.SenderId.Trim();
                ShortSessionId = hello.ShortSessionId.Trim();
                if (!string.IsNullOrWhiteSpace(hello.SenderName)) PeerName = hello.SenderName.Trim();
                _boundPeerId = PeerId;
                _boundShortSessionId = ShortSessionId;
                _remoteIdentityStaged = true;
            }

            return true;
        }

        public bool CompleteHelloVerification()
        {
            lock (_stateLock)
            {
                if (!_isConnected || !_remoteIdentityStaged ||
                    string.IsNullOrWhiteSpace(_boundPeerId) ||
                    string.IsNullOrWhiteSpace(_boundShortSessionId))
                {
                    return false;
                }
            }

            IsHelloVerified = true;
            return _isHelloVerified;
        }

        public bool IsInboundMessageIdentityValid(ChatMessage message, out string error)
        {
            error = "";
            if (message == null)
            {
                error = "A message is required.";
                return false;
            }

            try
            {
                if (IsMessageType(message, "hello"))
                {
                    ValidateHello(message);
                    if (_isHelloVerified) ValidateBoundHello(message);
                }
                else
                {
                    if (!_isHelloVerified) throw new InvalidDataException("HELLO has not been verified.");
                    ValidateBoundIdentity(message);
                }

                return true;
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public Task ConnectAsync(string ipAddress, int port) =>
            ConnectCoreAsync(ipAddress, port, CancellationToken.None, throwOnFailure: false);

        public Task ConnectAsync(string ipAddress, int port, CancellationToken cancellationToken) =>
            ConnectCoreAsync(ipAddress, port, cancellationToken, throwOnFailure: true);

        private async Task ConnectCoreAsync(
            string ipAddress,
            int port,
            CancellationToken cancellationToken,
            bool throwOnFailure)
        {
            int requestEpoch;
            bool gateEntered = false;
            bool delegated = false;
            lock (_stateLock)
            {
                requestEpoch = _operationEpoch;
                _connectionAttemptCount++;
            }

            try
            {
                await _connectionAttemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateEntered = true;
                lock (_stateLock)
                {
                    if (requestEpoch != _operationEpoch)
                        throw new OperationCanceledException("The connection attempt was canceled before it started.");
                }

                delegated = true;
                await ConnectCoreSerializedAsync(
                    ipAddress,
                    port,
                    cancellationToken,
                    throwOnFailure,
                    requestEpoch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!delegated)
                {
                    LogException(ex);
                    MarkAttemptFailureIfTerminal();
                }
                if (throwOnFailure) throw;
            }
            finally
            {
                lock (_stateLock) _connectionAttemptCount--;
                if (gateEntered) _connectionAttemptGate.Release();
            }
        }

        private async Task ConnectCoreSerializedAsync(
            string ipAddress,
            int port,
            CancellationToken cancellationToken,
            bool throwOnFailure,
            int requestEpoch)
        {
            if (_isConnected)
            {
                SafeLog("Chat TCP接続済みなので再利用");
                return;
            }

            if (string.IsNullOrWhiteSpace(ipAddress) || port is <= 0 or > 65535)
            {
                var exception = new ArgumentException("A valid IP address and TCP port are required.");
                LogException(exception);
                MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw exception;
                return;
            }

            HostName remoteHost;
            try
            {
                remoteHost = new HostName(ipAddress.Trim());
                if (remoteHost.Type is not (HostNameType.Ipv4 or HostNameType.Ipv6))
                    throw new ArgumentException("A literal IPv4 or IPv6 address is required.", nameof(ipAddress));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                LogException(ex);
                MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw;
                return;
            }

            if (_permanentlyRejected)
            {
                var exception = new InvalidOperationException("This connection was rejected by the connection manager.");
                if (throwOnFailure) throw exception;
                return;
            }
            if (_useEcdhHandshake && LocalHandshakeIdentity == null)
            {
                var exception = new InvalidOperationException("A stable local handshake identity is required.");
                LogException(exception);
                MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw exception;
                return;
            }

            Close(CurrentGeneration);
            await WaitForTransportLoopsAsync(cancellationToken).ConfigureAwait(false);
            StreamSocket? socket = new();
            int generation = -1;

            try
            {
                using CancellationTokenSource operation = CreateTimeoutCancellation(cancellationToken, ConnectTimeout);
                lock (_stateLock)
                {
                    if (requestEpoch != _operationEpoch)
                        throw new OperationCanceledException("The connection attempt was canceled.");
                    _pendingConnectSocket = socket;
                    _pendingConnectCancellation = operation;
                }

                await socket.ConnectAsync(remoteHost, port.ToString()).AsTask(operation.Token);
                cancellationToken.ThrowIfCancellationRequested();

                lock (_stateLock)
                {
                    if (requestEpoch != _operationEpoch ||
                        !ReferenceEquals(_pendingConnectSocket, socket) ||
                        !ReferenceEquals(_pendingConnectCancellation, operation) ||
                        operation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The connection attempt was canceled.");
                    }

                    _pendingConnectSocket = null;
                    _pendingConnectCancellation = null;
                    RemoteIpAddress = remoteHost.DisplayName;
                    generation = AttachSocketLocked(socket, isInitiator: true);
                    socket = null;
                }
                await InitializeCryptoAsync(generation, isInitiator: true, cancellationToken);
                if (!IsCurrentConnectedGeneration(generation))
                    throw new OperationCanceledException("The connection was closed during its handshake.");
                EnableSends(generation);
                StartSendLoop(generation);
                StartReceiveLoop();
                SafeLog("Chat TCP接続成功");
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_pendingConnectSocket, socket)) _pendingConnectSocket = null;
                    _pendingConnectCancellation = null;
                }
                SafeDispose(socket);
                LogException(ex);
                if (generation >= 0) Close(generation);
                else MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw;
            }
        }

        public Task AttachAcceptedSocketAsync(StreamSocket socket) =>
            AttachAcceptedSocketCoreAsync(socket, CancellationToken.None, throwOnFailure: false);

        public Task AttachAcceptedSocketAsync(StreamSocket socket, CancellationToken cancellationToken) =>
            AttachAcceptedSocketCoreAsync(socket, cancellationToken, throwOnFailure: true);

        private async Task AttachAcceptedSocketCoreAsync(
            StreamSocket socket,
            CancellationToken cancellationToken,
            bool throwOnFailure)
        {
            ArgumentNullException.ThrowIfNull(socket);
            int requestEpoch;
            bool gateEntered = false;
            bool delegated = false;
            lock (_stateLock)
            {
                requestEpoch = _operationEpoch;
                _connectionAttemptCount++;
            }

            try
            {
                await _connectionAttemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateEntered = true;
                lock (_stateLock)
                {
                    if (requestEpoch != _operationEpoch)
                        throw new OperationCanceledException("The accepted connection was canceled before it started.");
                    if (_isConnected)
                        throw new InvalidOperationException("This ChatConnection already owns a connected socket.");
                }

                delegated = true;
                await AttachAcceptedSocketSerializedAsync(socket, cancellationToken, throwOnFailure, requestEpoch)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!delegated)
                {
                    SafeDispose(socket);
                    LogException(ex);
                    MarkAttemptFailureIfTerminal();
                }
                if (throwOnFailure) throw;
            }
            finally
            {
                lock (_stateLock) _connectionAttemptCount--;
                if (gateEntered) _connectionAttemptGate.Release();
            }
        }

        private async Task AttachAcceptedSocketSerializedAsync(
            StreamSocket socket,
            CancellationToken cancellationToken,
            bool throwOnFailure,
            int requestEpoch)
        {
            ArgumentNullException.ThrowIfNull(socket);
            if (_permanentlyRejected)
            {
                socket.Dispose();
                var rejected = new InvalidOperationException("This connection was rejected by the connection manager.");
                if (throwOnFailure) throw rejected;
                return;
            }
            if (_useEcdhHandshake && LocalHandshakeIdentity == null)
            {
                socket.Dispose();
                var exception = new InvalidOperationException("A stable local handshake identity is required.");
                LogException(exception);
                MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw exception;
                return;
            }

            Close(CurrentGeneration);
            await WaitForTransportLoopsAsync(cancellationToken).ConfigureAwait(false);
            int generation = -1;
            try
            {
                RemoteIpAddress = socket.Information.RemoteAddress?.DisplayName ?? "";
                PeerId = string.IsNullOrWhiteSpace(PeerId)
                    ? $"{RemoteIpAddress}:{socket.Information.RemotePort}"
                    : PeerId;
                PeerName = string.IsNullOrWhiteSpace(PeerName) ? RemoteIpAddress : PeerName;

                lock (_stateLock)
                {
                    if (requestEpoch != _operationEpoch)
                        throw new OperationCanceledException("The accepted connection was canceled.");
                    generation = AttachSocketLocked(socket, isInitiator: false);
                }
                await InitializeCryptoAsync(generation, isInitiator: false, cancellationToken);
                if (!IsCurrentConnectedGeneration(generation))
                    throw new OperationCanceledException("The accepted connection was closed during its handshake.");
                EnableSends(generation);
                StartSendLoop(generation);
                StartReceiveLoop();
                SafeLog("Chat TCP受信接続成功");
            }
            catch (Exception ex)
            {
                if (generation < 0) socket.Dispose();
                LogException(ex);
                if (generation >= 0) Close(generation);
                else MarkAttemptFailureIfTerminal();
                if (throwOnFailure) throw;
            }
        }

        public async Task SendPingAsync(string senderId, string senderName, string shortSessionId)
        {
            var ping = new ChatMessage
            {
                Type = "ping",
                SenderId = senderId,
                SenderName = senderName,
                ShortSessionId = shortSessionId,
                Body = ""
            };

            LastPingAt = DateTime.UtcNow;
            LastPingTimestamp = Stopwatch.GetTimestamp();
            IsPingWaiting = true;
            try
            {
                await SendAsync(ping);
            }
            catch
            {
                IsPingWaiting = false;
                throw;
            }
        }

        internal long LastPingTimestamp
        {
            get => Interlocked.Read(ref _lastPingTimestamp);
            private set => Interlocked.Exchange(ref _lastPingTimestamp, value);
        }

        internal long LastPongTimestamp
        {
            get => Interlocked.Read(ref _lastPongTimestamp);
            private set => Interlocked.Exchange(ref _lastPongTimestamp, value);
        }

        public Task SendAsync(ChatMessage message) => SendAsync(message, CancellationToken.None);

        public async Task SendAsync(ChatMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOutboundMessage(message);

            bool isHello = IsMessageType(message, "hello");
            if (isHello)
            {
                ValidateHello(message);
            }

            byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(message, MessageJsonOptions);
            if (plainBytes.Length == 0 || plainBytes.Length + AesGcmOverheadBytes > MaxMessageBytes)
            {
                throw new InvalidDataException($"The serialized message exceeds the {MaxMessageBytes}-byte frame limit.");
            }

            PendingSend? pendingSend = null;
            bool startLoop;
            try
            {
                lock (_stateLock)
                {
                    if (!_isConnected || _writer == null)
                    {
                        throw new IOException("Chat TCP is not connected.");
                    }

                    int generation = _connectionGeneration;
                    if (isHello)
                    {
                        if (_localHelloEnqueued)
                            throw new InvalidOperationException("HELLO was already queued on this connection.");
                        _localHelloEnqueued = true;
                        _localHelloSent = false;
                        _localHelloGeneration = generation;
                        LocalPeerId = message.SenderId.Trim();
                        LocalPeerName = message.SenderName?.Trim() ?? "";
                        LocalShortSessionId = message.ShortSessionId.Trim();
                    }
                    else if (!_isHelloVerified || !_localHelloSent)
                    {
                        throw new InvalidOperationException(
                            "Both local and remote HELLO messages must be completed before application messages are sent.");
                    }

                    pendingSend = new PendingSend(message, plainBytes, cancellationToken, generation);
                    lock (_sendQueueLock)
                    {
                        if (!_acceptingSends || generation != _connectionGeneration)
                            throw new IOException("Chat TCP is closing or was replaced.");
                        if (_queuedMessageCount >= MaximumQueuedMessages ||
                            _queuedBytes + plainBytes.Length > MaximumQueuedBytes)
                        {
                            throw new IOException("The bounded chat send queue is full.");
                        }

                        Queue<PendingSend> queue = IsHighPriorityMessage(message)
                            ? _highPrioritySends
                            : _normalPrioritySends;
                        queue.Enqueue(pendingSend);
                        _queuedMessageCount++;
                        _queuedBytes += plainBytes.Length;
                        startLoop = !_isSendLoopStarted;
                        if (startLoop) _isSendLoopStarted = true;
                    }
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                if (isHello && pendingSend != null)
                    ReleaseLocalHelloReservation(pendingSend.Generation);
                throw;
            }

            _sendSignal.Release();
            if (startLoop) StartSendLoopTask();
            try
            {
                await pendingSend!.Completion.Task.ConfigureAwait(false);
                if (isHello)
                {
                    lock (_stateLock)
                    {
                        if (pendingSend.Generation == _connectionGeneration && _isConnected && _localHelloEnqueued)
                            _localHelloSent = true;
                    }
                }
            }
            catch
            {
                if (isHello) ReleaseLocalHelloReservation(pendingSend!.Generation);
                throw;
            }
        }

        private void ReleaseLocalHelloReservation(int generation)
        {
            lock (_stateLock)
            {
                if (_localHelloGeneration != generation || _localHelloSent) return;
                _localHelloEnqueued = false;
                _localHelloGeneration = -1;
                LocalPeerId = "";
                LocalPeerName = "";
                LocalShortSessionId = "";
            }
        }

        private void StartSendLoop(int generation)
        {
            bool start;
            lock (_sendQueueLock)
            {
                start = _acceptingSends && !_isSendLoopStarted;
                if (start) _isSendLoopStarted = true;
            }

            if (start) StartSendLoopTask(generation);
        }

        private void EnableSends(int generation)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || !_isConnected || _writer == null)
                    throw new OperationCanceledException("The connection closed before sending was enabled.");
                lock (_sendQueueLock) _acceptingSends = true;
            }
        }

        private void StartSendLoopTask(int? knownGeneration = null)
        {
            int generation = knownGeneration ?? CurrentGeneration;
            Task task = SendLoopAsync(generation);
            lock (_stateLock)
            {
                if (generation == _connectionGeneration) _sendLoopTask = task;
            }
        }

        private async Task SendLoopAsync(int generation)
        {
            try
            {
                while (IsCurrentConnectedGeneration(generation))
                {
                    CancellationToken lifetime = GetConnectionToken(generation);
                    await _sendSignal.WaitAsync(lifetime).ConfigureAwait(false);
                    if (!IsCurrentConnectedGeneration(generation))
                    {
                        RestoreSendSignalIfQueued();
                        return;
                    }
                    if (!TryDequeueSend(generation, out PendingSend? pendingSend) || pendingSend == null)
                    {
                        if (!IsCurrentConnectedGeneration(generation))
                        {
                            RestoreSendSignalIfQueued();
                            return;
                        }
                        continue;
                    }

                    try
                    {
                        if (pendingSend.CancellationToken.IsCancellationRequested)
                        {
                            pendingSend.Completion.TrySetCanceled(pendingSend.CancellationToken);
                            continue;
                        }

                        await WriteMessageAsync(pendingSend, generation, lifetime).ConfigureAwait(false);
                        pendingSend.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        pendingSend.Completion.TrySetException(ex);
                        if (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
                        {
                            LogException(ex);
                        }
                        Close(generation);
                        return;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(pendingSend.PlainBytes);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close(generation);
            }
            finally
            {
                lock (_sendQueueLock)
                {
                    if (generation == Volatile.Read(ref _connectionGeneration)) _isSendLoopStarted = false;
                }
            }
        }

        private bool TryDequeueSend(int generation, out PendingSend? pendingSend)
        {
            lock (_sendQueueLock)
            {
                Queue<PendingSend>? selected = null;
                if (_highPrioritySends.Count > 0 &&
                    (_normalPrioritySends.Count == 0 || _consecutiveHighPrioritySends < 8))
                {
                    selected = _highPrioritySends;
                    _consecutiveHighPrioritySends++;
                }
                else if (_normalPrioritySends.Count > 0)
                {
                    selected = _normalPrioritySends;
                    _consecutiveHighPrioritySends = 0;
                }

                if (selected == null)
                {
                    pendingSend = null;
                    return false;
                }

                if (selected.Peek().Generation != generation)
                {
                    pendingSend = null;
                    return false;
                }

                pendingSend = selected.Dequeue();
                _queuedMessageCount--;
                _queuedBytes -= pendingSend.PlainBytes.Length;
                return true;
            }
        }

        private void RestoreSendSignalIfQueued()
        {
            lock (_sendQueueLock)
            {
                if (_queuedMessageCount > 0) _sendSignal.Release();
            }
        }

        private async Task WriteMessageAsync(PendingSend pendingSend, int generation, CancellationToken lifetime)
        {
            DataWriter writer = GetWriter(generation);
            byte[] encryptedBytes;
            lock (_cryptoLock) encryptedBytes = _messageCrypto.Encrypt(pendingSend.PlainBytes);
            try
            {
                if (encryptedBytes.Length == 0 || encryptedBytes.Length > MaxMessageBytes)
                {
                    throw new InvalidDataException("Encrypted message frame length is invalid.");
                }

                writer.WriteUInt32((uint)encryptedBytes.Length);
                writer.WriteBytes(encryptedBytes);
                using CancellationTokenSource operation = CreateTimeoutCancellation(lifetime, WriteTimeout);
                await writer.StoreAsync().AsTask(operation.Token).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(operation.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!ReferenceEquals(encryptedBytes, pendingSend.PlainBytes))
                    CryptographicOperations.ZeroMemory(encryptedBytes);
            }
        }

        private static bool IsHighPriorityMessage(ChatMessage message) =>
            IsMessageType(message, "ping") || IsMessageType(message, "pong") ||
            IsMessageType(message, "hello") || IsMessageType(message, "file_ack") ||
            IsMessageType(message, "file_abort");

        private static void ValidateOutboundMessage(ChatMessage message) =>
            ChatMessageValidator.ValidateOutbound(message);

        private static bool IsSha256Fingerprint(string? value)
        {
            string normalized = (value ?? "")
                .Replace(":", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Trim();
            return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
        }

        private void FailPendingSends(Exception exception, int generation)
        {
            lock (_stateLock)
            {
                lock (_sendQueueLock)
                {
                    if (generation == _connectionGeneration) _acceptingSends = false;
                    FailQueuedGeneration(_highPrioritySends, generation, exception);
                    FailQueuedGeneration(_normalPrioritySends, generation, exception);
                    if (_queuedMessageCount == 0) _consecutiveHighPrioritySends = 0;
                }
            }
        }

        private void FailQueuedGeneration(
            Queue<PendingSend> queue,
            int generation,
            Exception exception)
        {
            int count = queue.Count;
            for (int index = 0; index < count; index++)
            {
                PendingSend pending = queue.Dequeue();
                if (pending.Generation != generation)
                {
                    queue.Enqueue(pending);
                    continue;
                }

                _queuedMessageCount--;
                _queuedBytes -= pending.PlainBytes.Length;
                CryptographicOperations.ZeroMemory(pending.PlainBytes);
                pending.Completion.TrySetException(exception);
            }
        }

        private void StartReceiveLoop()
        {
            int generation;
            lock (_stateLock)
            {
                if (!_isConnected || _reader == null || _isReceiveLoopStarted) return;
                _isReceiveLoopStarted = true;
                generation = _connectionGeneration;
                _receiveLoopTask = ReceiveLoopAsync(generation);
            }
        }

        private async Task ReceiveLoopAsync(int generation)
        {
            try
            {
                while (IsCurrentConnectedGeneration(generation))
                {
                    CancellationToken lifetime = GetConnectionToken(generation);
                    DataReader reader = GetReader(generation);
                    TimeSpan frameTimeout = _isHelloVerified
                        ? ReadTimeout
                        : _helloReceived ? IdentityApprovalTimeout : HelloVerificationTimeout;
                    if (!await LoadExactAsync(reader, sizeof(uint), "length", lifetime, timeoutOverride: frameTimeout).ConfigureAwait(false)) break;

                    uint messageLength = reader.ReadUInt32();
                    if (messageLength == 0 || messageLength > MaxMessageBytes)
                        throw new InvalidDataException($"Invalid chat frame length: {messageLength}.");

                    EnforceReceiveRate(messageLength);
                    if (!await LoadExactAsync(reader, messageLength, "body", lifetime, timeoutOverride: frameTimeout).ConfigureAwait(false)) break;

                    byte[] encryptedBytes = new byte[messageLength];
                    reader.ReadBytes(encryptedBytes);
                    byte[]? plainBytes = null;
                    try
                    {
                        lock (_cryptoLock) plainBytes = _messageCrypto.Decrypt(encryptedBytes);
                        using JsonDocument document = JsonDocument.Parse(
                            plainBytes,
                            new JsonDocumentOptions
                            {
                                MaxDepth = 8,
                                AllowTrailingCommas = false,
                                CommentHandling = JsonCommentHandling.Disallow
                            });
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            !document.RootElement.TryGetProperty(nameof(ChatMessage.MessageId), out JsonElement messageIdElement) ||
                            messageIdElement.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException("MessageId is required in the serialized frame.");
                        }

                        ChatMessage? message = document.RootElement.Deserialize<ChatMessage>(MessageJsonOptions);
                        if (message == null) throw new InvalidDataException("The chat frame contained no message.");
                        ValidateOutboundMessage(message);

                        string messageType = message.Type?.Trim() ?? "";
                        if (!IsCurrentConnectedGeneration(generation))
                            throw new OperationCanceledException(lifetime);
                        if (!_messageTypeRateLimiter.TryConsume(messageType, Stopwatch.GetTimestamp()))
                            throw new InvalidDataException($"The {messageType} control-message rate limit was exceeded.");
                        if (string.Equals(messageType, "hello", StringComparison.OrdinalIgnoreCase))
                        {
                            RecordReceivedHello(message, generation, lifetime);
                        }
                        else
                        {
                            lock (_stateLock)
                            {
                                if (!_helloReceived)
                                    throw new InvalidDataException("An application message arrived before HELLO.");
                            }

                            await WaitForHelloVerificationAsync(lifetime).ConfigureAwait(false);
                            ValidateBoundIdentity(message);
                        }

                        LastResponseAt = DateTime.UtcNow;
                        if (string.Equals(messageType, "pong", StringComparison.OrdinalIgnoreCase))
                        {
                            LastPongAt = DateTime.UtcNow;
                            LastPongTimestamp = Stopwatch.GetTimestamp();
                            IsPingWaiting = false;
                        }

                        if (!IsCurrentConnectedGeneration(generation))
                            throw new OperationCanceledException(lifetime);
                        DispatchMessageSafely(message);
                    }
                    finally
                    {
                        if (plainBytes != null)
                            CryptographicOperations.ZeroMemory(plainBytes);
                        CryptographicOperations.ZeroMemory(encryptedBytes);
                    }
                }
            }
            catch (OperationCanceledException) when (!IsCurrentConnectedGeneration(generation))
            {
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
            finally
            {
                Close(generation);
            }
        }

        internal void RecordReceivedHello(
            ChatMessage message,
            int generation,
            CancellationToken lifetime)
        {
            ArgumentNullException.ThrowIfNull(message);
            ValidateHello(message);
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || !_isConnected)
                    throw new OperationCanceledException(lifetime);
                if (_helloReceived)
                    throw new InvalidDataException("A duplicate HELLO was received on the same connection.");
                _helloReceived = true;
                _receivedHelloMessageId = message.MessageId.Trim();
                _receivedHelloPeerId = message.SenderId.Trim();
                _receivedHelloSenderName = message.SenderName;
                _receivedHelloShortSessionId = message.ShortSessionId.Trim();
            }
        }

        private async Task WaitForHelloVerificationAsync(CancellationToken lifetime)
        {
            if (_isHelloVerified) return;
            using CancellationTokenSource timeout = CreateTimeoutCancellation(lifetime, IdentityApprovalTimeout);
            try
            {
                await _helloVerified.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                throw new InvalidDataException("An application message arrived before HELLO was verified.");
            }

            if (!_isHelloVerified) throw new InvalidDataException("HELLO verification failed.");
        }

        private void DispatchMessageSafely(ChatMessage message)
        {
            Action<ChatMessage, ChatConnection>? handlers = MessageReceived;
            if (handlers == null) return;
            foreach (Action<ChatMessage, ChatConnection> handler in
                     handlers.GetInvocationList().Cast<Action<ChatMessage, ChatConnection>>())
            {
                try { handler(message, this); }
                catch (Exception ex)
                {
                    SafeLog($"MessageReceived handler failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void ValidateHello(ChatMessage message)
        {
            ChatMessageValidator.ValidateHello(message);
        }

        private void ValidateBoundHello(ChatMessage message)
        {
            string peerId;
            string shortSessionId;
            lock (_stateLock)
            {
                peerId = _boundPeerId;
                shortSessionId = _boundShortSessionId;
            }

            if ((!string.IsNullOrWhiteSpace(peerId) &&
                 !string.Equals(peerId, message.SenderId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(shortSessionId) &&
                 !string.Equals(shortSessionId, message.ShortSessionId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("A repeated HELLO attempted to change the bound peer identity.");
            }
        }

        private void ValidateBoundIdentity(ChatMessage message)
        {
            string peerId;
            string shortSessionId;
            lock (_stateLock)
            {
                peerId = _boundPeerId;
                shortSessionId = _boundShortSessionId;
            }

            if (message.HopCount < 0 || message.HopCount > MaximumRelayHopCount)
                throw new InvalidDataException("The group relay hop count is invalid.");

            if (message.HopCount == 0)
            {
                if (!string.IsNullOrEmpty(message.RelaySenderId) ||
                    !string.IsNullOrEmpty(message.RelaySenderName) ||
                    !string.IsNullOrEmpty(message.RelayShortSessionId) ||
                    string.IsNullOrWhiteSpace(message.SenderId) ||
                    string.IsNullOrWhiteSpace(message.ShortSessionId) ||
                    (!string.IsNullOrWhiteSpace(peerId) &&
                     !string.Equals(peerId, message.SenderId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(shortSessionId) &&
                     !string.Equals(shortSessionId, message.ShortSessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("The direct message identity does not match the verified HELLO identity.");
                }

                return;
            }

            if (!message.IsGroup || IsInbound ||
                string.IsNullOrWhiteSpace(message.SenderId) ||
                string.IsNullOrWhiteSpace(message.ShortSessionId) ||
                string.IsNullOrWhiteSpace(message.RelaySenderId) ||
                string.IsNullOrWhiteSpace(message.RelayShortSessionId) ||
                (!string.IsNullOrWhiteSpace(peerId) &&
                 !string.Equals(peerId, message.RelaySenderId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(shortSessionId) &&
                 !string.Equals(shortSessionId, message.RelayShortSessionId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The group relay envelope does not match the verified host identity.");
            }
        }

        private void EnforceReceiveRate(uint frameLength)
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_receiveWindowStartedAt, now) >= TimeSpan.FromMinutes(1))
            {
                _receiveWindowStartedAt = now;
                _receiveWindowMessageCount = 0;
                _receiveWindowBytes = 0;
            }

            _receiveWindowMessageCount++;
            _receiveWindowBytes += frameLength;
            if (_receiveWindowMessageCount > MaximumMessagesPerMinute ||
                _receiveWindowBytes > MaximumBytesPerMinute)
            {
                throw new InvalidDataException("The connection exceeded the inbound rate limit.");
            }
        }

        public void Close()
        {
            StreamSocket? pendingSocket;
            CancellationTokenSource? pendingCancellation;
            int generation;
            lock (_stateLock)
            {
                _operationEpoch++;
                IsPreparing = false;
                pendingSocket = _pendingConnectSocket;
                pendingCancellation = _pendingConnectCancellation;
                _pendingConnectSocket = null;
                _pendingConnectCancellation = null;
                generation = _connectionGeneration;
            }

            try { pendingCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            SafeDispose(pendingSocket);
            Close(generation);
        }

        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            var loops = new List<Task>();
            lock (_stateLock)
            {
                if (_sendLoopTask != null) loops.Add(_sendLoopTask);
                if (_receiveLoopTask != null) loops.Add(_receiveLoopTask);
            }

            Close();
            await _connectionAttemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Close again at the gate's linearization point. Any reconnect that
                // raced the first Close is now either closed or invalidated by the
                // new operation epoch before another queued attempt can attach.
                Close();
                lock (_stateLock)
                {
                    if (_sendLoopTask != null) loops.Add(_sendLoopTask);
                    if (_receiveLoopTask != null) loops.Add(_receiveLoopTask);
                }
            }
            finally
            {
                _connectionAttemptGate.Release();
            }

            Task[] distinctLoops = loops.Distinct().ToArray();
            if (distinctLoops.Length == 0) return;
            try { await Task.WhenAll(distinctLoops).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }

        private async Task WaitForTransportLoopsAsync(CancellationToken cancellationToken)
        {
            Task[] loops;
            lock (_stateLock)
            {
                loops = new[] { _sendLoopTask, _receiveLoopTask }
                    .Where(task => task != null)
                    .Cast<Task>()
                    .Distinct()
                    .ToArray();
            }

            if (loops.Length == 0) return;
            try
            {
                await Task.WhenAll(loops).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        private void Close(int generation)
        {
            StreamSocket? socket;
            DataReader? reader;
            DataWriter? writer;
            CancellationTokenSource? cancellation;
            Task? sendLoop;
            Task? receiveLoop;
            bool shouldNotify;

            lock (_stateLock)
            {
                if (generation != _connectionGeneration || _closedGeneration == generation) return;
                _closedGeneration = generation;
                shouldNotify = (_isConnected || _isReady || _isHelloVerified || IsPingWaiting) &&
                               _connectionAttemptCount <= 1;
                _isConnected = false;
                _isReceiveLoopStarted = false;
                _isReady = false;
                _isHelloVerified = false;
                IsCryptographicIdentityVerified = false;
                _remoteIdentityStaged = false;
                _helloReceived = false;
                _receivedHelloMessageId = "";
                _receivedHelloPeerId = "";
                _receivedHelloSenderName = "";
                _receivedHelloShortSessionId = "";
                _localHelloEnqueued = false;
                _localHelloSent = false;
                _localHelloGeneration = -1;
                LocalPeerId = "";
                LocalPeerName = "";
                LocalShortSessionId = "";
                IsPreparing = false;
                IsPingWaiting = false;
                LastPingTimestamp = 0;
                LastPongTimestamp = 0;
                socket = _socket;
                reader = _reader;
                writer = _writer;
                cancellation = _connectionCancellation;
                sendLoop = _sendLoopTask;
                receiveLoop = _receiveLoopTask;
                _socket = null;
                _reader = null;
                _writer = null;
                _connectionCancellation = null;
                _helloVerified.TrySetResult(false);
            }

            FailPendingSends(new IOException("Chat TCP connection was closed."), generation);
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }

            // Dispose the socket first. It aborts an in-flight LoadAsync safely; DetachStream
            // must not be called while a read is pending.
            SafeDispose(socket);
            SafeDispose(reader);
            SafeDispose(writer);
            if (cancellation != null)
                _ = DisposeCancellationAfterLoopsAsync(cancellation, sendLoop, receiveLoop);

            if (_useEcdhHandshake)
            {
                lock (_stateLock)
                {
                    if (generation == _connectionGeneration && !_isConnected)
                        SetMessageCrypto(new NoOpMessageCrypto(), ownsMessageCrypto: true);
                }
            }

            MarkDisconnected(shouldNotify, generation);
        }

        private static async Task DisposeCancellationAfterLoopsAsync(
            CancellationTokenSource cancellation,
            Task? sendLoop,
            Task? receiveLoop)
        {
            Task[] loops = new[] { sendLoop, receiveLoop }
                .Where(task => task != null)
                .Cast<Task>()
                .Distinct()
                .ToArray();
            try
            {
                if (loops.Length > 0) await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch
            {
                // Loop failures are reported by the transport itself. This cleanup
                // task only owns the cancellation source lifetime.
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        internal void Reject(string reason)
        {
            _permanentlyRejected = true;
            SafeLog(reason);
            Close();
        }

        private int AttachSocket(StreamSocket socket, bool isInitiator)
        {
            lock (_stateLock)
            {
                return AttachSocketLocked(socket, isInitiator);
            }
        }

        private int AttachSocketLocked(StreamSocket socket, bool isInitiator)
        {
            if (_permanentlyRejected) throw new InvalidOperationException("The connection was rejected.");
            _connectionGeneration++;
            _closedGeneration = -1;
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream) { ByteOrder = ByteOrder.LittleEndian };
            _reader = new DataReader(socket.InputStream)
            {
                ByteOrder = ByteOrder.LittleEndian,
                InputStreamOptions = InputStreamOptions.Partial
            };
            _connectionCancellation = new CancellationTokenSource();
            _helloVerified = NewHelloCompletion();
            _isConnected = true;
            _isReceiveLoopStarted = false;
            _isSendLoopStarted = false;
            _sendLoopTask = null;
            _receiveLoopTask = null;
            _isHelloVerified = false;
            _isReady = false;
            _disconnectNotified = false;
            _acceptingSends = false;
            IsInitiatorConnection = isInitiator;
            Direction = isInitiator ? ChatConnectionDirection.Outbound : ChatConnectionDirection.Inbound;
            IsCryptographicIdentityVerified = false;
            RemotePublicKeyFingerprint = "";
            RemoteIdentityFingerprint = "";
            _boundPeerId = "";
            _boundShortSessionId = "";
            _remoteIdentityStaged = false;
            _helloReceived = false;
            _receivedHelloMessageId = "";
            _receivedHelloPeerId = "";
            _receivedHelloSenderName = "";
            _receivedHelloShortSessionId = "";
            _localHelloEnqueued = false;
            _localHelloSent = false;
            _localHelloGeneration = -1;
            LocalPeerId = "";
            LocalPeerName = "";
            LocalShortSessionId = "";
            IsPingWaiting = false;
            LastPingTimestamp = 0;
            LastPongTimestamp = 0;
            _receiveWindowStartedAt = Stopwatch.GetTimestamp();
            _receiveWindowMessageCount = 0;
            _receiveWindowBytes = 0;
            _messageTypeRateLimiter.Reset();
            return _connectionGeneration;
        }

        private void MarkDisconnected(bool shouldNotify, int expectedGeneration)
        {
            lock (_stateLock)
            {
                if (expectedGeneration != _connectionGeneration ||
                    !shouldNotify || _disconnectNotified) return;
                _disconnectNotified = true;
            }

            SafeLog($"Chat TCP切断: Peer={PeerName}");
            InvokeConnectionHandlersSafely(Disconnected, nameof(Disconnected));
            Action? closedHandlers = Closed;
            if (closedHandlers == null) return;
            foreach (Action handler in closedHandlers.GetInvocationList().Cast<Action>())
            {
                try { handler(); }
                catch (Exception ex)
                {
                    SafeLog($"Closed handler failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void MarkAttemptFailureIfTerminal()
        {
            bool shouldNotify;
            int generation;
            lock (_stateLock)
            {
                // A canceled request must not evict a live connection or another
                // serialized attempt that still owns this ChatConnection.
                shouldNotify = !_isConnected && _connectionAttemptCount <= 1;
                generation = _connectionGeneration;
            }
            MarkDisconnected(shouldNotify, generation);
        }

        private void InvokeConnectionHandlersSafely(Action<ChatConnection>? handlers, string eventName)
        {
            if (handlers == null) return;
            foreach (Action<ChatConnection> handler in handlers.GetInvocationList().Cast<Action<ChatConnection>>())
            {
                try { handler(this); }
                catch (Exception ex)
                {
                    SafeLog($"{eventName} handler failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task InitializeCryptoAsync(int generation, bool isInitiator, CancellationToken externalCancellation)
        {
            if (!_useEcdhHandshake) return;
            DataWriter writer = GetWriter(generation);
            DataReader reader = GetReader(generation);
            CancellationToken lifetime = GetConnectionToken(generation);
            using CancellationTokenSource handshake = CreateTimeoutCancellation(lifetime, HandshakeTimeout, externalCancellation);
            using ECDiffieHellman localKey = EcdhService.Create();
            EcdsaChatIdentity localIdentity = LocalHandshakeIdentity ??
                throw new InvalidOperationException("A stable local handshake identity is required.");
            Func<ChatConnection, string, bool>? remoteIdentityVerifier = RemoteIdentityVerifier;
            Func<ChatConnection, string, bool>? remotePublicKeyVerifier = RemotePublicKeyVerifier;
            byte[] localPublicKey = EcdhService.GetPublicKey(localKey);
            byte[] localIdentityPublicKey = localIdentity.ExportPublicKey();
            HandshakeKeyMaterial? remoteKeyMaterial = null;
            byte[]? sharedKey = null;
            byte[]? initiatorToAcceptorKey = null;
            byte[]? acceptorToInitiatorKey = null;

            try
            {
                if (isInitiator)
                {
                    await WriteHandshakePublicKeyAsync(
                        writer,
                        localPublicKey,
                        localIdentityPublicKey,
                        localIdentity,
                        ChatHandshakeRole.Initiator,
                        handshake.Token).ConfigureAwait(false);
                    remoteKeyMaterial = await ReadHandshakePublicKeyAsync(reader, handshake.Token).ConfigureAwait(false);
                }
                else
                {
                    remoteKeyMaterial = await ReadHandshakePublicKeyAsync(reader, handshake.Token).ConfigureAwait(false);
                    await WriteHandshakePublicKeyAsync(
                        writer,
                        localPublicKey,
                        localIdentityPublicKey,
                        localIdentity,
                        ChatHandshakeRole.Acceptor,
                        handshake.Token).ConfigureAwait(false);
                }

                string fingerprint = EcdhService.CreateFingerprint(remoteKeyMaterial.EphemeralPublicKey);
                VerifyRemoteHandshakeIdentity(
                    remoteKeyMaterial,
                    fingerprint,
                    isInitiator ? ChatHandshakeRole.Acceptor : ChatHandshakeRole.Initiator,
                    localIdentityPublicKey,
                    remoteIdentityVerifier,
                    remotePublicKeyVerifier);
                sharedKey = EcdhService.CreateSharedKey(localKey, remoteKeyMaterial.EphemeralPublicKey);
                byte[] initiatorPublicKey = isInitiator ? localPublicKey : remoteKeyMaterial.EphemeralPublicKey;
                byte[] acceptorPublicKey = isInitiator ? remoteKeyMaterial.EphemeralPublicKey : localPublicKey;
                byte[] initiatorIdentityPublicKey = isInitiator
                    ? localIdentityPublicKey
                    : remoteKeyMaterial.IdentityPublicKey;
                byte[] acceptorIdentityPublicKey = isInitiator
                    ? remoteKeyMaterial.IdentityPublicKey
                    : localIdentityPublicKey;
                initiatorToAcceptorKey = ChatSessionKeyDerivation.DeriveDirectionalKey(
                    sharedKey,
                    ChatKeyDirection.InitiatorToAcceptor,
                    initiatorPublicKey,
                    acceptorPublicKey,
                    initiatorIdentityPublicKey,
                    acceptorIdentityPublicKey);
                acceptorToInitiatorKey = ChatSessionKeyDerivation.DeriveDirectionalKey(
                    sharedKey,
                    ChatKeyDirection.AcceptorToInitiator,
                    initiatorPublicKey,
                    acceptorPublicKey,
                    initiatorIdentityPublicKey,
                    acceptorIdentityPublicKey);
                byte[] sendKey = isInitiator ? initiatorToAcceptorKey : acceptorToInitiatorKey;
                byte[] receiveKey = isInitiator ? acceptorToInitiatorKey : initiatorToAcceptorKey;
                var sessionCrypto = new AesGcmMessageCrypto(sendKey, receiveKey);
                if (!TrySetMessageCrypto(generation, sessionCrypto, ownsMessageCrypto: true))
                {
                    sessionCrypto.Dispose();
                    throw new OperationCanceledException(handshake.Token);
                }
                RemotePublicKeyFingerprint = fingerprint;
                SafeLog($"PeerPublicKeyFingerprint: {fingerprint}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(localPublicKey);
                CryptographicOperations.ZeroMemory(localIdentityPublicKey);
                if (remoteKeyMaterial != null)
                {
                    CryptographicOperations.ZeroMemory(remoteKeyMaterial.EphemeralPublicKey);
                    CryptographicOperations.ZeroMemory(remoteKeyMaterial.IdentityPublicKey);
                    CryptographicOperations.ZeroMemory(remoteKeyMaterial.IdentitySignature);
                }
                if (sharedKey != null) CryptographicOperations.ZeroMemory(sharedKey);
                if (initiatorToAcceptorKey != null)
                    CryptographicOperations.ZeroMemory(initiatorToAcceptorKey);
                if (acceptorToInitiatorKey != null)
                    CryptographicOperations.ZeroMemory(acceptorToInitiatorKey);
            }
        }

        private void VerifyRemoteHandshakeIdentity(
            HandshakeKeyMaterial remote,
            string ephemeralFingerprint,
            ChatHandshakeRole remoteRole,
            ReadOnlySpan<byte> localIdentityPublicKey,
            Func<ChatConnection, string, bool>? remoteIdentityVerifier,
            Func<ChatConnection, string, bool>? remotePublicKeyVerifier)
        {
            bool ephemeralAuthorized = false;
            if (!string.IsNullOrWhiteSpace(ExpectedRemotePublicKeyFingerprint))
            {
                if (!EcdhService.FingerprintsEqual(ExpectedRemotePublicKeyFingerprint, ephemeralFingerprint))
                    throw new CryptographicException("The remote ECDH public key fingerprint did not match the pinned identity.");
                ephemeralAuthorized = true;
            }

            bool remoteSuppliedIdentity = remote.IdentityPublicKey.Length > 0 || remote.IdentitySignature.Length > 0;
            bool stableAuthorizationRequired = RequireAuthenticatedRemoteIdentity ||
                                               !string.IsNullOrWhiteSpace(ExpectedRemoteIdentityFingerprint) ||
                                               remoteIdentityVerifier != null;
            if (!remoteSuppliedIdentity)
            {
                if (stableAuthorizationRequired)
                    throw new CryptographicException("The remote peer did not provide a signed stable identity.");
                IsCryptographicIdentityVerified = ephemeralAuthorized;
                return;
            }

            if (remote.IdentityPublicKey.Length == 0 || remote.IdentitySignature.Length == 0 ||
                !EcdsaChatIdentity.VerifyEphemeralKey(
                    remote.IdentityPublicKey,
                    remote.EphemeralPublicKey,
                    remote.IdentitySignature,
                    remoteRole))
            {
                throw new CryptographicException("The remote stable identity signature was invalid.");
            }

            string identityFingerprint = EcdsaChatIdentity.CreatePublicKeyFingerprint(remote.IdentityPublicKey);
            string localIdentityFingerprint = EcdsaChatIdentity.CreatePublicKeyFingerprint(localIdentityPublicKey);
            if (EcdhService.FingerprintsEqual(localIdentityFingerprint, identityFingerprint))
                throw new CryptographicException("A remote connection cannot authenticate as the local stable identity.");
            RemoteIdentityFingerprint = identityFingerprint;
            bool stableAuthorized = false;
            if (!string.IsNullOrWhiteSpace(ExpectedRemoteIdentityFingerprint))
            {
                if (!EcdhService.FingerprintsEqual(ExpectedRemoteIdentityFingerprint, identityFingerprint))
                    throw new CryptographicException("The remote stable identity fingerprint did not match the pinned identity.");
                stableAuthorized = true;
            }

            if (remoteIdentityVerifier != null)
            {
                if (!remoteIdentityVerifier(this, identityFingerprint))
                    throw new CryptographicException("The application rejected the remote stable identity.");
                stableAuthorized = true;
            }

            if (RequireAuthenticatedRemoteIdentity && !stableAuthorized)
                throw new CryptographicException("Stable identity authentication requires a fingerprint pin or authorization callback.");

            // Invoke an application callback only after the stable signature, role,
            // self-identity check, and stable pin policy have all succeeded. This keeps
            // a malformed handshake from causing TOFU/pinning side effects.
            if (remotePublicKeyVerifier != null)
            {
                if (!remotePublicKeyVerifier(this, ephemeralFingerprint))
                    throw new CryptographicException("The application rejected the remote ECDH public key.");
                ephemeralAuthorized = true;
            }

            IsCryptographicIdentityVerified = stableAuthorized || ephemeralAuthorized;
        }

        private static async Task WriteHandshakePublicKeyAsync(
            DataWriter writer,
            byte[] publicKey,
            byte[] identityPublicKey,
            EcdsaChatIdentity localIdentity,
            ChatHandshakeRole localRole,
            CancellationToken token)
        {
            byte[] identitySignature = localIdentity.SignEphemeralKey(publicKey, localRole);
            byte[] payload = CreateHandshakePayload(publicKey, identityPublicKey, identitySignature);
            try
            {
                writer.WriteInt32(payload.Length);
                writer.WriteBytes(payload);
                await writer.StoreAsync().AsTask(token).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(identitySignature);
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private async Task<HandshakeKeyMaterial> ReadHandshakePublicKeyAsync(DataReader reader, CancellationToken token)
        {
            if (!await LoadExactAsync(reader, sizeof(int), "handshake length", token, applyReadTimeout: false).ConfigureAwait(false))
                throw new EndOfStreamException("Chat encryption handshake ended before its length was received.");
            int length = reader.ReadInt32();
            if (length <= HandshakeMagic.Length || length > MaxHandshakeFrameLength)
                throw new InvalidDataException($"Invalid chat handshake length: {length}.");
            if (!await LoadExactAsync(reader, (uint)length, "handshake body", token, applyReadTimeout: false).ConfigureAwait(false))
                throw new EndOfStreamException("Chat encryption handshake ended before its body was received.");
            byte[] payload = new byte[length];
            reader.ReadBytes(payload);
            try
            {
                return ReadPublicKeyFromHandshakePayload(payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        private async Task<bool> LoadExactAsync(
            DataReader reader,
            uint bytesToRead,
            string label,
            CancellationToken token,
            bool applyReadTimeout = true,
            TimeSpan? timeoutOverride = null)
        {
            using CancellationTokenSource? timeout = applyReadTimeout
                ? CreateTimeoutCancellation(token, timeoutOverride ?? ReadTimeout)
                : null;
            CancellationToken effectiveToken = timeout?.Token ?? token;
            while (reader.UnconsumedBufferLength < bytesToRead)
            {
                uint need = bytesToRead - reader.UnconsumedBufferLength;
                uint loaded;
                try
                {
                    loaded = await reader.LoadAsync(need).AsTask(effectiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out while reading chat {label}.");
                }

                if (loaded == 0) return false;
            }

            return true;
        }

        private static byte[] CreateHandshakePayload(
            byte[] publicKey,
            byte[] identityPublicKey,
            byte[] identitySignature)
        {
            if (publicKey == null || publicKey.Length == 0)
                throw new ArgumentException("A public key is required.", nameof(publicKey));
            int length = checked(HandshakeMagic.Length + sizeof(int) * 3 +
                                 publicKey.Length + identityPublicKey.Length + identitySignature.Length);
            if (length > MaxHandshakeFrameLength) throw new InvalidDataException("The handshake identity payload is too large.");
            byte[] payload = new byte[length];
            System.Buffer.BlockCopy(HandshakeMagic, 0, payload, 0, HandshakeMagic.Length);
            int offset = HandshakeMagic.Length;
            WriteLengthPrefixed(payload, ref offset, publicKey);
            WriteLengthPrefixed(payload, ref offset, identityPublicKey);
            WriteLengthPrefixed(payload, ref offset, identitySignature);
            return payload;
        }

        private static HandshakeKeyMaterial ReadPublicKeyFromHandshakePayload(byte[] payload)
        {
            if (payload.Length <= HandshakeMagic.Length ||
                !payload.AsSpan(0, HandshakeMagic.Length).SequenceEqual(HandshakeMagic))
                throw new InvalidDataException("The chat encryption handshake format is invalid.");
            int offset = HandshakeMagic.Length;
            byte[] ephemeral = ReadLengthPrefixed(payload, ref offset, allowEmpty: false);
            byte[] identity = ReadLengthPrefixed(payload, ref offset, allowEmpty: true);
            byte[] signature = ReadLengthPrefixed(payload, ref offset, allowEmpty: true);
            if (offset != payload.Length) throw new InvalidDataException("Trailing data followed the chat handshake.");
            return new HandshakeKeyMaterial(ephemeral, identity, signature);
        }

        private static void WriteLengthPrefixed(byte[] destination, ref int offset, byte[] value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, sizeof(int)), value.Length);
            offset += sizeof(int);
            value.CopyTo(destination, offset);
            offset += value.Length;
        }

        private static byte[] ReadLengthPrefixed(byte[] source, ref int offset, bool allowEmpty)
        {
            if (offset > source.Length - sizeof(int)) throw new InvalidDataException("The chat handshake was truncated.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            if (length < 0 || (!allowEmpty && length == 0) || length > source.Length - offset)
                throw new InvalidDataException("The chat handshake field length is invalid.");
            byte[] value = source.AsSpan(offset, length).ToArray();
            offset += length;
            return value;
        }

        private void SetMessageCrypto(IMessageCrypto messageCrypto, bool ownsMessageCrypto)
        {
            lock (_cryptoLock)
            {
                if (_ownsMessageCrypto && _messageCrypto is IDisposable disposable) disposable.Dispose();
                _messageCrypto = messageCrypto;
                _ownsMessageCrypto = ownsMessageCrypto;
            }
        }

        private bool TrySetMessageCrypto(int generation, IMessageCrypto messageCrypto, bool ownsMessageCrypto)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || !_isConnected) return false;
                SetMessageCrypto(messageCrypto, ownsMessageCrypto);
                return true;
            }
        }

        private int CurrentGeneration
        {
            get { lock (_stateLock) return _connectionGeneration; }
        }

        private bool IsCurrentConnectedGeneration(int generation)
        {
            lock (_stateLock) return generation == _connectionGeneration && _isConnected;
        }

        private CancellationToken GetConnectionToken(int generation)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || _connectionCancellation == null)
                    return new CancellationToken(canceled: true);
                return _connectionCancellation.Token;
            }
        }

        private DataReader GetReader(int generation)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || _reader == null) throw new IOException("Chat reader is closed.");
                return _reader;
            }
        }

        private DataWriter GetWriter(int generation)
        {
            lock (_stateLock)
            {
                if (generation != _connectionGeneration || _writer == null) throw new IOException("Chat writer is closed.");
                return _writer;
            }
        }

        private static CancellationTokenSource CreateTimeoutCancellation(
            CancellationToken primary,
            TimeSpan timeout,
            CancellationToken secondary = default)
        {
            CancellationTokenSource cancellation = secondary.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(primary, secondary)
                : CancellationTokenSource.CreateLinkedTokenSource(primary);
            if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) cancellation.CancelAfter(timeout);
            return cancellation;
        }

        private static TaskCompletionSource<bool> NewHelloCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static bool IsMessageType(ChatMessage message, string type) =>
            string.Equals(message.Type?.Trim(), type, StringComparison.OrdinalIgnoreCase);

        private static void SafeDispose(IDisposable? disposable)
        {
            try { disposable?.Dispose(); } catch { }
        }

        private void LogException(Exception ex)
        {
            SafeLog("Chat TCPエラー");
            SafeLog($"例外名: {ex.GetType().Name}");
            SafeLog($"HResult: 0x{ex.HResult:X8}");
            SafeLog($"Message: {ex.Message}");
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
                    Debug.WriteLine($"ChatConnection log handler failed: {ex}");
                }
            }
        }

        private sealed class PendingSend
        {
            public PendingSend(
                ChatMessage message,
                byte[] plainBytes,
                CancellationToken cancellationToken,
                int generation)
            {
                Message = message;
                PlainBytes = plainBytes;
                CancellationToken = cancellationToken;
                Generation = generation;
            }

            public ChatMessage Message { get; }
            public byte[] PlainBytes { get; }
            public CancellationToken CancellationToken { get; }
            public int Generation { get; }
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record HandshakeKeyMaterial(
            byte[] EphemeralPublicKey,
            byte[] IdentityPublicKey,
            byte[] IdentitySignature);
    }
}
