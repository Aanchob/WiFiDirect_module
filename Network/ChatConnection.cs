using direct_module.Crypto;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public class ChatConnection
    {
        private const int MaxMessageFrameLength = 1024 * 1024;
        private const int MaxHandshakeFrameLength = 4096;
        private static readonly byte[] HandshakeMagic = Encoding.ASCII.GetBytes("NOVA-ECDH-1|");

        private readonly bool _useEcdhHandshake;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private IMessageCrypto _messageCrypto;
        private bool _ownsMessageCrypto;
        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;
        private bool _isConnected;
        private bool _isReceiveLoopStarted;

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

        public event Action<string>? LogReceived;
        public event Action<string>? MessageReceived;
        public event Action? Closed;

        public bool IsConnected => _isConnected;

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

            try
            {
                long startedAt = Stopwatch.GetTimestamp();

                LogReceived?.Invoke("Chat TCP接続開始");
                LogReceived?.Invoke($"接続先IP: {ipAddress}");
                LogReceived?.Invoke($"接続先Port: {port}");

                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName(ipAddress), port.ToString());

                AttachSocket(socket);
                await InitializeCryptoAsync(isInitiator: true);

                LogReceived?.Invoke("Chat TCP接続成功");
                LogReceived?.Invoke($"Chat TCP接続時間: {GetElapsedMilliseconds(startedAt)}ms");
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
                AttachSocket(socket);
                await InitializeCryptoAsync(isInitiator: false);

                LogReceived?.Invoke("Chat TCP接続成功");
                LogReceived?.Invoke("Chat TCP受信側socketを保持しました");
                LogReceived?.Invoke($"Chat TCP受信側準備時間: {GetElapsedMilliseconds(startedAt)}ms");
                StartReceiveLoop();
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
        }

        public async Task SendAsync(string message)
        {
            if (!_isConnected || _writer == null)
            {
                LogReceived?.Invoke("Chat TCPエラー");
                LogReceived?.Invoke("Message: Chat TCPが未接続です");
                return;
            }

            await _sendLock.WaitAsync();

            try
            {
                long startedAt = Stopwatch.GetTimestamp();

                LogReceived?.Invoke("Chat TCP送信開始");

                byte[] plainBytes = Encoding.UTF8.GetBytes(message);
                byte[] encryptedBytes = _messageCrypto.Encrypt(plainBytes);

                _writer.WriteInt32(encryptedBytes.Length);
                _writer.WriteBytes(encryptedBytes);
                await _writer.StoreAsync();

                LogReceived?.Invoke("Chat TCP送信成功");
                LogReceived?.Invoke($"平文Bytes: {plainBytes.Length}");
                LogReceived?.Invoke($"送信Bytes: {encryptedBytes.Length}");
                LogReceived?.Invoke($"Chat TCP送信時間: {GetElapsedMilliseconds(startedAt)}ms");
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void StartReceiveLoop()
        {
            if (!_isConnected || _reader == null)
            {
                return;
            }

            if (_isReceiveLoopStarted)
            {
                return;
            }

            _isReceiveLoopStarted = true;
            LogReceived?.Invoke("Chat TCP受信ループ開始");
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
                    uint headerLoaded = await _reader.LoadAsync(sizeof(int));

                    if (headerLoaded < sizeof(int))
                    {
                        break;
                    }

                    int length = _reader.ReadInt32();

                    if (length <= 0 || length > MaxMessageFrameLength)
                    {
                        LogReceived?.Invoke("Chat TCPエラー");
                        LogReceived?.Invoke($"Message: 不正なメッセージ長です Length={length}");
                        break;
                    }

                    uint bodyLoaded = await _reader.LoadAsync((uint)length);

                    if (bodyLoaded < length)
                    {
                        break;
                    }

                    byte[] encryptedBytes = new byte[length];
                    _reader.ReadBytes(encryptedBytes);

                    byte[] plainBytes = _messageCrypto.Decrypt(encryptedBytes);
                    string message = Encoding.UTF8.GetString(plainBytes);

                    LogReceived?.Invoke("Chat TCP受信");
                    LogReceived?.Invoke($"受信Bytes: {encryptedBytes.Length}");
                    LogReceived?.Invoke($"復号後Bytes: {plainBytes.Length}");
                    MessageReceived?.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            bool wasConnected = _isConnected;
            _isConnected = false;
            _isReceiveLoopStarted = false;

            _writer?.DetachStream();
            _reader?.DetachStream();
            _writer?.Dispose();
            _reader?.Dispose();
            _socket?.Dispose();

            _writer = null;
            _reader = null;
            _socket = null;

            if (_useEcdhHandshake && _messageCrypto is not NoOpMessageCrypto)
            {
                SetMessageCrypto(new NoOpMessageCrypto(), ownsMessageCrypto: true);
            }

            if (wasConnected)
            {
                LogReceived?.Invoke("Chat TCP切断");
                Closed?.Invoke();
            }
        }

        private void AttachSocket(StreamSocket socket)
        {
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream)
            {
                ByteOrder = ByteOrder.LittleEndian
            };
            _reader = new DataReader(socket.InputStream)
            {
                ByteOrder = ByteOrder.LittleEndian,
                InputStreamOptions = InputStreamOptions.None
            };
            _isConnected = true;
            _isReceiveLoopStarted = false;
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
        }

        private async Task<byte[]> ReadHandshakePublicKeyAsync()
        {
            if (_reader == null)
            {
                throw new InvalidOperationException("Chat TCP reader is not ready.");
            }

            uint headerLoaded = await _reader.LoadAsync(sizeof(int));

            if (headerLoaded < sizeof(int))
            {
                throw new EndOfStreamException("Chat暗号ハンドシェイクを読み込めませんでした。");
            }

            int length = _reader.ReadInt32();

            if (length <= HandshakeMagic.Length || length > MaxHandshakeFrameLength)
            {
                throw new InvalidDataException($"Chat暗号ハンドシェイク長が不正です Length={length}");
            }

            uint bodyLoaded = await _reader.LoadAsync((uint)length);

            if (bodyLoaded < length)
            {
                throw new EndOfStreamException("Chat暗号ハンドシェイク本文を読み込めませんでした。");
            }

            byte[] payload = new byte[length];
            _reader.ReadBytes(payload);

            return ReadPublicKeyFromHandshakePayload(payload);
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
    }
}
