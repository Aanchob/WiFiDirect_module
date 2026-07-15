using System;

namespace direct_module.Network
{
    public class ChatMessage
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        public string Type { get; set; } = "chat";

        public string SenderId { get; set; } = "";

        public string SenderName { get; set; } = "";

        public string ShortSessionId { get; set; } = "";

        public string Body { get; set; } = "";

        public DateTime SentAt { get; set; } = DateTime.Now;

        public bool IsGroup { get; set; }

        public string ConversationId { get; set; } = "";

        public string? FileId { get; set; }

        public string? FileName { get; set; }

        public long? FileSize { get; set; }

        public int? ChunkIndex { get; set; }

        public int? ChunkCount { get; set; }

        public string? ChunkBase64 { get; set; }

        /// <summary>
        /// Stable peer identity that should consume a file acknowledgement. This is
        /// the SenderId of the original file-transfer message and is empty for every
        /// message type except file_ack.
        /// </summary>
        public string AcknowledgementTargetId { get; set; } = "";

        /// <summary>
        /// Stable identity of the peer that produced a file acknowledgement. It must
        /// equal SenderId. Group owners aggregate downstream acknowledgements and
        /// produce a new direct acknowledgement rather than relaying a raw ACK.
        /// </summary>
        public string AcknowledgementSenderId { get; set; } = "";

        /// <summary>
        /// Identity of the host that relayed a group message. These fields are empty
        /// for an original/direct message and are required when HopCount is greater
        /// than zero. SenderId/SenderName continue to identify the original author.
        /// </summary>
        public string RelaySenderId { get; set; } = "";

        public string RelaySenderName { get; set; } = "";

        public string RelayShortSessionId { get; set; } = "";

        public int HopCount { get; set; }

        public ChatMessage CreateRelayEnvelope(
            string relaySenderId,
            string relaySenderName,
            string relayShortSessionId)
        {
            if (!IsGroup || HopCount != 0)
                throw new InvalidOperationException("Only an original group message can be relayed.");
            if (string.Equals(Type, "file_ack", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File acknowledgements must be aggregated, not relayed.");
            if (string.IsNullOrWhiteSpace(relaySenderId) || string.IsNullOrWhiteSpace(relayShortSessionId))
                throw new ArgumentException("A stable relay identity and short session identity are required.");

            return new ChatMessage
            {
                MessageId = MessageId,
                Type = Type,
                SenderId = SenderId,
                SenderName = SenderName,
                ShortSessionId = ShortSessionId,
                Body = Body,
                SentAt = SentAt,
                IsGroup = true,
                ConversationId = ConversationId,
                FileId = FileId,
                FileName = FileName,
                FileSize = FileSize,
                ChunkIndex = ChunkIndex,
                ChunkCount = ChunkCount,
                ChunkBase64 = ChunkBase64,
                RelaySenderId = relaySenderId.Trim(),
                RelaySenderName = relaySenderName?.Trim() ?? "",
                RelayShortSessionId = relayShortSessionId.Trim(),
                HopCount = 1
            };
        }
    }
}
