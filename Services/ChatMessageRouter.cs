using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Network;

namespace direct_module.Services
{
    public sealed class ChatMessageRouter
    {
        private readonly ChatConnectionManager _connections;

        public ChatMessageRouter(ChatConnectionManager connections) => _connections = connections;

        public async Task<BroadcastResult?> SendAsync(
            ChatMessage message,
            bool isGroup,
            ChatConnection? directConnection,
            CancellationToken cancellationToken = default)
        {
            if (!isGroup)
            {
                if (directConnection == null || !directConnection.IsConnected || !directConnection.IsReady)
                    throw new InvalidOperationException("The destination connection is not ready.");

                await directConnection.SendAsync(message, cancellationToken);
                return null;
            }

            if (!_connections.Connections.Any(connection => connection.IsConnected && connection.IsReady))
                throw new InvalidOperationException("There are no ready group recipients.");

            return await _connections.BroadcastAsync(message, cancellationToken);
        }
    }
}
