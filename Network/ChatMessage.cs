using System;
using System.Collections.Generic;

namespace direct_module.Network
{
    public class ChatMessage
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        public string Type { get; set; } = "chat";

        public string SenderId { get; set; } = "";

        public string SenderName { get; set; } = "";

        public string ShortSessionId { get; set; } = "";

        public string ReceiverId { get; set; } = "";

        public string ReceiverName { get; set; } = "";

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

        public List<ChatParticipant>? Participants { get; set; }
    }

    public sealed class ChatParticipant
    {
        public string PeerId { get; set; } = "";

        public string PeerName { get; set; } = "";

        public string ShortSessionId { get; set; } = "";
    }
}
