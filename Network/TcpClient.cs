using direct_module.Crypto;
using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace direct_module.Network
{
    public class TcpClient
    {
        private readonly IMessageCrypto _messageCrypto;

        public TcpClient()
            : this(new NoOpMessageCrypto())
        {
        }

        public TcpClient(IMessageCrypto messageCrypto)
        {
            _messageCrypto = messageCrypto;
        }

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
                LogReceived?.Invoke($"MessageCrypto: {_messageCrypto.GetType().Name}");

                byte[] plainBytes = Encoding.UTF8.GetBytes(message);
                byte[] sendBytes = _messageCrypto.Encrypt(plainBytes);

                LogReceived?.Invoke($"平文Bytes: {plainBytes.Length}");
                LogReceived?.Invoke($"送信Bytes: {sendBytes.Length}");

                using var socket = new StreamSocket();

                await socket.ConnectAsync(
                    new HostName(ipAddress),
                    port.ToString()
                );

                using var writer = new DataWriter(socket.OutputStream);

                writer.WriteBytes(sendBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();

                LogReceived?.Invoke($"TCP送信成功: {ipAddress}:{port}");
                LogReceived?.Invoke($"送信内容: {message}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke("TCP送信失敗");
                LogReceived?.Invoke($"例外名: {ex.GetType().Name}");
                LogReceived?.Invoke($"HResult: 0x{ex.HResult:X8}");
                LogReceived?.Invoke($"Message: {ex.Message}");
            }
        }
    }
}
