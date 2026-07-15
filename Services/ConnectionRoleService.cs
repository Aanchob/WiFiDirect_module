using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services;

public enum BleRoleNegotiationStatus
{
    MissingRemoteRoleKey,
    AlreadyNegotiatedForOtherPeer,
    RoleKeyCollision,
    LocalGo,
    LocalClient
}

public enum TcpRoleDecisionSource
{
    PeerIdentity,
    RoleKey,
    ShortSessionId,
    EqualShortSessionIdFallback,
    MissingShortSessionIdFallback
}

public sealed class BleRoleNegotiationResult
{
    public BleRoleNegotiationStatus Status { get; init; }
    public string LocalRoleKey { get; init; } = "";
    public string RemoteRoleKey { get; init; } = "";
    public string CurrentPeerKey { get; init; } = "";
    public string IgnoredPeerKey { get; init; } = "";
    public bool LocalIsGo => Status == BleRoleNegotiationStatus.LocalGo;
    public bool LocalIsClient => Status == BleRoleNegotiationStatus.LocalClient;
}

public sealed class TcpRoleDecision
{
    public TcpRoleDecisionSource Source { get; init; }
    public bool ShouldStartConnection { get; init; }
    public string LocalShortSessionId { get; init; } = "";
    public string RemoteShortSessionId { get; init; } = "";
    public string RemoteRoleKey { get; init; } = "";
    public string LocalRoleText => ShouldStartConnection ? "Client" : "GO";
}

public sealed class ConnectionRoleService
{
    private const int DiscoveryIdentityHexCharacters = 24;
    private readonly string _localShortSessionId;
    private readonly string _localRoleKey;
    private readonly string _localPeerIdentity;

    public ConnectionRoleService(
        string localShortSessionId,
        string localRoleKey,
        string localPeerIdentity = "")
    {
        _localShortSessionId = NormalizeHex(localShortSessionId);
        _localRoleKey = NormalizeHex(localRoleKey);
        _localPeerIdentity = NormalizeHex(localPeerIdentity);
    }

    // Negotiation is now peer-local and stateless; resetting is retained for API compatibility.
    public void ResetBleNegotiation()
    {
    }

    public bool IsLocalClientForWifiDirect(PeerInfo peer)
    {
        return TryGetOrderingKeys(peer, out string localKey, out string remoteKey) &&
               string.Compare(localKey, remoteKey, StringComparison.OrdinalIgnoreCase) < 0;
    }

    public BleRoleNegotiationResult DecideBleRole(PeerInfo peer, string peerKey)
    {
        if (!TryGetOrderingKeys(peer, out string localKey, out string remoteKey))
        {
            return new BleRoleNegotiationResult
            {
                Status = BleRoleNegotiationStatus.MissingRemoteRoleKey,
                LocalRoleKey = _localRoleKey,
                RemoteRoleKey = peer.RoleKey,
                CurrentPeerKey = peerKey
            };
        }

        int compare = string.Compare(localKey, remoteKey, StringComparison.OrdinalIgnoreCase);
        if (compare == 0)
        {
            return new BleRoleNegotiationResult
            {
                Status = BleRoleNegotiationStatus.RoleKeyCollision,
                LocalRoleKey = localKey,
                RemoteRoleKey = remoteKey,
                CurrentPeerKey = peerKey
            };
        }

        return new BleRoleNegotiationResult
        {
            Status = compare > 0
                ? BleRoleNegotiationStatus.LocalGo
                : BleRoleNegotiationStatus.LocalClient,
            LocalRoleKey = localKey,
            RemoteRoleKey = remoteKey,
            CurrentPeerKey = peerKey
        };
    }

    public TcpRoleDecision DecideTcpRole(PeerInfo peer, bool fallbackIsClient)
    {
        if (TryGetStrongRemoteIdentity(peer, out string remoteIdentity) && IsStrongIdentity(_localPeerIdentity))
        {
            int compare = string.Compare(_localPeerIdentity, remoteIdentity, StringComparison.OrdinalIgnoreCase);
            if (compare != 0)
            {
                return CreateTcpDecision(TcpRoleDecisionSource.PeerIdentity, compare < 0, peer);
            }
        }

        if (HasRoleKey(peer))
        {
            return CreateTcpDecision(
                TcpRoleDecisionSource.RoleKey,
                string.Compare(_localRoleKey, peer.RoleKey, StringComparison.OrdinalIgnoreCase) < 0,
                peer);
        }

        string remoteShortSessionId = NormalizeHex(peer.ShortSessionId);
        if (!string.IsNullOrEmpty(_localShortSessionId) && !string.IsNullOrEmpty(remoteShortSessionId))
        {
            int compare = string.Compare(
                _localShortSessionId,
                remoteShortSessionId,
                StringComparison.OrdinalIgnoreCase);

            return compare == 0
                ? CreateTcpDecision(TcpRoleDecisionSource.EqualShortSessionIdFallback, fallbackIsClient, peer)
                : CreateTcpDecision(TcpRoleDecisionSource.ShortSessionId, compare < 0, peer);
        }

        return CreateTcpDecision(TcpRoleDecisionSource.MissingShortSessionIdFallback, fallbackIsClient, peer);
    }

    public static bool HasRoleKey(PeerInfo peer)
    {
        string shortSessionId = NormalizeHex(peer.ShortSessionId);
        string roleKey = NormalizeHex(peer.RoleKey);
        string matchKey = NormalizeHex(peer.MatchKey);

        return shortSessionId.Length == 4 &&
               roleKey.Length == 8 &&
               IsStrongIdentity(matchKey) &&
               roleKey.StartsWith(shortSessionId, StringComparison.OrdinalIgnoreCase) &&
               matchKey.StartsWith(roleKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetOrderingKeys(PeerInfo peer, out string localKey, out string remoteKey)
    {
        localKey = "";
        remoteKey = "";
        if (!HasRoleKey(peer))
        {
            return false;
        }

        if (IsStrongIdentity(_localPeerIdentity) && TryGetStrongRemoteIdentity(peer, out remoteKey))
        {
            localKey = _localPeerIdentity;
            return true;
        }

        if (_localRoleKey.Length == 8)
        {
            localKey = _localRoleKey;
            remoteKey = NormalizeHex(peer.RoleKey);
            return true;
        }

        return false;
    }

    private static bool TryGetStrongRemoteIdentity(PeerInfo peer, out string identity)
    {
        string matchKey = NormalizeHex(peer.MatchKey);
        if (IsStrongIdentity(matchKey))
        {
            identity = matchKey;
            return true;
        }

        identity = "";
        return false;
    }

    private TcpRoleDecision CreateTcpDecision(
        TcpRoleDecisionSource source,
        bool shouldStartConnection,
        PeerInfo peer)
    {
        return new TcpRoleDecision
        {
            Source = source,
            ShouldStartConnection = shouldStartConnection,
            LocalShortSessionId = _localShortSessionId,
            RemoteShortSessionId = peer.ShortSessionId,
            RemoteRoleKey = peer.RoleKey
        };
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
}
