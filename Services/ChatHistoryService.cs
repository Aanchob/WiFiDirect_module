using System;
using System.Collections.Generic;
using direct_module.Database;
using direct_module.Network;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public enum ChatHistorySaveStatus
    {
        SkippedNonChat,
        Saved,
        DuplicateMessageId,
        Failed
    }

    public sealed class ChatHistorySaveResult
    {
        public ChatHistorySaveStatus Status { get; init; }

        public string MessageId { get; init; } = "";

        public string ErrorMessage { get; init; } = "";
    }

    public sealed class ChatHistoryService
    {
        private readonly ChatRepository _repository;
        private readonly string _localPeerId;
        private readonly string _localPeerName;
        private readonly HashSet<string> _savedMessageIds = new(StringComparer.OrdinalIgnoreCase);

        public ChatHistoryService(ChatRepository repository, string localPeerId, string localPeerName)
        {
            _repository = repository;
            _localPeerId = localPeerId;
            _localPeerName = localPeerName;
        }

        public ChatHistorySaveResult SaveMessage(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection)
        {
            if (!string.Equals(message.Type, "chat", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.SkippedNonChat,
                    MessageId = message.MessageId
                };
            }

            if (!string.IsNullOrWhiteSpace(message.MessageId) && !_savedMessageIds.Add(message.MessageId))
            {
                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.DuplicateMessageId,
                    MessageId = message.MessageId
                };
            }

            try
            {
                direct_module.Models.ChatMessage dbMessage = ToDatabaseMessage(message, isOutgoing, peer, connection);
                _repository.SaveMessage(dbMessage);

                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.Saved,
                    MessageId = message.MessageId
                };
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(message.MessageId))
                {
                    _savedMessageIds.Remove(message.MessageId);
                }

                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.Failed,
                    MessageId = message.MessageId,
                    ErrorMessage = ex.Message
                };
            }
        }

        private direct_module.Models.ChatMessage ToDatabaseMessage(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection)
        {
            string peerId = GetHistoryPeerId(peer, connection, isOutgoing);
            string peerName = GetHistoryPeerName(peer, connection, isOutgoing);

            return new direct_module.Models.ChatMessage
            {
                ConversationId = GetHistoryConversationId(peer, connection, isOutgoing),
                SenderId = isOutgoing ? _localPeerId : message.SenderId,
                SenderName = isOutgoing ? _localPeerName : message.SenderName,
                ReceiverId = isOutgoing ? peerId : _localPeerId,
                ReceiverName = isOutgoing ? peerName : _localPeerName,
                Message = message.Body,
                SendTime = message.SentAt,
                IsMine = isOutgoing
            };
        }

        private static string GetHistoryConversationId(PeerInfo? peer, ChatConnection? connection, bool isOutgoing)
        {
            if (peer != null)
            {
                return GetPeerConnectionId(peer);
            }

            if (!string.IsNullOrWhiteSpace(connection?.ShortSessionId))
            {
                return connection.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerId))
            {
                return connection.PeerId;
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return isOutgoing ? "broadcast" : "unknown";
        }

        private static string GetHistoryPeerId(PeerInfo? peer, ChatConnection? connection, bool isOutgoing)
        {
            if (peer != null)
            {
                return GetPeerConnectionId(peer);
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerId))
            {
                return connection.PeerId;
            }

            if (!string.IsNullOrWhiteSpace(connection?.ShortSessionId))
            {
                return connection.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return isOutgoing ? "broadcast" : "unknown";
        }

        private static string GetHistoryPeerName(PeerInfo? peer, ChatConnection? connection, bool isOutgoing)
        {
            if (!string.IsNullOrWhiteSpace(peer?.DisplayName))
            {
                return peer.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerName))
            {
                return connection.PeerName;
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return connection.RemoteIpAddress;
            }

            return isOutgoing ? "Broadcast" : "Unknown";
        }

        private static string GetPeerConnectionId(PeerInfo peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.ShortSessionId))
            {
                return peer.ShortSessionId;
            }

            if (!string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                return peer.DeviceId;
            }

            if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                return peer.RemoteIpAddress;
            }

            return peer.DisplayName;
        }
    }
}
