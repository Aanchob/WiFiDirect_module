using System;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public class TcpClient
    {
        public event Action<string>? LogReceived;

        public async Task SendAsync(string ipAddress, int port, string message)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                LogReceived?.Invoke("TCP送信失敗");
                LogReceived?.Invoke("Message: IPアドレスが空です");
                return;
            }

            if (port <= 0)
            {
                LogReceived?.Invoke("TCP送信失敗");
                LogReceived?.Invoke("Message: ポート番号が不正です");
                return;
            }

            try
            {
                LogReceived?.Invoke("TCP送信開始");
                LogReceived?.Invoke($"送信先IP: {ipAddress}");
                LogReceived?.Invoke($"送信先Port: {port}");
                LogReceived?.Invoke($"送信内容: {message}");

                using var socket = new StreamSocket();

                await socket.ConnectAsync(
                    new HostName(ipAddress),
                    port.ToString()
                );

                using var writer = new DataWriter(socket.OutputStream);

                writer.WriteString(message);
                await writer.StoreAsync();
                await writer.FlushAsync();

                LogReceived?.Invoke($"TCP送信成功: {ipAddress}:{port}");
                LogReceived?.Invoke($"送信内容: {message}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("TCP送信失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }
    }
}
