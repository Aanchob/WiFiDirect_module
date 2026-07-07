using direct_module.Crypto;
using System;
using System.Diagnostics;
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

        public void AttachAcceptedSocket(StreamSocket socket)
        {
            Close();

            RemoteIpAddress = socket.Information.RemoteAddress?.DisplayName ?? "";
            PeerId = string.IsNullOrWhiteSpace(PeerId)
                ? $"{RemoteIpAddress}:{socket.Information.RemotePort}"
                : PeerId;
            PeerName = string.IsNullOrWhiteSpace(PeerName)
                ? RemoteIpAddress
                : PeerName;

            AttachSocket(socket);

            LogReceived?.Invoke("Chat TCP接続成功");
            LogReceived?.Invoke("Chat TCP受信側socketを保持しました");
            LogReceived?.Invoke($"RemoteAddress: {RemoteIpAddress}");
            StartReceiveLoop();
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

            await _sendLock.WaitAsync();

            try
            {
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
                LogReceived?.Invoke($"送信Bytes: {encryptedBytes.Length}");
                LogReceived?.Invoke($"Chat TCP送信完了 合計: {totalWatch.ElapsedMilliseconds}ms");
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

                    if (string.IsNullOrWhiteSpace(PeerName) && !string.IsNullOrWhiteSpace(message.SenderName))
                    {
                        PeerName = message.SenderName;
                    }

                    if (string.IsNullOrWhiteSpace(PeerId) && !string.IsNullOrWhiteSpace(message.SenderId))
                    {
                        PeerId = message.SenderId;
                    }

                    LogReceived?.Invoke("Chat TCP受信");
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
                Close();
            }
        }

        public void Close()
        {
            bool wasConnected = _isConnected;

            _isConnected = false;
            _isReceiveLoopStarted = false;
            IsPreparing = false;
            IsPingWaiting = false;
            IsHelloVerified = false;
            IsReady = false;

            _reader?.DetachStream();
            _writer?.DetachStream();

            _reader?.Dispose();
            _writer?.Dispose();
            _socket?.Dispose();

            _reader = null;
            _writer = null;
            _socket = null;

            if (wasConnected)
            {
                LogReceived?.Invoke("Chat TCP切断");
                Disconnected?.Invoke(this);
            }
        }

        private void AttachSocket(StreamSocket socket)
        {
            _socket = socket;
            _writer = new DataWriter(socket.OutputStream);
            LogReceived?.Invoke("DataWriter作成完了");
            _reader = new DataReader(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial
            };
            LogReceived?.Invoke("DataReader作成完了");
            _isConnected = true;
            _isReceiveLoopStarted = false;
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

        private void LogException(Exception ex)
        {
            LogReceived?.Invoke("Chat TCPエラー");
            LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
            LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
            LogReceived?.Invoke($"Message: {ex.Message}");
        }
    }
}
