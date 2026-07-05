using direct_module.Crypto;
using System;
using System.Diagnostics;
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
        private const uint MaxMessageBytes = 1024 * 1024;

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
                LogReceived?.Invoke("Chat TCP ConnectAsyncスキップ: すでに接続済み");
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

                var stepWatch = Stopwatch.StartNew();
                LogReceived?.Invoke("Socket作成開始");
                var socket = new StreamSocket();
                LogReceived?.Invoke($"Socket作成完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                LogReceived?.Invoke("Socket.ConnectAsync開始");
                await socket.ConnectAsync(new HostName(ipAddress), port.ToString());
                LogReceived?.Invoke($"Socket.ConnectAsync完了: {stepWatch.ElapsedMilliseconds}ms");

                AttachSocket(socket);

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

        public void AttachAcceptedSocket(StreamSocket socket)
        {
            var totalWatch = Stopwatch.StartNew();

            Close();
            AttachSocket(socket);

            LogReceived?.Invoke("Chat TCP接続成功");
            LogReceived?.Invoke("Chat TCP受信側socketを保持しました");
            LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
            LogReceived?.Invoke($"Chat TCP受信Connection作成完了: {totalWatch.ElapsedMilliseconds}ms");
            StartReceiveLoop();
        }

        public async Task SendAsync(string message)
        {
            var totalWatch = Stopwatch.StartNew();

            LogReceived?.Invoke("Chat TCP SendAsync開始");
            LogReceived?.Invoke($"送信メッセージ文字数: {message.Length}");
            LogReceived?.Invoke($"接続状態 IsConnected={_isConnected}");
            LogReceived?.Invoke($"SendAsync内でConnectが必要か: {!_isConnected}");

            if (!_isConnected || _writer == null)
            {
                LogReceived?.Invoke("Chat TCPエラー");
                LogReceived?.Invoke("Message: Chat TCPが未接続です");
                return;
            }

            var lockWatch = Stopwatch.StartNew();
            LogReceived?.Invoke("送信ロック待機開始");
            await _sendLock.WaitAsync();
            LogReceived?.Invoke($"送信ロック取得: {lockWatch.ElapsedMilliseconds}ms");

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(message);
                byte[] encryptedBytes = _messageCrypto.Encrypt(plainBytes);

                LogReceived?.Invoke($"平文Bytes: {plainBytes.Length}");
                LogReceived?.Invoke($"暗号化後Bytes: {encryptedBytes.Length}");
                LogReceived?.Invoke($"送信フレームBytes: {sizeof(uint) + encryptedBytes.Length}");

                var stepWatch = Stopwatch.StartNew();
                LogReceived?.Invoke("WriteUInt32開始");
                _writer.WriteUInt32((uint)encryptedBytes.Length);
                LogReceived?.Invoke($"WriteUInt32完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                LogReceived?.Invoke("WriteBytes開始");
                _writer.WriteBytes(encryptedBytes);
                LogReceived?.Invoke($"WriteBytes完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                LogReceived?.Invoke("StoreAsync開始");
                await _writer.StoreAsync();
                LogReceived?.Invoke($"StoreAsync完了: {stepWatch.ElapsedMilliseconds}ms");

                stepWatch.Restart();
                LogReceived?.Invoke("FlushAsync開始");
                await _writer.FlushAsync();
                LogReceived?.Invoke($"FlushAsync完了: {stepWatch.ElapsedMilliseconds}ms");

                LogReceived?.Invoke("Chat TCP送信成功");
                LogReceived?.Invoke($"Chat TCP SendAsync完了 合計: {totalWatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                LogException(ex);
                Close();
            }
            finally
            {
                _sendLock.Release();
                LogReceived?.Invoke("送信ロック解放");
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
                    var messageWatch = Stopwatch.StartNew();
                    var stepWatch = Stopwatch.StartNew();

                    LogReceived?.Invoke("length読み取り待機開始");
                    bool lengthRead = await LoadExactAsync(sizeof(uint), "length");
                    LogReceived?.Invoke($"length読み取り完了: {stepWatch.ElapsedMilliseconds}ms");

                    if (!lengthRead)
                    {
                        LogReceived?.Invoke("Chat TCP切断: lengthを読み取れませんでした");
                        break;
                    }

                    uint messageLength = _reader.ReadUInt32();
                    LogReceived?.Invoke($"受信予定Bytes: {messageLength}");

                    if (messageLength == 0 || messageLength > MaxMessageBytes)
                    {
                        LogReceived?.Invoke($"不正なメッセージ長: {messageLength}");
                        break;
                    }

                    stepWatch.Restart();
                    LogReceived?.Invoke("本文読み取り開始");
                    bool bodyRead = await LoadExactAsync(messageLength, "本文");
                    LogReceived?.Invoke($"本文読み取り完了: {stepWatch.ElapsedMilliseconds}ms");

                    if (!bodyRead)
                    {
                        LogReceived?.Invoke($"本文不足: {_reader.UnconsumedBufferLength}/{messageLength}");
                        break;
                    }

                    byte[] encryptedBytes = new byte[messageLength];
                    _reader.ReadBytes(encryptedBytes);

                    stepWatch.Restart();
                    LogReceived?.Invoke("Decrypt開始");
                    byte[] plainBytes = _messageCrypto.Decrypt(encryptedBytes);
                    LogReceived?.Invoke($"Decrypt完了: {stepWatch.ElapsedMilliseconds}ms");

                    string message = Encoding.UTF8.GetString(plainBytes);

                    LogReceived?.Invoke("Chat TCP受信");
                    LogReceived?.Invoke($"受信Bytes: {encryptedBytes.Length}");
                    LogReceived?.Invoke($"復号後Bytes: {plainBytes.Length}");
                    LogReceived?.Invoke($"受信メッセージ: {message}");
                    LogReceived?.Invoke($"Chat TCP受信完了 合計: {messageWatch.ElapsedMilliseconds}ms");
                    MessageReceived?.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
            finally
            {
                LogReceived?.Invoke("Chat TCP ReceiveLoop終了");
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
            var stepWatch = Stopwatch.StartNew();
            LogReceived?.Invoke("DataWriter作成開始");
            _writer = new DataWriter(socket.OutputStream)
            {
                ByteOrder = ByteOrder.LittleEndian
            };
            LogReceived?.Invoke($"DataWriter作成完了: {stepWatch.ElapsedMilliseconds}ms");

            stepWatch.Restart();
            LogReceived?.Invoke("DataReader作成開始");
            _reader = new DataReader(socket.InputStream)
            {
                ByteOrder = ByteOrder.LittleEndian,
                InputStreamOptions = InputStreamOptions.None
            };
            LogReceived?.Invoke($"DataReader作成完了: {stepWatch.ElapsedMilliseconds}ms");

            _socket = socket;
            _isConnected = true;
            _isReceiveLoopStarted = false;
        }

        private async Task<bool> LoadExactAsync(uint byteCount, string label)
        {
            while (_reader != null && _reader.UnconsumedBufferLength < byteCount)
            {
                uint remaining = byteCount - _reader.UnconsumedBufferLength;
                uint loaded = await _reader.LoadAsync(remaining);

                if (loaded == 0)
                {
                    LogReceived?.Invoke($"{label}読み取り不足: {_reader.UnconsumedBufferLength}/{byteCount}");
                    return false;
                }
            }

            return _reader != null && _reader.UnconsumedBufferLength >= byteCount;
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
