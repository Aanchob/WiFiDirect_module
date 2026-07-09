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

        private static bool HasSameValue(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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
