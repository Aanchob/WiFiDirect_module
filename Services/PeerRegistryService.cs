using System;
using System.Collections.Generic;
using System.Linq;
using direct_module.Network;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public enum PeerRegistrationKind
    {
        IgnoredPendingRequest,
        Added,
        Merged,
        MergedPartialName,
        MergedSingleCandidate
    }

    public sealed class PeerRegistrationResult
    {
        public PeerInfo Peer { get; init; } = null!;
        public PeerRegistrationKind Kind { get; init; }
        public string MatchReason { get; init; } = "";
        public int AmbiguousCandidateCount { get; init; }
        public bool CollectionChanged => Kind == PeerRegistrationKind.Added;
    }

    /// <summary>
    /// Owns peer identity lookup and discovery-result reconciliation.
    /// UI-specific rendering and connection-state calculation remain with the caller.
    /// </summary>
    public sealed class PeerRegistryService
    {
        private readonly List<PeerInfo> _peers = new();

        public IReadOnlyList<PeerInfo> Peers => _peers;

        public void AddSpecialPeer(PeerInfo peer)
        {
            if (!_peers.Contains(peer))
            {
                _peers.Add(peer);
            }
        }

        public PeerInfo? FindByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId)) return null;
            return _peers.FirstOrDefault(peer =>
                string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase));
        }

        public PeerInfo? FindByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            return _peers.FirstOrDefault(peer =>
                (!string.IsNullOrWhiteSpace(remoteIpAddress) &&
                 string.Equals(peer.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(displayName) &&
                 string.Equals(peer.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));
        }

        public PeerInfo? FindByPeerId(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return null;
            return _peers.FirstOrDefault(peer =>
                string.Equals(PeerIdentityService.GetConnectionId(peer), peerId, StringComparison.OrdinalIgnoreCase));
        }

        public PeerInfo? FindForHello(ChatMessage message, ChatConnection connection)
        {
            return FindByShortSessionId(message.ShortSessionId)
                ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "")
                ?? FindByPeerId(connection.PeerId)
                ?? FindByRemoteIpOrName("", message.SenderName);
        }

        public PeerInfo? FindForConnection(ChatConnection connection)
        {
            return FindByPeerId(connection.PeerId)
                ?? FindByShortSessionId(connection.ShortSessionId)
                ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "")
                ?? FindByRemoteIpOrName("", connection.PeerName);
        }

        public PeerRegistrationResult Register(PeerInfo incoming)
        {
            if (incoming.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
            {
                return Result(incoming, PeerRegistrationKind.IgnoredPendingRequest);
            }

            foreach (PeerInfo existing in _peers.Where(peer => !peer.IsGroupChat))
            {
                string reason = PeerMergeService.GetMatchReason(existing, incoming);
                if (!string.IsNullOrEmpty(reason))
                {
                    PeerMergeService.Merge(existing, incoming);
                    return Result(existing, PeerRegistrationKind.Merged, reason);
                }

                if (PeerMergeService.IsPartialNameMatchCandidate(existing, incoming) &&
                    IsBleWiFiDirectPartialNamePair(existing, incoming))
                {
                    PeerMergeService.Merge(existing, incoming);
                    return Result(existing, PeerRegistrationKind.MergedPartialName);
                }
            }

            List<PeerInfo> fallbackCandidates = _peers
                .Where(peer => !peer.IsGroupChat)
                .Where(peer => PeerMergeService.IsSingleCandidateFallback(peer, incoming))
                .ToList();

            if (fallbackCandidates.Count == 1)
            {
                PeerInfo existing = fallbackCandidates[0];
                PeerMergeService.Merge(existing, incoming);
                return Result(existing, PeerRegistrationKind.MergedSingleCandidate);
            }

            _peers.Add(incoming);
            return new PeerRegistrationResult
            {
                Peer = incoming,
                Kind = PeerRegistrationKind.Added,
                AmbiguousCandidateCount = fallbackCandidates.Count
            };
        }

        public IReadOnlyList<PeerInfo> RemoveStaleWiFiDirectPeers()
        {
            var changed = new List<PeerInfo>();
            foreach (PeerInfo peer in _peers.ToList())
            {
                if (peer.IsGroupChat || !peer.DiscoveredByWiFiDirect || peer.IsConnected) continue;

                if (peer.DiscoveredByBle)
                {
                    peer.DiscoveredByWiFiDirect = false;
                    peer.WiFiDirectName = "";
                    peer.DeviceId = "";
                    peer.DeviceKind = "";
                    peer.IsEnabled = null;
                }
                else
                {
                    _peers.Remove(peer);
                }

                changed.Add(peer);
            }
            return changed;
        }

        private static PeerRegistrationResult Result(PeerInfo peer, PeerRegistrationKind kind, string reason = "")
            => new() { Peer = peer, Kind = kind, MatchReason = reason };

        private static bool IsBleWiFiDirectPartialNamePair(PeerInfo existing, PeerInfo incoming)
        {
            return (existing.DiscoveredByBle && !existing.DiscoveredByWiFiDirect && incoming.DiscoveredByWiFiDirect) ||
                   (incoming.DiscoveredByBle && !incoming.DiscoveredByWiFiDirect && existing.DiscoveredByWiFiDirect);
        }
    }
}
