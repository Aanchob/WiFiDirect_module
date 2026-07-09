using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public static class PeerMergeService
    {
        public static string GetMatchReason(PeerInfo existing, PeerInfo incoming)
        {
            if (HasSameValue(existing.ShortSessionId, incoming.ShortSessionId))
            {
                return $"ShortSessionId一致 ({incoming.ShortSessionId})";
            }

            if (HasSameValue(existing.DeviceId, incoming.DeviceId))
            {
                return "DeviceId一致";
            }

            if (HasSameValue(existing.RemoteIpAddress, incoming.RemoteIpAddress))
            {
                return $"RemoteIpAddress一致 ({incoming.RemoteIpAddress})";
            }

            if (HasSameValue(existing.MatchKey, incoming.MatchKey))
            {
                return $"MatchKey一致 ({incoming.MatchKey})";
            }

            if (HasSameValue(existing.DisplayName, incoming.DisplayName))
            {
                return $"DisplayName完全一致 ({incoming.DisplayName})";
            }

            if (IsBleNamePrefixMatch(existing, incoming))
            {
                string bleName = GetBleName(existing, incoming);
                string wifiName = GetWifiDirectName(existing, incoming);
                return $"BLE名前頭一致 ({bleName} -> {wifiName})";
            }

            if (IsBleWiFiDirectPartialNameMatch(existing, incoming))
            {
                return $"注意: BLE/Wi-Fi Direct名の部分一致 ({existing.DisplayName} / {incoming.DisplayName})";
            }

            return "";
        }

        public static bool IsPartialNameMatchCandidate(PeerInfo existing, PeerInfo incoming)
        {
            string existingName = existing.DisplayName ?? "";
            string incomingName = incoming.DisplayName ?? "";

            return existingName.Length >= 4 &&
                   incomingName.Length >= 4 &&
                   !string.Equals(existingName, incomingName, StringComparison.OrdinalIgnoreCase) &&
                   (existingName.Contains(incomingName, StringComparison.OrdinalIgnoreCase) ||
                    incomingName.Contains(existingName, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsSingleCandidateFallback(PeerInfo existing, PeerInfo incoming)
        {
            return IsBleWiFiDirectPair(existing, incoming) &&
                   !HasStableIdentityConflict(existing, incoming) &&
                   HasAnyDiscoveryIdentity(existing) &&
                   HasAnyDiscoveryIdentity(incoming);
        }

        public static void Merge(PeerInfo target, PeerInfo source)
        {
            if (!string.IsNullOrWhiteSpace(source.DisplayName) &&
                (string.IsNullOrWhiteSpace(target.DisplayName) ||
                 source.DisplayName.Length > target.DisplayName.Length))
            {
                target.DisplayName = source.DisplayName;
            }

            target.DiscoveredByBle |= source.DiscoveredByBle;
            target.DiscoveredByWiFiDirect |= source.DiscoveredByWiFiDirect;

            CopyIfPresent(source.BleName, value => target.BleName = value);
            CopyIfPresent(source.WiFiDirectName, value => target.WiFiDirectName = value);
            CopyIfPresent(source.MatchKey, value => target.MatchKey = value);
            CopyIfPresent(source.ShortSessionId, value => target.ShortSessionId = value);
            CopyIfPresent(source.RoleKey, value => target.RoleKey = value);
            CopyIfPresent(source.DeviceId, value => target.DeviceId = value);
            CopyIfPresent(source.DeviceKind, value => target.DeviceKind = value);
            CopyIfPresent(source.IpAddress, value => target.IpAddress = value);
            CopyIfPresent(source.RemoteIpAddress, value => target.RemoteIpAddress = value);

            if (source.TcpPort > 0)
            {
                target.TcpPort = source.TcpPort;
            }

            if (source.IsEnabled.HasValue)
            {
                target.IsEnabled = source.IsEnabled;
            }

            target.IsConnected |= source.IsConnected;
            target.IsPreparingChatTcp |= source.IsPreparingChatTcp;
            target.IsTcpConnected |= source.IsTcpConnected;
            target.IsHelloVerified |= source.IsHelloVerified;
            target.IsChatReady |= source.IsChatReady;
            CopyIfPresent(source.StatusText, value => target.StatusText = value);
        }

        private static bool IsBleWiFiDirectPartialNameMatch(PeerInfo existing, PeerInfo incoming)
        {
            return IsSingleCandidateFallback(existing, incoming) &&
                   IsPartialNameMatchCandidate(existing, incoming);
        }

        private static bool IsBleWiFiDirectPair(PeerInfo existing, PeerInfo incoming)
        {
            return (existing.DiscoveredByBle && incoming.DiscoveredByWiFiDirect) ||
                   (existing.DiscoveredByWiFiDirect && incoming.DiscoveredByBle);
        }

        private static bool HasStableIdentityConflict(PeerInfo existing, PeerInfo incoming)
        {
            return HasDifferentValue(existing.ShortSessionId, incoming.ShortSessionId) ||
                   HasDifferentValue(existing.DeviceId, incoming.DeviceId) ||
                   HasDifferentValue(existing.RemoteIpAddress, incoming.RemoteIpAddress) ||
                   HasDifferentValue(existing.MatchKey, incoming.MatchKey) ||
                   HasDifferentValue(existing.RoleKey, incoming.RoleKey);
        }

        private static bool HasSameValue(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBleNamePrefixMatch(PeerInfo existing, PeerInfo incoming)
        {
            return IsBleNamePrefixMatchCore(existing, incoming) ||
                   IsBleNamePrefixMatchCore(incoming, existing);
        }

        private static bool IsBleNamePrefixMatchCore(PeerInfo blePeer, PeerInfo wifiPeer)
        {
            if (!blePeer.DiscoveredByBle ||
                blePeer.DiscoveredByWiFiDirect ||
                !wifiPeer.DiscoveredByWiFiDirect ||
                wifiPeer.DiscoveredByBle)
            {
                return false;
            }

            string bleName = GetPeerBleName(blePeer);
            string wifiName = GetPeerWiFiDirectName(wifiPeer);

            return bleName.Length >= 3 &&
                   wifiName.Length > bleName.Length &&
                   wifiName.StartsWith(bleName, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetBleName(PeerInfo existing, PeerInfo incoming)
        {
            return existing.DiscoveredByBle && !existing.DiscoveredByWiFiDirect
                ? GetPeerBleName(existing)
                : GetPeerBleName(incoming);
        }

        private static string GetWifiDirectName(PeerInfo existing, PeerInfo incoming)
        {
            return existing.DiscoveredByWiFiDirect && !existing.DiscoveredByBle
                ? GetPeerWiFiDirectName(existing)
                : GetPeerWiFiDirectName(incoming);
        }

        private static string GetPeerBleName(PeerInfo peer)
        {
            return !string.IsNullOrWhiteSpace(peer.BleName)
                ? peer.BleName
                : peer.DisplayName;
        }

        private static string GetPeerWiFiDirectName(PeerInfo peer)
        {
            return !string.IsNullOrWhiteSpace(peer.WiFiDirectName)
                ? peer.WiFiDirectName
                : peer.DisplayName;
        }

        private static bool HasDifferentValue(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAnyDiscoveryIdentity(PeerInfo peer)
        {
            return !string.IsNullOrWhiteSpace(peer.ShortSessionId) ||
                   !string.IsNullOrWhiteSpace(peer.DeviceId) ||
                   !string.IsNullOrWhiteSpace(peer.RemoteIpAddress) ||
                   !string.IsNullOrWhiteSpace(peer.MatchKey) ||
                   !string.IsNullOrWhiteSpace(peer.RoleKey) ||
                   !string.IsNullOrWhiteSpace(peer.DisplayName);
        }

        private static void CopyIfPresent(string value, Action<string> apply)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value);
            }
        }
    }
}
