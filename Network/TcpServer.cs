using System;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public class TcpServer
    {
        private StreamSocketListener? _listener;

        public event Action<string>? LogReceived;
        public event Action<string>? MessageReceived;

        public async Task StartAsync(int port)
        {
            if (_listener != null)
            {
                LogReceived?.Invoke("TCPサーバーはすでに開始されています");
                return;
            }

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;

            await _listener.BindServiceNameAsync(port.ToString());

            LogReceived?.Invoke($"TCPサーバー待ち受け開始: Port={port}");
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

            try
            {
                using var reader = new DataReader(args.Socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial
                };

                uint loaded = await reader.LoadAsync(1024);

                if (loaded == 0)
                {
                    LogReceived?.Invoke("受信データなし");
                    return;
                }

                string message = reader.ReadString(loaded);

                LogReceived?.Invoke($"TCP受信: {message}");
                MessageReceived?.Invoke(message);
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"TCP受信エラー: {ex.Message}");
            }
        }
    }
}