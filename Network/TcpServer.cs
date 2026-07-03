using direct_module.Crypto;
using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public class TcpServer
    {
        private readonly IMessageCrypto _messageCrypto;
        private StreamSocketListener? _listener;

        public TcpServer()
            : this(new NoOpMessageCrypto())
        {
        }

        public TcpServer(IMessageCrypto messageCrypto)
        {
            _messageCrypto = messageCrypto;
        }

        public event Action<string>? LogReceived;
        public event Action<string>? MessageReceived;

        public bool IsStarted => _listener != null;

        public async Task StartAsync(int port)
        {
            if (_listener != null)
            {
                LogReceived?.Invoke("TCPサーバーはすでに開始されています");
                return;
            }

            try
            {
                _listener = new StreamSocketListener();
                _listener.ConnectionReceived += OnConnectionReceived;

                await _listener.BindServiceNameAsync(port.ToString());

                LogReceived?.Invoke($"TCPサーバー待ち受け開始: Port={port}");
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("TCPサーバー待ち受け開始失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (_listener == null)
            {
                LogReceived?.Invoke("TCPサーバーは開始されていません");
                return;
            }

            _listener.ConnectionReceived -= OnConnectionReceived;
            _listener.Dispose();
            _listener = null;

            LogReceived?.Invoke("TCPサーバー停止");
        }

        private async void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            LogReceived?.Invoke("TCP接続を受信");
            LogReceived?.Invoke($"RemoteAddress: {args.Socket.Information.RemoteAddress?.DisplayName}");
            LogReceived?.Invoke($"RemotePort: {args.Socket.Information.RemotePort}");

            try
            {
                using var reader = new DataReader(args.Socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial
                };

                uint loaded = await reader.LoadAsync(4096);

                if (loaded == 0)
                {
                    LogReceived?.Invoke("受信データなし");
                    return;
                }

                byte[] receivedBytes = new byte[loaded];
                reader.ReadBytes(receivedBytes);

                LogReceived?.Invoke($"受信Bytes: {receivedBytes.Length}");
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");

                byte[] plainBytes = _messageCrypto.Decrypt(receivedBytes);
                string message = Encoding.UTF8.GetString(plainBytes);

                LogReceived?.Invoke($"復号後Bytes: {plainBytes.Length}");
                LogReceived?.Invoke($"TCP受信: {message}");
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("TCP受信エラー");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }
    }
}
