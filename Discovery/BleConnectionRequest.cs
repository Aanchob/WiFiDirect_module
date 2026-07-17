namespace direct_module.Discovery
{
    public sealed class BleConnectionRequest
    {
        public string SourceShortSessionId { get; init; } = "";

        public string TargetShortSessionId { get; init; } = "";

        public string SourceRoleKey { get; init; } = "";

        public string RequestId { get; init; } = "";
    }
}
