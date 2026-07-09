using System;

namespace direct_module.Models
{
    public class ChatMessage
    {
        public int Id { get; set; }

        public string ConversationId { get; set; } = "";

        public string SenderId { get; set; } = "";
        public string SenderName { get; set; } = "";

        public string ReceiverId { get; set; } = "";
        public string ReceiverName { get; set; } = "";

        public string Message { get; set; } = "";

        public DateTime SendTime { get; set; }

        public bool IsMine { get; set; }

        public string MessageType { get; set; } = "chat";
        public string? FileId { get; set; }
        public string? FileName { get; set; }
        public long FileSize { get; set; }
        public string? LocalFilePath { get; set; }
        public string? MimeType { get; set; }
        public bool IsGroup { get; set; }
    }
}