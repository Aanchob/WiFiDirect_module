using System;

namespace direct_module.Network
{
    public class ChatMessage
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        public string SenderId { get; set; } = "";

        public string SenderName { get; set; } = "";

        public string Body { get; set; } = "";

        public DateTime SentAt { get; set; } = DateTime.Now;
    }
}
