using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public sealed class ChatHistoryAttachment
    {
        public string? FileId { get; init; }
        public string? FileName { get; init; }
        public string? LocalFilePath { get; init; }
        public long? FileSize { get; init; }
    }

    public sealed class ChatHistoryService
    {
        private const int RecentMessageIdCapacity = 4_096;

        private readonly ChatRepository _repository;
        private readonly string _localPeerId;
        private readonly string _localPeerName;
        private readonly object _messageIdGate = new();
        private readonly HashSet<string> _recentMessageIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _recentMessageIdOrder = new();

        public ChatHistoryService(ChatRepository repository, string localPeerId, string localPeerName)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _localPeerId = string.IsNullOrWhiteSpace(localPeerId) ||
                           localPeerId.Length > 512 ||
                           localPeerId.Any(char.IsControl)
                ? throw new ArgumentException("A stable local peer identity is required.", nameof(localPeerId))
                : localPeerId.Trim();
            _localPeerName = localPeerName?.Trim() ?? "";
        }

        public ChatHistorySaveResult SaveMessage(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection,
            ChatHistoryAttachment? attachment = null)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!string.Equals(message.Type, "chat", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.SkippedNonChat,
                    MessageId = message.MessageId
                };
            }

            if (string.IsNullOrWhiteSpace(message.MessageId))
            {
                message.MessageId = Guid.NewGuid().ToString("N");
            }
            else
            {
                string candidate = message.MessageId.Trim();
                if (!Guid.TryParse(candidate, out Guid parsedMessageId))
                {
                    return new ChatHistorySaveResult
                    {
                        Status = ChatHistorySaveStatus.Failed,
                        MessageId = message.MessageId,
                        ErrorMessage = "The message identity is invalid."
                    };
                }

                message.MessageId = parsedMessageId.ToString("N");
            }

            if (!TryTrackMessageId(message.MessageId))
            {
                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.DuplicateMessageId,
                    MessageId = message.MessageId
                };
            }

            try
            {
                direct_module.Models.ChatMessage dbMessage =
                    ToDatabaseMessage(message, isOutgoing, peer, connection, attachment);
                bool inserted = _repository.TrySaveMessage(dbMessage);
                return new ChatHistorySaveResult
                {
                    Status = inserted
                        ? ChatHistorySaveStatus.Saved
                        : ChatHistorySaveStatus.DuplicateMessageId,
                    MessageId = message.MessageId
                };
            }
            catch (Exception ex)
            {
                UntrackMessageId(message.MessageId);
                return new ChatHistorySaveResult
                {
                    Status = ChatHistorySaveStatus.Failed,
                    MessageId = message.MessageId,
                    ErrorMessage = ex.Message
                };
            }
        }

        public Task<ChatHistorySaveResult> SaveMessageAsync(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection,
            ChatHistoryAttachment? attachment = null,
            CancellationToken cancellationToken = default) =>
            Task.Run(
                () => SaveMessage(message, isOutgoing, peer, connection, attachment),
                cancellationToken);

        public IReadOnlyList<direct_module.Models.ChatMessage> LoadConversation(
            string conversationId,
            int limit = ChatRepository.DefaultConversationLoadLimit) =>
            _repository.GetMessages(conversationId, limit);

        public IReadOnlyList<direct_module.Models.ChatMessage> LoadRecent(
            int limit = ChatRepository.DefaultGlobalLoadLimit) =>
            _repository.GetRecentMessages(limit);

        public Task<IReadOnlyList<direct_module.Models.ChatMessage>> LoadConversationAsync(
            string conversationId,
            int limit = ChatRepository.DefaultConversationLoadLimit,
            CancellationToken cancellationToken = default) =>
            Task.Run<IReadOnlyList<direct_module.Models.ChatMessage>>(
                () => _repository.GetMessages(conversationId, limit),
                cancellationToken);

        public string GetConversationId(PeerInfo? peer, ChatConnection? connection, bool isGroup)
        {
            if (isGroup)
            {
                return PeerIdentityService.DefaultGroupConversationId;
            }

            return GetHistoryConversationId(peer, connection, isOutgoing: false);
        }

        private direct_module.Models.ChatMessage ToDatabaseMessage(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection,
            ChatHistoryAttachment? attachment)
        {
            string peerId = GetHistoryPeerId(peer, connection, isOutgoing);
            string peerName = GetHistoryPeerName(peer, connection, isOutgoing);
            string conversationId = message.IsGroup
                ? PeerIdentityService.NormalizeConversationId(message.ConversationId, isGroup: true)
                : GetHistoryConversationId(peer, connection, isOutgoing);

            return new direct_module.Models.ChatMessage
            {
                MessageId = message.MessageId,
                MessageType = message.Type,
                ConversationId = conversationId,
                SenderId = isOutgoing ? _localPeerId : EmptyFallback(message.SenderId, peerId),
                SenderName = isOutgoing ? _localPeerName : EmptyFallback(message.SenderName, peerName),
                ReceiverId = message.IsGroup
                    ? conversationId
                    : isOutgoing ? peerId : _localPeerId,
                ReceiverName = message.IsGroup
                    ? "Group"
                    : isOutgoing ? peerName : _localPeerName,
                Message = message.Body,
                SendTime = HistoryTimestampPolicy.NormalizeForStorage(
                    message.SentAt,
                    isOutgoing,
                    DateTime.UtcNow),
                IsMine = isOutgoing,
                IsGroup = message.IsGroup,
                FileId = attachment?.FileId ?? message.FileId,
                FileName = attachment?.FileName ?? message.FileName,
                LocalFilePath = attachment?.LocalFilePath,
                FileSize = attachment?.FileSize ?? message.FileSize
            };
        }

        private static string GetHistoryConversationId(
            PeerInfo? peer,
            ChatConnection? connection,
            bool isOutgoing)
        {
            if (peer != null)
            {
                string peerIdentity = PeerIdentityService.GetConnectionId(peer);
                if (!string.IsNullOrWhiteSpace(peerIdentity))
                {
                    return peerIdentity;
                }
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerId))
            {
                return connection.PeerId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return $"ip:{connection.RemoteIpAddress.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(connection?.ShortSessionId))
            {
                return $"session:{connection.ShortSessionId.Trim()}";
            }

            return isOutgoing ? "broadcast:unknown" : "peer:unknown";
        }

        private static string GetHistoryPeerId(
            PeerInfo? peer,
            ChatConnection? connection,
            bool isOutgoing)
        {
            if (peer != null)
            {
                string peerIdentity = PeerIdentityService.GetConnectionId(peer);
                if (!string.IsNullOrWhiteSpace(peerIdentity))
                {
                    return peerIdentity;
                }
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerId))
            {
                return connection.PeerId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return $"ip:{connection.RemoteIpAddress.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(connection?.ShortSessionId))
            {
                return $"session:{connection.ShortSessionId.Trim()}";
            }

            return isOutgoing ? "broadcast:unknown" : "peer:unknown";
        }

        private static string GetHistoryPeerName(
            PeerInfo? peer,
            ChatConnection? connection,
            bool isOutgoing)
        {
            if (!string.IsNullOrWhiteSpace(peer?.DisplayName))
            {
                return peer.DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(connection?.PeerName))
            {
                return connection.PeerName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(connection?.RemoteIpAddress))
            {
                return connection.RemoteIpAddress.Trim();
            }

            return isOutgoing ? "Broadcast" : "Unknown";
        }

        private bool TryTrackMessageId(string messageId)
        {
            lock (_messageIdGate)
            {
                if (!_recentMessageIds.Add(messageId))
                {
                    return false;
                }

                _recentMessageIdOrder.Enqueue(messageId);
                while (_recentMessageIdOrder.Count > RecentMessageIdCapacity)
                {
                    string oldest = _recentMessageIdOrder.Dequeue();
                    if (!_recentMessageIdOrder.Contains(oldest))
                    {
                        _recentMessageIds.Remove(oldest);
                    }
                }

                return true;
            }
        }

        private void UntrackMessageId(string messageId)
        {
            lock (_messageIdGate)
            {
                _recentMessageIds.Remove(messageId);
                int count = _recentMessageIdOrder.Count;
                for (int index = 0; index < count; index++)
                {
                    string queued = _recentMessageIdOrder.Dequeue();
                    if (!string.Equals(queued, messageId, StringComparison.Ordinal))
                    {
                        _recentMessageIdOrder.Enqueue(queued);
                    }
                }
            }
        }

        private static string EmptyFallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
