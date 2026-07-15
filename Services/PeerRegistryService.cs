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
        IgnoredCapacity,
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

    public sealed class PeerIdentityPromotionResult
    {
        public PeerInfo CanonicalPeer { get; init; } = null!;
        public IReadOnlyList<PeerInfo> RemovedPeers { get; init; } = Array.Empty<PeerInfo>();
        public bool IsConflict { get; init; }
        public bool CollectionChanged => RemovedPeers.Count > 0;
    }

    public sealed class PeerPresenceRemovalResult
    {
        public PeerInfo? Peer { get; init; }
        public bool SourceChanged { get; init; }
        public bool CollectionChanged { get; init; }
    }

    /// <summary>
    /// Owns bounded peer identity lookup and discovery-result reconciliation.
    /// IP addresses, display names, and 16-bit short IDs are lookup hints and never
    /// establish that two stable peers are the same identity.
    /// </summary>
    public sealed class PeerRegistryService
    {
        public const int MaximumPeers = 512;
        private const int DiscoveryIdentityHexCharacters = 24;

        private readonly object _gate = new();
        private readonly List<PeerInfo> _peers = new();

        public IReadOnlyList<PeerInfo> Peers
        {
            get { lock (_gate) return _peers.ToList(); }
        }

        public void AddSpecialPeer(PeerInfo peer)
        {
            ArgumentNullException.ThrowIfNull(peer);
            lock (_gate)
            {
                if (!_peers.Contains(peer)) _peers.Add(peer);
            }
        }

        public PeerInfo? FindByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId)) return null;
            lock (_gate)
            {
                List<PeerInfo> candidates = _peers.Where(peer => !peer.IsGroupChat &&
                    string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase)).ToList();
                return ChooseUnambiguous(candidates);
            }
        }

        public PeerInfo? FindByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            if (string.IsNullOrWhiteSpace(remoteIpAddress)) return null;
            lock (_gate)
            {
                List<PeerInfo> candidates = _peers.Where(peer => !peer.IsGroupChat &&
                    string.Equals(peer.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase)).ToList();
                return ChooseUnambiguous(candidates);
            }
        }

        public PeerInfo? FindByPeerId(string peerId)
        {
            if (!PeerIdentityService.TryNormalizeStablePeerId(peerId, out string normalizedPeerId)) return null;
            lock (_gate)
            {
                return ChooseBest(_peers.Where(peer => !peer.IsGroupChat &&
                    PeerIdentityService.TryNormalizeStablePeerId(peer.PeerId, out string candidatePeerId) &&
                    string.Equals(candidatePeerId, normalizedPeerId, StringComparison.Ordinal)));
            }
        }

        public PeerInfo? FindForHello(ChatMessage message, ChatConnection connection)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(connection);
            PeerInfo? stable = FindByPeerId(message.SenderId);
            PeerInfo? discovery = FindByShortSessionId(message.ShortSessionId);
            if (discovery != null && !ReferenceEquals(discovery, stable) &&
                (string.IsNullOrWhiteSpace(discovery.PeerId) ||
                 string.Equals(discovery.PeerId, message.SenderId, StringComparison.OrdinalIgnoreCase)))
            {
                // Prefer the current discovery-session object so verified promotion
                // can reconcile a restarted peer's old stable entry with fresh state.
                return discovery;
            }

            return stable ?? discovery ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "");
        }

        public PeerInfo? FindForConnection(ChatConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return FindByPeerId(connection.PeerId)
                ?? FindByShortSessionId(connection.ShortSessionId)
                ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "");
        }

        public PeerRegistrationResult Register(PeerInfo incoming)
        {
            ArgumentNullException.ThrowIfNull(incoming);
            if ((incoming.DeviceId ?? "").Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
                return Result(incoming, PeerRegistrationKind.IgnoredPendingRequest);

            if (!string.IsNullOrWhiteSpace(incoming.PeerId))
            {
                incoming.PeerId = PeerIdentityService.TryNormalizeStablePeerId(
                    incoming.PeerId,
                    out string normalizedPeerId)
                    ? normalizedPeerId
                    : "";
            }

            if (incoming.LastSeenAtUtc == default) incoming.LastSeenAtUtc = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                foreach (PeerInfo existing in _peers.Where(peer => !peer.IsGroupChat))
                {
                    string reason = PeerMergeService.GetMatchReason(existing, incoming);
                    if (reason.Length == 0) continue;
                    PeerMergeService.Merge(existing, incoming);
                    return Result(existing, PeerRegistrationKind.Merged, reason);
                }

                if (_peers.Count(peer => !peer.IsGroupChat) >= MaximumPeers)
                    return Result(incoming, PeerRegistrationKind.IgnoredCapacity);

                _peers.Add(incoming);
                return new PeerRegistrationResult
                {
                    Peer = incoming,
                    Kind = PeerRegistrationKind.Added,
                    AmbiguousCandidateCount = 0
                };
            }
        }

        /// <summary>
        /// Promotes a candidate only after HELLO plus its cryptographic identity have
        /// been verified. Existing entries with the same stable PeerId are folded into
        /// one canonical object, allowing a peer's discovery session ID to rotate on
        /// restart without leaving duplicate UI entries.
        /// </summary>
        public PeerIdentityPromotionResult PromoteVerifiedStableIdentity(PeerInfo candidate, string peerId)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            string normalizedPeerId = NormalizeStablePeerId(peerId);
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(candidate.PeerId) &&
                    !string.Equals(candidate.PeerId, normalizedPeerId, StringComparison.OrdinalIgnoreCase))
                {
                    return new PeerIdentityPromotionResult { CanonicalPeer = candidate, IsConflict = true };
                }

                List<PeerInfo> existingStableEntries = _peers.Where(peer =>
                    !peer.IsGroupChat && !ReferenceEquals(peer, candidate) &&
                    string.Equals(peer.PeerId, normalizedPeerId, StringComparison.OrdinalIgnoreCase)).ToList();

                PeerInfo canonical = ChooseBest(existingStableEntries) ?? candidate;
                var removed = new List<PeerInfo>();
                candidate.PeerId = normalizedPeerId;

                if (!_peers.Contains(canonical))
                {
                    if (_peers.Count(peer => !peer.IsGroupChat) >= MaximumPeers)
                        return new PeerIdentityPromotionResult { CanonicalPeer = candidate, IsConflict = true };
                    _peers.Add(canonical);
                }

                if (!ReferenceEquals(canonical, candidate))
                {
                    PeerMergeService.Merge(canonical, candidate);
                    if (!string.IsNullOrWhiteSpace(candidate.DisplayName))
                        canonical.DisplayName = candidate.DisplayName;
                    if (_peers.Remove(candidate)) removed.Add(candidate);
                }

                foreach (PeerInfo duplicate in existingStableEntries)
                {
                    if (ReferenceEquals(duplicate, canonical)) continue;
                    PeerMergeService.Merge(canonical, duplicate);
                    if (_peers.Remove(duplicate)) removed.Add(duplicate);
                }

                canonical.PeerId = normalizedPeerId;
                return new PeerIdentityPromotionResult
                {
                    CanonicalPeer = canonical,
                    RemovedPeers = removed
                };
            }
        }

        public PeerPresenceRemovalResult RemoveBlePresence(PeerInfo departed)
        {
            ArgumentNullException.ThrowIfNull(departed);
            lock (_gate)
            {
                PeerInfo? peer = FindPresenceCandidateLocked(departed);
                if (peer == null || !peer.DiscoveredByBle) return new PeerPresenceRemovalResult { Peer = peer };

                peer.DiscoveredByBle = false;
                peer.BleName = "";
                if (!peer.DiscoveredByWiFiDirect && !peer.IsConnected && !peer.IsTcpConnected && !peer.IsChatReady)
                {
                    bool removed = _peers.Remove(peer);
                    return new PeerPresenceRemovalResult
                    {
                        Peer = peer,
                        SourceChanged = true,
                        CollectionChanged = removed
                    };
                }

                return new PeerPresenceRemovalResult { Peer = peer, SourceChanged = true };
            }
        }

        public IReadOnlyList<PeerInfo> RemoveStaleWiFiDirectPeers()
        {
            var changed = new List<PeerInfo>();
            lock (_gate)
            {
                foreach (PeerInfo peer in _peers.ToList())
                {
                    if (peer.IsGroupChat || !peer.DiscoveredByWiFiDirect || peer.IsConnected ||
                        peer.IsTcpConnected || peer.IsChatReady) continue;
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
            }
            return changed;
        }

        private PeerInfo? FindPresenceCandidateLocked(PeerInfo peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.PeerId))
            {
                PeerInfo? stable = PeerIdentityService.TryNormalizeStablePeerId(peer.PeerId, out string stablePeerId)
                    ? _peers.FirstOrDefault(candidate =>
                        PeerIdentityService.TryNormalizeStablePeerId(candidate.PeerId, out string candidatePeerId) &&
                        string.Equals(candidatePeerId, stablePeerId, StringComparison.Ordinal))
                    : null;
                if (stable != null) return stable;
            }

            if (!string.IsNullOrWhiteSpace(peer.MatchKey))
            {
                List<PeerInfo> identityMatches = _peers.Where(candidate =>
                    string.Equals(candidate.MatchKey, peer.MatchKey, StringComparison.OrdinalIgnoreCase)).ToList();
                if (identityMatches.Count == 1) return identityMatches[0];
            }

            return null;
        }

        private static PeerInfo? ChooseUnambiguous(List<PeerInfo> candidates)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            string[] stableIds = candidates
                .Select(peer => PeerIdentityService.TryNormalizeStablePeerId(peer.PeerId, out string normalized)
                    ? normalized
                    : "")
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (stableIds.Length == 1 && candidates.All(peer =>
                    string.IsNullOrWhiteSpace(peer.PeerId) ||
                    (PeerIdentityService.TryNormalizeStablePeerId(peer.PeerId, out string normalized) &&
                     string.Equals(normalized, stableIds[0], StringComparison.Ordinal))))
            {
                return ChooseBest(candidates);
            }

            string[] discoveryIds = candidates.Select(peer => NormalizeDiscoveryIdentity(peer.MatchKey))
                .Where(value => value.Length == DiscoveryIdentityHexCharacters)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return stableIds.Length == 0 && discoveryIds.Length == 1
                ? ChooseBest(candidates)
                : null;
        }

        private static PeerInfo? ChooseBest(IEnumerable<PeerInfo> peers) => peers
            .OrderByDescending(peer => peer.IsChatReady)
            .ThenByDescending(peer => peer.IsConnected || peer.IsTcpConnected)
            .ThenByDescending(peer => peer.LastSeenAtUtc)
            .FirstOrDefault();

        private static string NormalizeStablePeerId(string peerId)
        {
            if (!PeerIdentityService.TryNormalizeStablePeerId(peerId, out string normalized))
            {
                throw new ArgumentException("A v2 stable peer identity is required.", nameof(peerId));
            }
            return normalized;
        }

        private static string NormalizeDiscoveryIdentity(string? identity)
        {
            string value = (identity ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            if (value.Length != DiscoveryIdentityHexCharacters || value.Any(character => !Uri.IsHexDigit(character)))
            {
                return "";
            }

            return value;
        }

        private static PeerRegistrationResult Result(PeerInfo peer, PeerRegistrationKind kind, string reason = "")
            => new() { Peer = peer, Kind = kind, MatchReason = reason };
    }
}
