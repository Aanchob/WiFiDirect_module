using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
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
        private readonly string _localShortSessionId;
        private readonly string _localRoleKey;

        private string _negotiatedBlePeerKey = "";

        public ConnectionRoleService(string localShortSessionId, string localRoleKey)
        {
            _localShortSessionId = localShortSessionId;
            _localRoleKey = localRoleKey;
        }

        public void ResetBleNegotiation()
        {
            _negotiatedBlePeerKey = "";
        }

        public bool IsLocalClientForWifiDirect(PeerInfo peer)
        {
            return HasRoleKey(peer) &&
                   CompareRoleKey(_localRoleKey, peer.RoleKey) < 0;
        }

        public BleRoleNegotiationResult DecideBleRole(PeerInfo peer, string peerKey)
        {
            string remoteRoleKey = peer.RoleKey;

            if (string.IsNullOrWhiteSpace(remoteRoleKey))
            {
                return new BleRoleNegotiationResult
                {
                    Status = BleRoleNegotiationStatus.MissingRemoteRoleKey,
                    LocalRoleKey = _localRoleKey,
                    RemoteRoleKey = remoteRoleKey
                };
            }

            if (!string.IsNullOrWhiteSpace(_negotiatedBlePeerKey) &&
                !string.Equals(_negotiatedBlePeerKey, peerKey, StringComparison.OrdinalIgnoreCase))
            {
                return new BleRoleNegotiationResult
                {
                    Status = BleRoleNegotiationStatus.AlreadyNegotiatedForOtherPeer,
                    LocalRoleKey = _localRoleKey,
                    RemoteRoleKey = remoteRoleKey,
                    CurrentPeerKey = _negotiatedBlePeerKey,
                    IgnoredPeerKey = peerKey
                };
            }

            int compare = CompareRoleKey(_localRoleKey, remoteRoleKey);
            if (compare == 0)
            {
                return new BleRoleNegotiationResult
                {
                    Status = BleRoleNegotiationStatus.RoleKeyCollision,
                    LocalRoleKey = _localRoleKey,
                    RemoteRoleKey = remoteRoleKey
                };
            }

            _negotiatedBlePeerKey = peerKey;

            return new BleRoleNegotiationResult
            {
                Status = compare > 0
                    ? BleRoleNegotiationStatus.LocalGo
                    : BleRoleNegotiationStatus.LocalClient,
                LocalRoleKey = _localRoleKey,
                RemoteRoleKey = remoteRoleKey,
                CurrentPeerKey = peerKey
            };
        }

        public TcpRoleDecision DecideTcpRole(PeerInfo peer, bool fallbackIsClient)
        {
            if (HasRoleKey(peer))
            {
                bool localIsClient = IsLocalClientForWifiDirect(peer);
                return new TcpRoleDecision
                {
                    Source = TcpRoleDecisionSource.RoleKey,
                    ShouldStartConnection = localIsClient,
                    LocalShortSessionId = _localShortSessionId,
                    RemoteShortSessionId = peer.ShortSessionId,
                    RemoteRoleKey = peer.RoleKey
                };
            }

            string remoteShortSessionId = peer.ShortSessionId;

            if (!string.IsNullOrWhiteSpace(_localShortSessionId) &&
                !string.IsNullOrWhiteSpace(remoteShortSessionId))
            {
                int compare = string.Compare(
                    _localShortSessionId,
                    remoteShortSessionId,
                    StringComparison.OrdinalIgnoreCase);

                if (compare == 0)
                {
                    return new TcpRoleDecision
                    {
                        Source = TcpRoleDecisionSource.EqualShortSessionIdFallback,
                        ShouldStartConnection = fallbackIsClient,
                        LocalShortSessionId = _localShortSessionId,
                        RemoteShortSessionId = remoteShortSessionId,
                        RemoteRoleKey = peer.RoleKey
                    };
                }

                return new TcpRoleDecision
                {
                    Source = TcpRoleDecisionSource.ShortSessionId,
                    ShouldStartConnection = compare < 0,
                    LocalShortSessionId = _localShortSessionId,
                    RemoteShortSessionId = remoteShortSessionId,
                    RemoteRoleKey = peer.RoleKey
                };
            }

            return new TcpRoleDecision
            {
                Source = TcpRoleDecisionSource.MissingShortSessionIdFallback,
                ShouldStartConnection = fallbackIsClient,
                LocalShortSessionId = _localShortSessionId,
                RemoteShortSessionId = remoteShortSessionId,
                RemoteRoleKey = peer.RoleKey
            };
        }

        public static bool HasRoleKey(PeerInfo peer)
        {
            return !string.IsNullOrWhiteSpace(peer.RoleKey);
        }

        private static int CompareRoleKey(string localRoleKey, string remoteRoleKey)
        {
            return string.Compare(localRoleKey, remoteRoleKey, StringComparison.OrdinalIgnoreCase);
        }
    }
}
