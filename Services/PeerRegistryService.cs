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
        Updated,
        Confirmed,
        Provisional
    }

    public sealed class PeerRegistrationResult
    {
        public PeerInfo Peer { get; init; } = null!;

        public PeerRegistrationKind Kind { get; init; }

        public string MatchReason { get; init; } = "";

        public int MatchScore { get; init; }

        public int UnmergedCandidateCount { get; init; }

        public bool PartialNameCandidateDetected { get; init; }

        public bool RoleConflictDetected { get; init; }

        public bool CollectionChanged => Kind == PeerRegistrationKind.Added;
    }

    public sealed class PeerRegistryService
    {
        private readonly ConnectionRoleService _connectionRoleService;
        private readonly List<PeerInfo> _peers = new();

        public PeerRegistryService(ConnectionRoleService connectionRoleService)
        {
            _connectionRoleService = connectionRoleService;
        }

        public IReadOnlyList<PeerInfo> Peers => _peers;

        public void AddSpecialPeer(PeerInfo peer)
        {
            if (!_peers.Contains(peer)) _peers.Add(peer);
        }

        public PeerInfo? FindByShortSessionId(string shortSessionId)
        {
            if (string.IsNullOrWhiteSpace(shortSessionId)) return null;
            return _peers.FirstOrDefault(peer =>
                string.Equals(peer.ShortSessionId, shortSessionId, StringComparison.OrdinalIgnoreCase));
        }

        public PeerInfo? FindByRemoteIpOrName(string remoteIpAddress, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(remoteIpAddress))
            {
                return _peers.FirstOrDefault(peer =>
                    string.Equals(peer.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrWhiteSpace(displayName)) return null;
            return _peers.FirstOrDefault(peer =>
                string.Equals(peer.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        }

        public PeerInfo? FindByPeerId(string peerId)
        {
            if (string.IsNullOrWhiteSpace(peerId)) return null;
            return _peers.FirstOrDefault(peer =>
                string.Equals(peer.PeerId, peerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(PeerIdentityService.GetConnectionId(peer), peerId, StringComparison.OrdinalIgnoreCase));
        }

        public PeerInfo? FindForHello(ChatMessage message, ChatConnection connection)
        {
            return FindByShortSessionId(message.ShortSessionId)
                ?? FindByPeerId(message.SenderId)
                ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "");
        }

        public PeerInfo? FindProvisionalForConnection(
            string remoteIpAddress,
            string originalPeerId,
            string originalShortSessionId)
        {
            return _peers.FirstOrDefault(peer =>
                peer.MatchState == PeerMatchState.Provisional &&
                ((!string.IsNullOrWhiteSpace(remoteIpAddress) &&
                  string.Equals(peer.RemoteIpAddress, remoteIpAddress, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(originalShortSessionId) &&
                  string.Equals(peer.ShortSessionId, originalShortSessionId, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(originalPeerId) &&
                  string.Equals(PeerIdentityService.GetConnectionId(peer), originalPeerId, StringComparison.OrdinalIgnoreCase))));
        }

        public PeerInfo? FindForConnection(ChatConnection connection)
        {
            return FindByPeerId(connection.PeerId)
                ?? FindByShortSessionId(connection.ShortSessionId)
                ?? FindByRemoteIpOrName(connection.RemoteIpAddress, "");
        }

        public PeerRegistrationResult Register(PeerInfo incoming)
        {
            if (incoming.IsIncomingConnectionRequest ||
                (!string.IsNullOrWhiteSpace(incoming.DeviceId) &&
                 incoming.DeviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase)))
            {
                return Result(incoming, PeerRegistrationKind.IgnoredPendingRequest);
            }

            PeerInfo? sameInstance = _peers.FirstOrDefault(peer => ReferenceEquals(peer, incoming));
            if (sameInstance != null)
            {
                return Result(sameInstance, PeerRegistrationKind.Updated);
            }

            PeerInfo? existingProvisional = _peers.FirstOrDefault(peer =>
                peer.MatchState == PeerMatchState.Provisional &&
                !string.IsNullOrWhiteSpace(peer.PendingWiFiDirectDeviceId) &&
                string.Equals(
                    peer.PendingWiFiDirectDeviceId,
                    incoming.DeviceId,
                    StringComparison.OrdinalIgnoreCase));
            if (existingProvisional != null)
            {
                PeerMergeService.ApplyProvisional(
                    existingProvisional,
                    incoming,
                    existingProvisional.MatchReason,
                    existingProvisional.MatchScore);
                return Result(
                    existingProvisional,
                    PeerRegistrationKind.Provisional,
                    existingProvisional.MatchReason,
                    existingProvisional.MatchScore);
            }

            var evaluations = _peers
                .Where(peer => !peer.IsGroupChat)
                .Select(peer => new
                {
                    Peer = peer,
                    Evaluation = PeerMatchService.Evaluate(peer, incoming, IsRoleCompatible(peer, incoming))
                })
                .ToList();

            var confirmed = evaluations
                .Where(item => item.Evaluation.State == PeerMatchState.Confirmed)
                .OrderByDescending(item => item.Evaluation.Score)
                .FirstOrDefault();
            if (confirmed != null)
            {
                PeerMergeService.MergeConfirmed(
                    confirmed.Peer,
                    incoming,
                    confirmed.Evaluation.Reason,
                    confirmed.Evaluation.Score);
                return Result(
                    confirmed.Peer,
                    PeerRegistrationKind.Confirmed,
                    confirmed.Evaluation.Reason,
                    confirmed.Evaluation.Score);
            }

            var provisional = evaluations
                .Where(item => item.Evaluation.State == PeerMatchState.Provisional)
                .ToList();
            if (provisional.Count == 1)
            {
                PeerInfo target = provisional[0].Peer;
                PeerInfo wifiCandidate = incoming.DiscoveredByWiFiDirect
                    ? incoming
                    : CreateWiFiCandidateSnapshot(target);
                if (incoming.DiscoveredByBle && !target.DiscoveredByBle)
                {
                    PeerMergeService.ApplyBleIdentityForProvisional(target, incoming);
                    target.DeviceId = "";
                    target.WiFiDirectName = "";
                    target.DeviceKind = "";
                    target.IsEnabled = null;
                }

                PeerMatchEvaluation evaluation = provisional[0].Evaluation;
                PeerMergeService.ApplyProvisional(target, wifiCandidate, evaluation.Reason, evaluation.Score);
                return Result(
                    target,
                    PeerRegistrationKind.Provisional,
                    evaluation.Reason,
                    evaluation.Score);
            }

            int unmergedCandidateCount = evaluations.Count(item =>
                PeerMatchService.IsBleWiFiDirectPair(item.Peer, incoming) &&
                !item.Evaluation.HasStableIdentityConflict);
            bool partialNameDetected = evaluations.Any(item => item.Evaluation.IsPartialNameCandidate);
            bool roleConflictDetected = evaluations.Any(item => item.Evaluation.IsRoleConflict);

            incoming.MatchState = PeerMatchState.Unmatched;
            incoming.MatchScore = 0;
            incoming.MatchReason = "強い照合キーなし";
            _peers.Add(incoming);
            return new PeerRegistrationResult
            {
                Peer = incoming,
                Kind = PeerRegistrationKind.Added,
                UnmergedCandidateCount = unmergedCandidateCount,
                PartialNameCandidateDetected = partialNameDetected,
                RoleConflictDetected = roleConflictDetected
            };
        }

        public IReadOnlyList<PeerInfo> RemoveStaleWiFiDirectPeers()
        {
            var changed = new List<PeerInfo>();
            foreach (PeerInfo peer in _peers.ToList())
            {
                if (peer.IsGroupChat || !peer.DiscoveredByWiFiDirect) continue;

                bool activeProvisional = peer.MatchState == PeerMatchState.Provisional &&
                    (peer.IsConnectingWiFiDirect || peer.IsConnected || peer.IsPreparingChatTcp || peer.IsTcpConnected);
                bool confirmedReconnectTarget = peer.MatchState == PeerMatchState.Confirmed &&
                    (!string.IsNullOrWhiteSpace(peer.DeviceId) || !string.IsNullOrWhiteSpace(peer.RemoteIpAddress));
                if (activeProvisional || confirmedReconnectTarget) continue;

                if (peer.DiscoveredByBle)
                {
                    PeerMergeService.ClearProvisionalCandidate(peer);
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

        public bool ConfirmUserSelectedWiFiCandidate(PeerInfo blePeer, PeerInfo wifiCandidate)
        {
            if (ReferenceEquals(blePeer, wifiCandidate) ||
                !_peers.Contains(blePeer) ||
                !_peers.Contains(wifiCandidate) ||
                !blePeer.DiscoveredByBle ||
                !wifiCandidate.DiscoveredByWiFiDirect ||
                wifiCandidate.DiscoveredByBle ||
                (!string.IsNullOrWhiteSpace(wifiCandidate.ShortSessionId) &&
                 !string.Equals(
                     blePeer.ShortSessionId,
                     wifiCandidate.ShortSessionId,
                     StringComparison.OrdinalIgnoreCase)) ||
                string.IsNullOrWhiteSpace(wifiCandidate.WiFiDirectDeviceIdForConnection))
            {
                return false;
            }

            PeerMergeService.MergeConfirmed(
                blePeer,
                wifiCandidate,
                "ユーザーがWi-Fi Direct候補を選択",
                100);
            _peers.Remove(wifiCandidate);
            return true;
        }

        public IReadOnlyList<PeerInfo> RemoveRelayPeersExcept(ISet<string> activePeerIds)
        {
            List<PeerInfo> removed = _peers
                .Where(peer =>
                    peer.IsRelayPeer &&
                    !activePeerIds.Contains(peer.PeerId))
                .ToList();
            foreach (PeerInfo peer in removed)
            {
                _peers.Remove(peer);
            }

            return removed;
        }

        private bool IsRoleCompatible(PeerInfo existing, PeerInfo incoming)
        {
            if (!PeerMatchService.IsBleWiFiDirectPair(existing, incoming)) return false;
            PeerInfo blePeer = existing.DiscoveredByBle ? existing : incoming;
            return ConnectionRoleService.HasRoleKey(blePeer) &&
                   _connectionRoleService.IsLocalClientForWifiDirect(blePeer);
        }

        private static PeerInfo CreateWiFiCandidateSnapshot(PeerInfo peer)
        {
            return new PeerInfo
            {
                DisplayName = peer.DisplayName,
                WiFiDirectName = peer.WiFiDirectName,
                DeviceId = peer.DeviceId,
                DeviceKind = peer.DeviceKind,
                IsEnabled = peer.IsEnabled,
                DiscoveredByWiFiDirect = true,
                DchatInformation = peer.DchatInformation,
                ShortSessionId = peer.ShortSessionId,
                MatchKey = peer.MatchKey
            };
        }

        private static PeerRegistrationResult Result(
            PeerInfo peer,
            PeerRegistrationKind kind,
            string reason = "",
            int score = 0)
        {
            return new PeerRegistrationResult
            {
                Peer = peer,
                Kind = kind,
                MatchReason = reason,
                MatchScore = score
            };
        }
    }
}
