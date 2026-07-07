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
    }
}