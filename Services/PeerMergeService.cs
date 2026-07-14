using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public static class PeerMergeService
    {
        public static void MergeConfirmed(
            PeerInfo target,
            PeerInfo source,
            string reason,
            int score)
        {
            if (!string.IsNullOrWhiteSpace(source.DisplayName) &&
                (string.IsNullOrWhiteSpace(target.DisplayName) || source.DisplayName.Length > target.DisplayName.Length))
            {
                target.DisplayName = source.DisplayName;
            }

            target.DiscoveredByBle |= source.DiscoveredByBle;
            target.DiscoveredByWiFiDirect |= source.DiscoveredByWiFiDirect;

            CopyIfPresent(source.BleName, value => target.BleName = value);
            CopyIfPresent(source.WiFiDirectName, value => target.WiFiDirectName = value);
            CopyIfPresent(source.MatchKey, value => target.MatchKey = value);
            CopyIfPresent(source.DchatInformation, value => target.DchatInformation = value);
            CopyIfPresent(source.ShortSessionId, value => target.ShortSessionId = value);
            CopyIfPresent(source.RoleKey, value => target.RoleKey = value);
            CopyIfPresent(source.DeviceId, value => target.DeviceId = value);
            CopyIfPresent(source.PeerId, value => target.PeerId = value);
            CopyIfPresent(source.DeviceKind, value => target.DeviceKind = value);
            CopyIfPresent(source.IpAddress, value => target.IpAddress = value);
            CopyIfPresent(source.RemoteIpAddress, value => target.RemoteIpAddress = value);

            if (source.TcpPort > 0) target.TcpPort = source.TcpPort;
            if (source.IsEnabled.HasValue) target.IsEnabled = source.IsEnabled;

            target.IsConnected |= source.IsConnected;
            target.IsPreparingChatTcp |= source.IsPreparingChatTcp;
            target.IsTcpConnected |= source.IsTcpConnected;
            target.IsHelloVerified |= source.IsHelloVerified;
            target.IsChatReady |= source.IsChatReady;
            CopyIfPresent(source.StatusText, value => target.StatusText = value);

            ClearPending(target);
            target.MatchState = PeerMatchState.Confirmed;
            target.MatchScore = score;
            target.MatchReason = reason;
        }

        public static void ApplyProvisional(
            PeerInfo target,
            PeerInfo wifiCandidate,
            string reason,
            int score)
        {
            target.DiscoveredByWiFiDirect = true;
            target.PendingWiFiDirectDeviceId = wifiCandidate.DeviceId;
            target.PendingWiFiDirectName = !string.IsNullOrWhiteSpace(wifiCandidate.WiFiDirectName)
                ? wifiCandidate.WiFiDirectName
                : wifiCandidate.DisplayName;
            target.PendingWiFiDirectDeviceKind = wifiCandidate.DeviceKind;
            target.PendingWiFiDirectIsEnabled = wifiCandidate.IsEnabled;
            target.MatchState = PeerMatchState.Provisional;
            target.MatchScore = score;
            target.MatchReason = reason;
        }

        public static void ApplyBleIdentityForProvisional(PeerInfo target, PeerInfo bleCandidate)
        {
            target.DisplayName = bleCandidate.DisplayName;
            target.DiscoveredByBle = true;
            CopyIfPresent(bleCandidate.BleName, value => target.BleName = value);
            CopyIfPresent(bleCandidate.ShortSessionId, value => target.ShortSessionId = value);
            CopyIfPresent(bleCandidate.RoleKey, value => target.RoleKey = value);
            CopyIfPresent(bleCandidate.MatchKey, value => target.MatchKey = value);
            if (bleCandidate.TcpPort > 0) target.TcpPort = bleCandidate.TcpPort;
        }

        public static void ConfirmAfterHello(PeerInfo peer, string reason)
        {
            if (!string.IsNullOrWhiteSpace(peer.PendingWiFiDirectDeviceId) &&
                !peer.PendingWiFiDirectDeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                peer.DeviceId = peer.PendingWiFiDirectDeviceId;
            }

            CopyIfPresent(peer.PendingWiFiDirectName, value => peer.WiFiDirectName = value);
            CopyIfPresent(peer.PendingWiFiDirectDeviceKind, value => peer.DeviceKind = value);
            if (peer.PendingWiFiDirectIsEnabled.HasValue)
            {
                peer.IsEnabled = peer.PendingWiFiDirectIsEnabled;
            }

            ClearPending(peer);
            peer.MatchState = PeerMatchState.Confirmed;
            peer.MatchScore = 100;
            peer.MatchReason = reason;
        }

        public static void RejectProvisional(PeerInfo peer, string reason)
        {
            ClearPending(peer);
            peer.DiscoveredByWiFiDirect = !string.IsNullOrWhiteSpace(peer.DeviceId);
            peer.IsConnected = false;
            peer.IsConnectingWiFiDirect = false;
            peer.IsTcpConnected = false;
            peer.IsHelloVerified = false;
            peer.IsChatReady = false;
            peer.RemoteIpAddress = "";
            peer.MatchState = PeerMatchState.Rejected;
            peer.MatchScore = 0;
            peer.MatchReason = reason;
        }

        public static void ClearProvisionalCandidate(PeerInfo peer)
        {
            ClearPending(peer);
            peer.DiscoveredByWiFiDirect = !string.IsNullOrWhiteSpace(peer.DeviceId);
            if (peer.MatchState == PeerMatchState.Provisional)
            {
                peer.MatchState = PeerMatchState.Unmatched;
                peer.MatchScore = 0;
                peer.MatchReason = "";
            }
        }

        private static void ClearPending(PeerInfo peer)
        {
            peer.PendingWiFiDirectDeviceId = "";
            peer.PendingWiFiDirectName = "";
            peer.PendingWiFiDirectDeviceKind = "";
            peer.PendingWiFiDirectIsEnabled = null;
        }

        private static void CopyIfPresent(string value, Action<string> apply)
        {
            if (!string.IsNullOrWhiteSpace(value)) apply(value);
        }
    }
}
