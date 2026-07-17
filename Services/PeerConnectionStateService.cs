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
            bool isIdle =
                !peer.IsConnectingWiFiDirect &&
                !peer.IsConnected &&
                !peer.IsTcpConnected &&
                !peer.IsChatReady &&
                !peer.IsPreparingChatTcp;

            bool canJoinAsClient =
                peer.DiscoveredByBle &&
                peer.DiscoveredByWiFiDirect &&
                !string.IsNullOrWhiteSpace(peer.WiFiDirectDeviceIdForConnection) &&
                !peer.WiFiDirectDeviceIdForConnection.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                !string.IsNullOrWhiteSpace(peer.RoleKey) &&
                (peer.MatchState == PeerMatchState.Provisional || peer.MatchState == PeerMatchState.Confirmed) &&
                _connectionRoleService.IsLocalClientForWifiDirect(peer);

            bool canRequestFromGo =
                peer.DiscoveredByBle &&
                !string.IsNullOrWhiteSpace(peer.ShortSessionId) &&
                !string.IsNullOrWhiteSpace(peer.RoleKey) &&
                !_connectionRoleService.IsLocalClientForWifiDirect(peer);

            peer.CanConnect = isIdle && (canJoinAsClient || canRequestFromGo);
        }

        public bool CanReconnect(PeerInfo peer)
        {
            UpdateConnectAvailability(peer);

            return peer.DiscoveredByBle &&
                   peer.DiscoveredByWiFiDirect &&
                   !string.IsNullOrWhiteSpace(peer.RoleKey) &&
                   (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress) || !string.IsNullOrWhiteSpace(peer.WiFiDirectDeviceIdForConnection)) &&
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
