using System;
using System.Threading;
using direct_module.Network;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private async System.Threading.Tasks.Task HandlePingMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            try
            {
                EnqueueLog($"PING受信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);

                var pong = new ChatMessage
                {
                    Type = "pong",
                    SenderId = LocalPeerId,
                    SenderName = Environment.MachineName,
                    ShortSessionId = GetLocalShortSessionId(),
                    Body = ""
                };

                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    sourceConnection.LifetimeToken,
                    _windowLifetimeCancellation.Token);
                await sourceConnection.SendAsync(pong, cancellation.Token);
                EnqueueLog($"PONG送信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);
            }
            catch (OperationCanceledException) when (
                _windowLifetimeCancellation.IsCancellationRequested ||
                sourceConnection.LifetimeToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                EnqueueLog($"PONG送信失敗: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Error);
                EnqueueLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                EnqueueLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                EnqueueLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private void HandlePongMessage(ChatMessage message, ChatConnection sourceConnection)
        {
            sourceConnection.LastPongAt = DateTime.UtcNow;
            sourceConnection.LastResponseAt = sourceConnection.LastPongAt;
            sourceConnection.IsPingWaiting = false;

            EnqueueLog($"PONG受信: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Debug);
            EnqueueLog($"接続確認成功: Peer={GetConnectionPeerName(sourceConnection)}", LogLevel.Success);
        }

        private async System.Threading.Tasks.Task SendPingAfterHelloAsync(ChatConnection connection)
        {
            try
            {
                AddLog($"PING送信: Peer={GetConnectionPeerName(connection)}", LogLevel.Debug);
                await connection.SendPingAsync(LocalPeerId, Environment.MachineName, GetLocalShortSessionId());
            }
            catch (Exception ex)
            {
                connection.IsPingWaiting = false;
                AddLog($"PING送信失敗: Peer={GetConnectionPeerName(connection)}", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
        }

        private static string GetConnectionPeerName(ChatConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.PeerName))
            {
                return connection.PeerName;
            }

            if (!string.IsNullOrWhiteSpace(connection.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return connection.PeerId;
        }
    }
}
