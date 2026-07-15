using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public sealed class PeerConnectionStateService
    {
        private readonly ConnectionRoleService _connectionRoleService;

        public PeerConnectionStateService(ConnectionRoleService connectionRoleService)
        {
            _connectionRoleService = connectionRoleService;
        }

        public void UpdateConnectAvailability(PeerInfo peer)
        {
            peer.CanConnect =
                peer.DiscoveredByBle &&
                peer.DiscoveredByWiFiDirect &&
                !string.IsNullOrWhiteSpace(peer.DeviceId) &&
                !peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                !string.IsNullOrWhiteSpace(peer.RoleKey) &&
                _connectionRoleService.IsLocalClientForWifiDirect(peer) &&
                !peer.IsConnectingWiFiDirect &&
                !peer.IsConnected &&
                !peer.IsTcpConnected &&
                !peer.IsChatReady &&
                !peer.IsPreparingChatTcp;
        }

        public bool CanReconnect(PeerInfo peer)
        {
            UpdateConnectAvailability(peer);

            bool hasTcpEndpoint = !string.IsNullOrWhiteSpace(peer.RemoteIpAddress);
            bool hasWiFiDirectEndpoint = !string.IsNullOrWhiteSpace(peer.DeviceId) &&
                                         !peer.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);

            return !peer.IsGroupChat &&
                   (hasTcpEndpoint || hasWiFiDirectEndpoint) &&
                   !peer.IsChatReady &&
                   !peer.IsConnectingWiFiDirect &&
                   !peer.IsPreparingChatTcp &&
                   !string.Equals(peer.StatusText, "再接続中", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsChatReady(PeerInfo peer)
        {
            return !peer.IsPreparingChatTcp &&
                   peer.IsTcpConnected &&
                   peer.IsHelloVerified &&
                   peer.IsChatReady;
        }
    }
}
