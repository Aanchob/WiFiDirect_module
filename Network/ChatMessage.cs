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

        public string? FileId { get; set; }

        public string? FileName { get; set; }

        public long? FileSize { get; set; }

        public int? ChunkIndex { get; set; }

        public int? ChunkCount { get; set; }

        public string? ChunkBase64 { get; set; }

        public bool IsGroup { get; set; }

        public string ConversationId { get; set; } = "";

        public string? MimeType { get; set; }
    }
}
