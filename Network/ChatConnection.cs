using direct_module.Crypto;
using System;
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
        private readonly IMessageCrypto _messageCrypto;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private StreamSocket? _socket;
        private DataWriter? _writer;
        private DataReader? _reader;
        private bool _isConnected;
        private bool _isReceiveLoopStarted;

        public ChatConnection()
            : this(new NoOpMessageCrypto())
        {
        }

        public ChatConnection(IMessageCrypto messageCrypto)
        {
            _messageCrypto = messageCrypto;
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
                LogReceived?.Invoke("Chat TCP接続開始");
                LogReceived?.Invoke($"接続先IP: {ipAddress}");
                LogReceived?.Invoke($"接続先Port: {port}");

                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName(ipAddress), port.ToString());

                AttachSocket(socket);

                LogReceived?.Invoke("Chat TCP接続成功");
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
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
            Close();
            AttachSocket(socket);
            LogReceived?.Invoke("Chat TCP接続成功");
            LogReceived?.Invoke("Chat TCP受信側socketを保持しました");
            LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
            StartReceiveLoop();
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
                LogReceived?.Invoke("Chat TCP送信開始");
                LogReceived?.Invoke($"送信内容: {message}");

                byte[] plainBytes = Encoding.UTF8.GetBytes(message);
                byte[] encryptedBytes = _messageCrypto.Encrypt(plainBytes);

                _writer.WriteInt32(encryptedBytes.Length);
                _writer.WriteBytes(encryptedBytes);
                await _writer.StoreAsync();
                await _writer.FlushAsync();

                LogReceived?.Invoke("Chat TCP送信成功");
                LogReceived?.Invoke($"平文Bytes: {plainBytes.Length}");
                LogReceived?.Invoke($"送信Bytes: {encryptedBytes.Length}");
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

                    if (length <= 0 || length > 1024 * 1024)
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
                    LogReceived?.Invoke($"受信内容: {message}");
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

        private void LogException(Exception ex)
        {
            LogReceived?.Invoke("Chat TCPエラー");
            LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
            LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
            LogReceived?.Invoke($"Message: {ex.Message}");
        }
    }
}
