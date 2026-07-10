using direct_module.Crypto;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public class ChatConnection
    {
        private const uint MaxMessageBytes = 1024 * 1024;
        private const int MaxHandshakeFrameLength = 4096;
        private static readonly byte[] HandshakeMagic = Encoding.ASCII.GetBytes("NOVA-ECDH-1|");

        private readonly bool _useEcdhHandshake;
        private readonly object _sendQueueLock = new();
        private readonly Queue<PendingSend> _highPrioritySends = new();
        private readonly Queue<PendingSend> _normalPrioritySends = new();
        private readonly SemaphoreSlim _sendSignal = new(0);
        private IMessageCrypto _messageCrypto;
        private bool _ownsMessageCrypto;
        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;
        private bool _isConnected;
        private bool _isReceiveLoopStarted;
        private bool _isSendLoopStarted;
        private bool _disconnectNotified;

        public ChatConnection()
            : this(new NoOpMessageCrypto(), useEcdhHandshake: true, ownsMessageCrypto: true)
        {
        }

        public ChatConnection(IMessageCrypto messageCrypto)
            : this(messageCrypto, useEcdhHandshake: false, ownsMessageCrypto: false)
        {
        }

        private ChatConnection(
            IMessageCrypto messageCrypto,
            bool useEcdhHandshake,
            bool ownsMessageCrypto)
        {
            _messageCrypto = messageCrypto ?? throw new ArgumentNullException(nameof(messageCrypto));
            _useEcdhHandshake = useEcdhHandshake;
            _ownsMessageCrypto = ownsMessageCrypto;
        }

        public string PeerId { get; set; } = "";

        public string PeerName { get; set; } = "";

        public string RemoteIpAddress { get; set; } = "";

        public string ShortSessionId { get; set; } = "";

        public bool IsPreparing { get; set; }

        public DateTime? LastPingAt { get; set; }

        public DateTime? LastPongAt { get; set; }

        public DateTime? LastResponseAt { get; set; }

        public bool IsPingWaiting { get; set; }

        public bool IsHelloVerified { get; set; }

        public bool IsReady { get; set; }

        public bool IsConnected => _isConnected;

        public bool IsReceiveLoopStarted => _isReceiveLoopStarted;

        public event Action<ChatMessage, ChatConnection>? MessageReceived;
        public event Action<string>? LogReceived;
        public event Action<ChatConnection>? Disconnected;
        public event Action? Closed;

        public async Task ConnectAsync(string ipAddress, int port)
        {
            if (_isConnected)
            {
                LogReceived?.Invoke("Chat TCP接続済みなので再利用");
                return;
            }

            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                LogReceived?.Invoke("Chat TCPエラー");
                LogReceived?.Invoke("Message: IPアドレスが空です");
                return;
            }

            if (port <= 0)
            {
                LogReceived?.Invoke("Chat TCPエラー");
                LogReceived?.Invoke("Message: ポート番号が不正です");
                return;
            }

            var totalWatch = Stopwatch.StartNew();

            try
            {
                LogReceived?.Invoke("Chat TCP ConnectAsync開始");
                LogReceived?.Invoke($"接続先IP: {ipAddress}");
                LogReceived?.Invoke($"接続先Port: {port}");

                var connectWatch = Stopwatch.StartNew();
                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName(ipAddress), port.ToString());
                LogReceived?.Invoke($"ConnectAsync: {connectWatch.ElapsedMilliseconds}ms");

                RemoteIpAddress = ipAddress;
                AttachSocket(socket);
                await InitializeCryptoAsync(isInitiator: true);
                StartSendLoop();

                LogReceived?.Invoke("Chat TCP ConnectAsync完了");
                LogReceived?.Invoke("Chat TCP接続成功");
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
                LogReceived?.Invoke($"Chat TCP接続完了 合計: {totalWatch.ElapsedMilliseconds}ms");
                StartReceiveLoop();
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
        }

        public async Task AttachAcceptedSocketAsync(StreamSocket socket)
        {
            try
            {
                long startedAt = Stopwatch.GetTimestamp();

                Close();

                RemoteIpAddress = socket.Information.RemoteAddress?.DisplayName ?? "";
                PeerId = string.IsNullOrWhiteSpace(PeerId)
                    ? $"{RemoteIpAddress}:{socket.Information.RemotePort}"
                    : PeerId;
                PeerName = string.IsNullOrWhiteSpace(PeerName)
                    ? RemoteIpAddress
                    : PeerName;

                AttachSocket(socket);
                await InitializeCryptoAsync(isInitiator: false);
                StartSendLoop();

                LogReceived?.Invoke("Chat TCP接続成功");
                LogReceived?.Invoke("Chat TCP受信側socketを保持しました");
                LogReceived?.Invoke($"RemoteAddress: {RemoteIpAddress}");
                LogReceived?.Invoke($"Chat TCP受信側準備時間: {GetElapsedMilliseconds(startedAt)}ms");
                StartReceiveLoop();
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
        }

        public void AttachAcceptedSocket(StreamSocket socket)
        {
            _ = AttachAcceptedSocketAsync(socket);
        }

        public async Task SendAsync(string message)
        {
            var chatMessage = new ChatMessage
            {
                Type = "chat",
                Body = message
            };

            await SendAsync(chatMessage);
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

            LastPingAt = DateTime.Now;
            IsPingWaiting = true;

            await SendAsync(ping);
        }

        public async Task SendAsync(ChatMessage message)
        {
            var totalWatch = Stopwatch.StartNew();

            LogReceived?.Invoke("SendAsync開始");
            LogReceived?.Invoke("Chat TCP送信開始");
            LogReceived?.Invoke($"接続状態: IsConnected={_isConnected}");
            LogReceived?.Invoke($"SendAsync内でConnectが必要か: {!_isConnected}");
            LogReceived?.Invoke($"ChatMessage JSON送信: Type={message.Type}");
            LogReceived?.Invoke($"MessageId: {message.MessageId}");
            LogReceived?.Invoke($"SenderName: {message.SenderName}");
            LogReceived?.Invoke($"送信内容: {message.Body}");

            if (!_isConnected || _writer == null)
            {
                LogReceived?.Invoke("Chat TCPエラー");
                LogReceived?.Invoke("Chat TCP未接続のため送信できません。事前接続を確認してください。");
                return;
            }

            var pendingSend = new PendingSend(message);
            EnqueueSend(pendingSend);
            await pendingSend.Completion.Task;

            LogReceived?.Invoke($"Chat TCP送信完了 合計: {totalWatch.ElapsedMilliseconds}ms");
        }

        private void EnqueueSend(PendingSend pendingSend)
        {
            lock (_sendQueueLock)
            {
                if (IsHighPriorityMessage(pendingSend.Message))
                {
                    _highPrioritySends.Enqueue(pendingSend);
                }
                else
                {
                    _normalPrioritySends.Enqueue(pendingSend);
                }
            }

            _sendSignal.Release();
            StartSendLoop();
        }

        private void StartSendLoop()
        {
            if (!_isConnected || _writer == null)
            {
                return;
            }

            lock (_sendQueueLock)
            {
                if (_isSendLoopStarted)
                {
                    return;
                }

                _isSendLoopStarted = true;
            }

            _ = SendLoopAsync();
        }

        private async Task SendLoopAsync()
        {
            try
            {
                while (_isConnected)
                {
                    await _sendSignal.WaitAsync();

                    if (!TryDequeueSend(out PendingSend? pendingSend) || pendingSend == null)
                    {
                        continue;
                    }

                    try
                    {
                        await WriteMessageAsync(pendingSend.Message);
                        pendingSend.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                        LogReceived?.Invoke($"SendAsync失敗により切断扱い: Peer={PeerName}");
                        pendingSend.Completion.TrySetException(ex);
                        Close();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
            finally
            {
                lock (_sendQueueLock)
                {
                    _isSendLoopStarted = false;
                }
            }
        }

        private bool TryDequeueSend(out PendingSend? pendingSend)
        {
            lock (_sendQueueLock)
            {
                if (_highPrioritySends.Count > 0)
                {
                    pendingSend = _highPrioritySends.Dequeue();
                    return true;
                }

                if (_normalPrioritySends.Count > 0)
                {
                    pendingSend = _normalPrioritySends.Dequeue();
                    return true;
                }
            }

            pendingSend = null;
            return false;
        }

        private async Task WriteMessageAsync(ChatMessage message)
        {
            if (!_isConnected || _writer == null)
            {
                throw new IOException("Chat TCP is not connected.");
            }

            string json = JsonSerializer.Serialize(message);
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);
            byte[] encryptedBytes = _messageCrypto.Encrypt(plainBytes);

            _writer.WriteUInt32((uint)encryptedBytes.Length);
            _writer.WriteBytes(encryptedBytes);

            var storeWatch = Stopwatch.StartNew();
            await _writer.StoreAsync();
            LogReceived?.Invoke($"StoreAsync: {storeWatch.ElapsedMilliseconds}ms");

            var flushWatch = Stopwatch.StartNew();
            await _writer.FlushAsync();
            LogReceived?.Invoke($"FlushAsync: {flushWatch.ElapsedMilliseconds}ms");

            LogReceived?.Invoke("Chat TCP送信成功");
            LogReceived?.Invoke($"平文Bytes: {plainBytes.Length}");
            LogReceived?.Invoke($"送信Bytes: {encryptedBytes.Length}");
        }

        private static bool IsHighPriorityMessage(ChatMessage message)
        {
            return string.Equals(message.Type, "ping", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message.Type, "pong", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message.Type, "hello", StringComparison.OrdinalIgnoreCase);
        }

        private void FailPendingSends(Exception exception)
        {
            lock (_sendQueueLock)
            {
                while (_highPrioritySends.Count > 0)
                {
                    _highPrioritySends.Dequeue().Completion.TrySetException(exception);
                }

                while (_normalPrioritySends.Count > 0)
                {
                    _normalPrioritySends.Dequeue().Completion.TrySetException(exception);
                }
            }
        }

        public void StartReceiveLoop()
        {
            if (!_isConnected || _reader == null || _isReceiveLoopStarted)
            {
                return;
            }

            _isReceiveLoopStarted = true;
            LogReceived?.Invoke("Chat TCP ReceiveLoop開始");
            _ = ReceiveLoopAsync();
        }

        public async Task ReceiveLoopAsync()
        {
            if (_reader == null)
            {
                return;
            }

            try
            {
                while (_isConnected)
                {
                    bool lengthRead = await LoadExactAsync(sizeof(uint), "length");
                    if (!lengthRead)
                    {
                        LogReceived?.Invoke("Chat TCP切断: lengthを読み取れませんでした");
                        break;
                    }

                    uint messageLength = _reader.ReadUInt32();
                    if (messageLength == 0 || messageLength > MaxMessageBytes)
                    {
                        LogReceived?.Invoke($"不正なメッセージ長: {messageLength}");
                        break;
                    }

                    bool bodyRead = await LoadExactAsync(messageLength, "body");
                    if (!bodyRead)
                    {
                        LogReceived?.Invoke($"本文不足: {_reader.UnconsumedBufferLength}/{messageLength}");
                        break;
                    }

                    byte[] encryptedBytes = new byte[messageLength];
                    _reader.ReadBytes(encryptedBytes);

                    byte[] plainBytes = _messageCrypto.Decrypt(encryptedBytes);
                    string json = Encoding.UTF8.GetString(plainBytes);
                    ChatMessage? message;

                    try
                    {
                        message = JsonSerializer.Deserialize<ChatMessage>(json);
                    }
                    catch (JsonException ex)
                    {
                        LogReceived?.Invoke("ChatMessage復元失敗");
                        LogReceived?.Invoke($"Message: {ex.Message}");
                        continue;
                    }

                    if (message == null)
                    {
                        LogReceived?.Invoke("ChatMessage復元失敗");
                        continue;
                    }

                    LastResponseAt = DateTime.Now;

                    if (string.IsNullOrWhiteSpace(PeerName) && !string.IsNullOrWhiteSpace(message.SenderName))
                    {
                        PeerName = message.SenderName;
                    }

                    if (string.IsNullOrWhiteSpace(PeerId) && !string.IsNullOrWhiteSpace(message.SenderId))
                    {
                        PeerId = message.SenderId;
                    }

                    LogReceived?.Invoke("Chat TCP受信");
                    LogReceived?.Invoke($"受信Bytes: {encryptedBytes.Length}");
                    LogReceived?.Invoke($"復号後Bytes: {plainBytes.Length}");
                    LogReceived?.Invoke($"ChatMessage JSON受信: Type={message.Type}");
                    LogReceived?.Invoke($"MessageId: {message.MessageId}");
                    LogReceived?.Invoke($"SenderName: {message.SenderName}");
                    LogReceived?.Invoke($"TCP受信: {message.Body}");
                    MessageReceived?.Invoke(message, this);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
            finally
            {
                LogReceived?.Invoke("Chat TCP ReceiveLoop終了");
                LogReceived?.Invoke($"ReceiveLoop終了: Peer={PeerName}");
                Close();
            }
        }

        public void Close()
        {
            bool shouldNotify = _isConnected || IsReady || IsHelloVerified || IsPingWaiting;

            _isConnected = false;
            _isReceiveLoopStarted = false;
            IsPreparing = false;
            IsPingWaiting = false;
            IsHelloVerified = false;
            IsReady = false;
            FailPendingSends(new IOException("Chat TCP connection was closed."));
            _sendSignal.Release();

            _reader?.DetachStream();
            _writer?.DetachStream();

            _reader?.Dispose();
            _writer?.Dispose();
            _socket?.Dispose();

            _reader = null;
            _writer = null;
            _socket = null;

            if (_useEcdhHandshake && _messageCrypto is not NoOpMessageCrypto)
            {
                SetMessageCrypto(new NoOpMessageCrypto(), ownsMessageCrypto: true);
            }

            MarkDisconnected(shouldNotify);
        }

        private void AttachSocket(StreamSocket socket)
        {
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream)
            {
                ByteOrder = ByteOrder.LittleEndian
            };
            LogReceived?.Invoke("DataWriter作成完了");
            _reader = new DataReader(socket.InputStream)
            {
                ByteOrder = ByteOrder.LittleEndian,
                InputStreamOptions = InputStreamOptions.Partial
            };
            LogReceived?.Invoke("DataReader作成完了");
            _isConnected = true;
            _isReceiveLoopStarted = false;
            _disconnectNotified = false;
        }

        private void MarkDisconnected(bool shouldNotify)
        {
            if (!shouldNotify || _disconnectNotified)
            {
                return;
            }

            _disconnectNotified = true;
            _isConnected = false;
            _isReceiveLoopStarted = false;
            IsReady = false;
            IsHelloVerified = false;
            IsPreparing = false;
            IsPingWaiting = false;

            LogReceived?.Invoke($"Chat TCP切断: Peer={PeerName}");
            LogReceived?.Invoke("Chat TCP切断");
            Disconnected?.Invoke(this);
            Closed?.Invoke();
        }

        private async Task InitializeCryptoAsync(bool isInitiator)
        {
            if (!_useEcdhHandshake)
            {
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
                return;
            }

            if (_writer == null || _reader == null)
            {
                throw new InvalidOperationException("Chat TCP stream is not ready.");
            }

            using ECDiffieHellman localKey = EcdhService.Create();
            byte[] localPublicKey = EcdhService.GetPublicKey(localKey);
            byte[]? remotePublicKey = null;
            byte[]? sharedKey = null;

            try
            {
                long startedAt = Stopwatch.GetTimestamp();

                LogReceived?.Invoke("Chat暗号ハンドシェイク開始");

                if (isInitiator)
                {
                    await WriteHandshakePublicKeyAsync(localPublicKey);
                    remotePublicKey = await ReadHandshakePublicKeyAsync();
                }
                else
                {
                    remotePublicKey = await ReadHandshakePublicKeyAsync();
                    await WriteHandshakePublicKeyAsync(localPublicKey);
                }

                string remoteFingerprint = EcdhService.CreateFingerprint(remotePublicKey);
                sharedKey = EcdhService.CreateSharedKey(localKey, remotePublicKey);
                SetMessageCrypto(new AesGcmMessageCrypto(sharedKey), ownsMessageCrypto: true);

                LogReceived?.Invoke("Chat暗号ハンドシェイク成功");
                LogReceived?.Invoke($"Chat暗号ハンドシェイク時間: {GetElapsedMilliseconds(startedAt)}ms");
                LogReceived?.Invoke($"PeerPublicKeyFingerprint: {remoteFingerprint}");
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(localPublicKey);

                if (remotePublicKey != null)
                {
                    CryptographicOperations.ZeroMemory(remotePublicKey);
                }

                if (sharedKey != null)
                {
                    CryptographicOperations.ZeroMemory(sharedKey);
                }
            }
        }

        private async Task WriteHandshakePublicKeyAsync(byte[] publicKey)
        {
            if (_writer == null)
            {
                throw new InvalidOperationException("Chat TCP writer is not ready.");
            }

            byte[] payload = CreateHandshakePayload(publicKey);

            _writer.WriteInt32(payload.Length);
            _writer.WriteBytes(payload);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }

        private async Task<byte[]> ReadHandshakePublicKeyAsync()
        {
            if (_reader == null)
            {
                throw new InvalidOperationException("Chat TCP reader is not ready.");
            }

            if (!await LoadExactAsync(sizeof(int), "handshake length"))
            {
                throw new EndOfStreamException("Chat暗号ハンドシェイクを読み込めませんでした。");
            }

            int length = _reader.ReadInt32();

            if (length <= HandshakeMagic.Length || length > MaxHandshakeFrameLength)
            {
                throw new InvalidDataException($"Chat暗号ハンドシェイク長が不正です Length={length}");
            }

            if (!await LoadExactAsync((uint)length, "handshake body"))
            {
                throw new EndOfStreamException("Chat暗号ハンドシェイク本文を読み込めませんでした。");
            }

            byte[] payload = new byte[length];
            _reader.ReadBytes(payload);

            return ReadPublicKeyFromHandshakePayload(payload);
        }

        private async Task<bool> LoadExactAsync(uint bytesToRead, string label)
        {
            if (_reader == null)
            {
                return false;
            }

            while (_reader.UnconsumedBufferLength < bytesToRead)
            {
                uint need = bytesToRead - _reader.UnconsumedBufferLength;
                uint loaded = await _reader.LoadAsync(need);

                if (loaded == 0)
                {
                    LogReceived?.Invoke($"Chat TCP切断: {label}読み取り中に0 bytes");
                    return false;
                }
            }

            return true;
        }

        private static byte[] CreateHandshakePayload(byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0)
            {
                throw new ArgumentException("公開鍵が不正です。", nameof(publicKey));
            }

            byte[] payload = new byte[HandshakeMagic.Length + publicKey.Length];
            System.Buffer.BlockCopy(HandshakeMagic, 0, payload, 0, HandshakeMagic.Length);
            System.Buffer.BlockCopy(publicKey, 0, payload, HandshakeMagic.Length, publicKey.Length);
            return payload;
        }

        private static byte[] ReadPublicKeyFromHandshakePayload(byte[] payload)
        {
            if (payload.Length <= HandshakeMagic.Length ||
                !payload.AsSpan(0, HandshakeMagic.Length).SequenceEqual(HandshakeMagic))
            {
                throw new InvalidDataException("Chat暗号ハンドシェイク形式が不正です。");
            }

            byte[] publicKey = new byte[payload.Length - HandshakeMagic.Length];
            System.Buffer.BlockCopy(payload, HandshakeMagic.Length, publicKey, 0, publicKey.Length);
            return publicKey;
        }

        private void SetMessageCrypto(IMessageCrypto messageCrypto, bool ownsMessageCrypto)
        {
            if (_ownsMessageCrypto && _messageCrypto is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _messageCrypto = messageCrypto;
            _ownsMessageCrypto = ownsMessageCrypto;
        }

        private void LogException(Exception ex)
        {
            LogReceived?.Invoke("Chat TCPエラー");
            LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
            LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
            LogReceived?.Invoke($"Message: {ex.Message}");
        }

        private static long GetElapsedMilliseconds(long startedAt)
        {
            return (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }

        private sealed class PendingSend
        {
            public PendingSend(ChatMessage message)
            {
                Message = message;
            }

            public ChatMessage Message { get; }

            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
