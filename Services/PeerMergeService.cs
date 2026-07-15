using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services;

public static class PeerMergeService
{
    private const int DiscoveryIdentityHexCharacters = 24;

    public static string GetMatchReason(PeerInfo existing, PeerInfo incoming)
    {
        if (HasStableIdentityConflict(existing, incoming))
        {
            return "";
        }

        if (HasSameStablePeerId(existing.PeerId, incoming.PeerId))
        {
            return "PeerId一致";
        }

        if (HasSameValue(existing.DeviceId, incoming.DeviceId))
        {
            return "DeviceId一致";
        }

        if (HasCompatibleDiscoveryIdentity(existing, incoming, out string identity))
        {
            return $"強い探索Identity一致 ({identity})";
        }

        if (HasSameValue(existing.RemoteIpAddress, incoming.RemoteIpAddress) &&
            HasSameStablePeerId(existing.PeerId, incoming.PeerId))
        {
            return $"RemoteIpAddress一致 ({incoming.RemoteIpAddress})";
        }

        // A 16-bit ShortSessionId and a display name are not identities.
        return "";
    }

    public static bool IsPartialNameMatchCandidate(PeerInfo existing, PeerInfo incoming) => false;

    public static bool IsSingleCandidateFallback(PeerInfo existing, PeerInfo incoming) => false;

    public static void Merge(PeerInfo target, PeerInfo source)
    {
        if (HasInvalidStablePeerId(target.PeerId) || HasInvalidStablePeerId(source.PeerId) ||
            HasDifferentStablePeerId(target.PeerId, source.PeerId))
        {
            return;
        }

        bool hasPeerIdMatch = HasSameStablePeerId(target.PeerId, source.PeerId);
        bool hasStrongIdentityMatch = HasCompatibleDiscoveryIdentity(target, source, out _);

        if (!string.IsNullOrWhiteSpace(source.DisplayName) &&
            (string.IsNullOrWhiteSpace(target.DisplayName) || source.DisplayName.Length > target.DisplayName.Length))
        {
            target.DisplayName = source.DisplayName;
        }

        target.DiscoveredByBle |= source.DiscoveredByBle;
        target.DiscoveredByWiFiDirect |= source.DiscoveredByWiFiDirect;

        CopyIfPresent(source.BleName, value => target.BleName = value);
        CopyIfPresent(source.WiFiDirectName, value => target.WiFiDirectName = value);
        CopyStablePeerIdIfCompatible(target.PeerId, source.PeerId, value => target.PeerId = value);
        if (hasPeerIdMatch)
        {
            CopyIfPresent(source.MatchKey, value => target.MatchKey = value);
            CopyIfPresent(source.ShortSessionId, value => target.ShortSessionId = value);
            CopyIfPresent(source.RoleKey, value => target.RoleKey = value);
        }
        else
        {
            CopyDiscoveryIdentity(target.MatchKey, source.MatchKey, value => target.MatchKey = value);
            CopyIdentityIfCompatible(target.ShortSessionId, source.ShortSessionId, value => target.ShortSessionId = value);
            CopyIdentityIfCompatible(target.RoleKey, source.RoleKey, value => target.RoleKey = value);
        }

        if (hasPeerIdMatch || hasStrongIdentityMatch)
        {
            CopyIfPresent(source.DeviceId, value => target.DeviceId = value);
        }
        else
        {
            CopyIdentityIfCompatible(target.DeviceId, source.DeviceId, value => target.DeviceId = value);
        }
        CopyIfPresent(source.DeviceKind, value => target.DeviceKind = value);
        CopyIfPresent(source.IpAddress, value => target.IpAddress = value);
        if (hasPeerIdMatch || hasStrongIdentityMatch)
        {
            CopyIfPresent(source.RemoteIpAddress, value => target.RemoteIpAddress = value);
        }
        else
        {
            CopyIdentityIfCompatible(target.RemoteIpAddress, source.RemoteIpAddress, value => target.RemoteIpAddress = value);
        }

        if (source.TcpPort is > 0 and <= ushort.MaxValue)
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
        if (source.LastSeenAtUtc > target.LastSeenAtUtc)
        {
            target.LastSeenAtUtc = source.LastSeenAtUtc;
        }
        CopyIfPresent(source.StatusText, value => target.StatusText = value);
    }

    private static bool HasStableIdentityConflict(PeerInfo existing, PeerInfo incoming)
    {
        if (HasInvalidStablePeerId(existing.PeerId) || HasInvalidStablePeerId(incoming.PeerId) ||
            HasDifferentStablePeerId(existing.PeerId, incoming.PeerId))
        {
            return true;
        }

        if (HasSameStablePeerId(existing.PeerId, incoming.PeerId))
        {
            return false;
        }

        if (HasDifferentValue(existing.DeviceId, incoming.DeviceId) &&
            !HasCompatibleDiscoveryIdentity(existing, incoming, out _))
        {
            return true;
        }

        if (HasDifferentValue(existing.RemoteIpAddress, incoming.RemoteIpAddress) &&
            !HasSameValue(existing.DeviceId, incoming.DeviceId) &&
            !HasCompatibleDiscoveryIdentity(existing, incoming, out _) &&
            !HasSameStablePeerId(existing.PeerId, incoming.PeerId))
        {
            return true;
        }

        string existingIdentity = NormalizeHex(existing.MatchKey);
        string incomingIdentity = NormalizeHex(incoming.MatchKey);
        if (IsStrongIdentity(existingIdentity) &&
            IsStrongIdentity(incomingIdentity) &&
            !AreCompatibleStrongIdentities(existingIdentity, incomingIdentity))
        {
            return true;
        }

        return HasDifferentValue(existing.RoleKey, incoming.RoleKey) &&
               IsStrongIdentity(existingIdentity) &&
               IsStrongIdentity(incomingIdentity);
    }

    private static bool HasCompatibleDiscoveryIdentity(
        PeerInfo existing,
        PeerInfo incoming,
        out string commonIdentity)
    {
        string left = NormalizeHex(existing.MatchKey);
        string right = NormalizeHex(incoming.MatchKey);
        bool compatible = AreCompatibleStrongIdentities(left, right);
        commonIdentity = compatible ? (left.Length <= right.Length ? left : right) : "";
        return compatible;
    }

    private static bool AreCompatibleStrongIdentities(string left, string right)
    {
        return IsStrongIdentity(left) &&
               IsStrongIdentity(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrongIdentity(string value) =>
        NormalizeHex(value).Length == DiscoveryIdentityHexCharacters;

    private static string NormalizeHex(string value)
    {
        string normalized = (value ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            return "";
        }

        foreach (char character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                return "";
            }
        }

        return normalized;
    }

    private static bool HasSameValue(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDifferentValue(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInvalidStablePeerId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !PeerIdentityService.TryNormalizeStablePeerId(value, out _);

    private static bool HasSameStablePeerId(string? left, string? right) =>
        PeerIdentityService.TryNormalizeStablePeerId(left, out string normalizedLeft) &&
        PeerIdentityService.TryNormalizeStablePeerId(right, out string normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);

    private static bool HasDifferentStablePeerId(string? left, string? right) =>
        PeerIdentityService.TryNormalizeStablePeerId(left, out string normalizedLeft) &&
        PeerIdentityService.TryNormalizeStablePeerId(right, out string normalizedRight) &&
        !string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);

    private static void CopyDiscoveryIdentity(string current, string incoming, Action<string> apply)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(current) ||
            string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeHex(incoming), NormalizeHex(current), StringComparison.OrdinalIgnoreCase))
        {
            apply(incoming);
        }
    }

    private static void CopyIdentityIfCompatible(string current, string incoming, Action<string> apply)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase))
        {
            apply(incoming);
        }
    }

    private static void CopyStablePeerIdIfCompatible(string? current, string? incoming, Action<string> apply)
    {
        if (!PeerIdentityService.TryNormalizeStablePeerId(incoming, out string normalizedIncoming))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(current) ||
            (PeerIdentityService.TryNormalizeStablePeerId(current, out string normalizedCurrent) &&
             string.Equals(normalizedCurrent, normalizedIncoming, StringComparison.Ordinal)))
        {
            apply(normalizedIncoming);
        }
    }

    private static void CopyIfPresent(string value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }
}
