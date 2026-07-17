using direct_module.Discovery;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private async System.Threading.Tasks.Task ProcessFileTransferMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            await _fileTransferReceiveGate.WaitAsync();
            try
            {
                await HandleFileTransferMessageAsync(message, sourceConnection);
            }
            finally
            {
                _fileTransferReceiveGate.Release();
            }
        }

        private async System.Threading.Tasks.Task HandleFileTransferMessageAsync(ChatMessage message, ChatConnection sourceConnection)
        {
            try
            {
                if (_chatRole == ChatRole.Host &&
                    !message.IsGroup &&
                    !IsMessageForLocalPeer(message))
                {
                    await RelayDirectMessageAsync(message, sourceConnection);
                    return;
                }

                if (_chatRole == ChatRole.Host && message.IsGroup)
                {
                    await _chatConnectionManager.BroadcastExceptAsync(message, sourceConnection);
                }

                FileTransferDisplayResult? displayResult = null;

                switch (message.Type.ToLowerInvariant())
                {
                    case "file_start":
                        displayResult = await _fileTransferService.HandleFileStartAsync(message);
                        break;
                    case "file_chunk":
                        await _fileTransferService.HandleFileChunkAsync(message);
                        break;
                    case "file_end":
                        displayResult = await _fileTransferService.HandleFileEndAsync(message);
                        break;
                }

                if (displayResult != null && !string.IsNullOrWhiteSpace(displayResult.Message))
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AddFileChatMessage(
                            $"{message.SenderName}: {displayResult.Message}",
                            displayResult.FileName,
                            displayResult.LocalFilePath,
                            GetConversationIdForMessage(message, sourceConnection));

                        if (string.Equals(message.Type, "file_end", StringComparison.OrdinalIgnoreCase))
                        {
                            var historyMessage = new ChatMessage
                            {
                                Type = "chat",
                                SenderId = message.SenderId,
                                SenderName = message.SenderName,
                                ShortSessionId = message.ShortSessionId,
                                Body = $"[ファイル] {message.FileName}",
                                IsGroup = message.IsGroup,
                                ConversationId = message.ConversationId
                            };

                            PeerInfo? historyPeer = !string.IsNullOrWhiteSpace(message.SenderId)
                                ? FindPeerByPeerId(message.SenderId)
                                : null;
                            SaveChatMessageSafely(
                                historyMessage,
                                false,
                                historyPeer ?? FindPeerForConnection(sourceConnection),
                                sourceConnection);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                EnqueueLog($"ファイル受信処理に失敗しました: {ex.Message}", LogLevel.Error);
            }
        }
    }
}
