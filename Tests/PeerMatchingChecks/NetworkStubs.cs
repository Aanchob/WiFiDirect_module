namespace direct_module.Network
{
    public sealed class ChatMessage
    {
        public string ShortSessionId { get; set; } = "";

        public string SenderId { get; set; } = "";
    }

    public sealed class ChatConnection
    {
        public string PeerId { get; set; } = "";

        public string ShortSessionId { get; set; } = "";

        public string RemoteIpAddress { get; set; } = "";
    }
}
