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
                _listener = new StreamSocketListener();
                _listener.ConnectionReceived += OnConnectionReceived;

                await _listener.BindServiceNameAsync(port.ToString());

                LogReceived?.Invoke($"TCPサーバー待ち受け開始: Port={port}");
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

        private void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            LogReceived?.Invoke("TCP接続を受信");
            LogReceived?.Invoke($"RemoteAddress: {args.Socket.Information.RemoteAddress?.DisplayName}");
            LogReceived?.Invoke($"RemotePort: {args.Socket.Information.RemotePort}");

            ConnectionAccepted?.Invoke(args.Socket);
        }
    }
}
