using System;
using System.Linq;
using System.Threading.Tasks;
using direct_module.Network;

namespace direct_module.Services
{
    public sealed class ChatMessageRouter
    {
        private readonly ChatConnectionManager _connections;

        public ChatMessageRouter(ChatConnectionManager connections) => _connections = connections;

        public async Task SendAsync(ChatMessage message, bool isGroup, bool localIsHost, ChatConnection? directConnection)
        {
            if (!isGroup)
            {
                if (directConnection == null || !directConnection.IsConnected || !directConnection.IsReady)
                    throw new InvalidOperationException("The destination connection is not ready.");

                await directConnection.SendAsync(message);
                return;
            }

            if (localIsHost)
            {
                await _connections.BroadcastAsync(message);
                return;
            }

            ChatConnection? host = _connections.Connections
                .FirstOrDefault(connection => connection.IsConnected && connection.IsReady);
            if (host == null)
                throw new InvalidOperationException("There is no ready connection to the host.");

            await host.SendAsync(message);
        }
    }
}
