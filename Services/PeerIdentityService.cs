using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public static class PeerIdentityService
    {
        public const string DefaultGroupConversationId = "group:all-peers:v1";

        public static string GetConnectionId(PeerInfo peer)
        {
            ArgumentNullException.ThrowIfNull(peer);

            if (TryNormalizeStablePeerId(peer.PeerId, out string stablePeerId))
            {
                return stablePeerId;
            }

            if (!string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                return peer.DeviceId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                return peer.RemoteIpAddress.Trim();
            }

            if (!string.IsNullOrWhiteSpace(peer.ShortSessionId))
            {
                return peer.ShortSessionId.Trim();
            }

            return peer.DisplayName.Trim();
        }

        public static string GetGroupConversationId(IEnumerable<string> participantPeerIds)
        {
            ArgumentNullException.ThrowIfNull(participantPeerIds);

            string[] identities = participantPeerIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            if (identities.Length == 0)
            {
                return DefaultGroupConversationId;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identities)));
            return $"group:v1:{Convert.ToHexString(hash).ToLowerInvariant()}";
        }

        public static string NormalizeConversationId(string? conversationId, bool isGroup)
        {
            string value = conversationId?.Trim() ?? "";
            if (isGroup && (value.Length == 0 || string.Equals(value, "group", StringComparison.OrdinalIgnoreCase)))
            {
                return DefaultGroupConversationId;
            }

            return value;
        }

        public static bool TryNormalizeStablePeerId(string? peerId, out string normalized)
        {
            string value = peerId?.Trim() ?? "";
            const string prefix = "peer:";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(value[prefix.Length..], "N", out Guid parsed))
            {
                normalized = "";
                return false;
            }

            normalized = prefix + parsed.ToString("N");
            return true;
        }
    }
}
