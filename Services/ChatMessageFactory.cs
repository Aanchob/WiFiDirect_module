using direct_module.Network;

namespace direct_module.Services
{
    public sealed class ChatMessageFactory
    {
        private readonly string _senderId;
        private readonly string _senderName;
        private readonly string _shortSessionId;

        public ChatMessageFactory(string senderId, string senderName, string shortSessionId)
        {
            _senderId = senderId;
            _senderName = senderName;
            _shortSessionId = shortSessionId;
        }

        public ChatMessage CreateChat(string body, bool isGroup) => new()
        {
            Type = "chat",
            SenderId = _senderId,
            SenderName = _senderName,
            ShortSessionId = _shortSessionId,
            Body = body,
            IsGroup = isGroup,
            ConversationId = isGroup ? PeerIdentityService.DefaultGroupConversationId : ""
        };
    }
}
