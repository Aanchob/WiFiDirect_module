using System;
using System.Threading.Tasks;
using Windows.Networking.Sockets;

namespace direct_module.Network
{
    public class TcpServer
    {
        private StreamSocketListener? _listener;

        public event Action<string>? LogReceived;
        public event Action<StreamSocket>? ConnectionAccepted;

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
                LogReceived?.Invoke("Chat TCP Server待ち受け開始");
                LogReceived?.Invoke($"Listen Port: {port}");

                _listener = new StreamSocketListener();
                _listener.ConnectionReceived += OnConnectionReceived;

                await _listener.BindServiceNameAsync(port.ToString());

                LogReceived?.Invoke($"TCPサーバー待ち受け開始: Port={port}");
                LogReceived?.Invoke("Chat TCP接続待機中");
                LogReceived?.Invoke("Chat TCP複数接続待機中");
            }
            catch (Exception ex)
            {
                if (_listener != null)
                {
                    _listener.ConnectionReceived -= OnConnectionReceived;
                    _listener.Dispose();
                    _listener = null;
                }

                LogReceived?.Invoke("TCPサーバー待ち受け開始失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }

        public async Task RestartAsync(int port, string reason)
        {
            LogReceived?.Invoke($"TCPサーバー再バインド開始: Port={port}, Reason={reason}");

            if (_listener != null)
            {
                Stop();
            }

            await StartAsync(port);
            LogReceived?.Invoke($"TCPサーバー再バインド完了: Port={port}, Reason={reason}");
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

        private void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            LogReceived?.Invoke("Chat TCP接続を受信");
            LogReceived?.Invoke($"RemoteAddress: {args.Socket.Information.RemoteAddress?.DisplayName}");
            LogReceived?.Invoke($"RemotePort: {args.Socket.Information.RemotePort}");
            LogReceived?.Invoke("Chat TCP受信側Connection作成");

            ConnectionAccepted?.Invoke(args.Socket);
        }
    }
}
