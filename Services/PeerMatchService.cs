using System;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public sealed class PeerMatchEvaluation
    {
        public PeerMatchState State { get; init; }

        public int Score { get; init; }

        public string Reason { get; init; } = "";

        public bool IsPartialNameCandidate { get; init; }

        public bool IsRoleConflict { get; init; }

        public bool HasStableIdentityConflict { get; init; }
    }

    public static class PeerMatchService
    {
        public static PeerMatchEvaluation Evaluate(
            PeerInfo existing,
            PeerInfo incoming,
            bool roleCompatible)
        {
            string conflictReason = GetDefinitiveIdentityConflictReason(existing, incoming);
            if (!string.IsNullOrWhiteSpace(conflictReason))
            {
                return new PeerMatchEvaluation
                {
                    State = PeerMatchState.Unmatched,
                    Reason = conflictReason,
                    HasStableIdentityConflict = true
                };
            }

            (string strongReason, int strongScore) = GetStrongMatch(existing, incoming);
            if (!string.IsNullOrWhiteSpace(strongReason))
            {
                return new PeerMatchEvaluation
                {
                    State = PeerMatchState.Confirmed,
                    Score = strongScore,
                    Reason = strongReason
                };
            }

            conflictReason = GetCandidateIdentityConflictReason(existing, incoming);
            if (!string.IsNullOrWhiteSpace(conflictReason))
            {
                return new PeerMatchEvaluation
                {
                    State = PeerMatchState.Unmatched,
                    Reason = conflictReason,
                    HasStableIdentityConflict = true
                };
            }

            if (!IsBleWiFiDirectPair(existing, incoming))
            {
                return new PeerMatchEvaluation { State = PeerMatchState.Unmatched };
            }

            bool exactName = HasExactTransportName(existing, incoming);
            bool partialName = IsPartialNameMatchCandidate(existing, incoming);
            if (!roleCompatible)
            {
                return new PeerMatchEvaluation
                {
                    State = PeerMatchState.Unmatched,
                    Reason = "Roleフロー不整合",
                    IsPartialNameCandidate = partialName,
                    IsRoleConflict = true
                };
            }

            int score = 25;
            if (exactName)
            {
                score += 30;
            }

            if (score >= 50)
            {
                return new PeerMatchEvaluation
                {
                    State = PeerMatchState.Provisional,
                    Score = score,
                    Reason = "Role整合 + DisplayName完全一致"
                };
            }

            return new PeerMatchEvaluation
            {
                State = PeerMatchState.Unmatched,
                Score = score,
                Reason = partialName ? "名前部分一致のみ" : "強い照合キーなし",
                IsPartialNameCandidate = partialName
            };
        }

        public static bool IsBleWiFiDirectPair(PeerInfo existing, PeerInfo incoming)
        {
            return (existing.DiscoveredByBle && !existing.DiscoveredByWiFiDirect && incoming.DiscoveredByWiFiDirect) ||
                   (incoming.DiscoveredByBle && !incoming.DiscoveredByWiFiDirect && existing.DiscoveredByWiFiDirect);
        }

        public static bool IsPartialNameMatchCandidate(PeerInfo existing, PeerInfo incoming)
        {
            string existingName = GetTransportName(existing);
            string incomingName = GetTransportName(incoming);

            return existingName.Length >= 3 &&
                   incomingName.Length >= 3 &&
                   !string.Equals(existingName, incomingName, StringComparison.OrdinalIgnoreCase) &&
                   (existingName.StartsWith(incomingName, StringComparison.OrdinalIgnoreCase) ||
                    incomingName.StartsWith(existingName, StringComparison.OrdinalIgnoreCase) ||
                    existingName.Contains(incomingName, StringComparison.OrdinalIgnoreCase) ||
                    incomingName.Contains(existingName, StringComparison.OrdinalIgnoreCase));
        }

        private static (string Reason, int Score) GetStrongMatch(PeerInfo existing, PeerInfo incoming)
        {
            if (HasSameValue(existing.ShortSessionId, incoming.ShortSessionId))
            {
                return ("ShortSessionId完全一致", 100);
            }

            if (HasSameValue(existing.PeerId, incoming.PeerId))
            {
                return ("PeerId完全一致", 100);
            }

            if (HasSameValue(existing.DeviceId, incoming.DeviceId))
            {
                return ("DeviceId完全一致", 100);
            }

            if (HasSameValue(existing.RemoteIpAddress, incoming.RemoteIpAddress))
            {
                return ("RemoteIpAddress完全一致", 90);
            }

            if (HasSameValue(existing.MatchKey, incoming.MatchKey))
            {
                return ("MatchKey完全一致", 90);
            }

            if (HasSameValue(existing.DchatInformation, incoming.DchatInformation))
            {
                return ("DCHAT InformationElement完全一致", 100);
            }

            return ("", 0);
        }

        private static string GetDefinitiveIdentityConflictReason(PeerInfo existing, PeerInfo incoming)
        {
            if (HasDifferentValue(existing.ShortSessionId, incoming.ShortSessionId)) return "ShortSessionId不一致";
            if (HasDifferentValue(existing.PeerId, incoming.PeerId)) return "PeerId不一致";
            if (HasDifferentValue(existing.RoleKey, incoming.RoleKey)) return "RoleKey不一致";
            return "";
        }

        private static string GetCandidateIdentityConflictReason(PeerInfo existing, PeerInfo incoming)
        {
            if (HasDifferentValue(existing.DeviceId, incoming.DeviceId)) return "DeviceId不一致";
            if (HasDifferentValue(existing.RemoteIpAddress, incoming.RemoteIpAddress)) return "RemoteIpAddress不一致";
            if (HasDifferentValue(existing.MatchKey, incoming.MatchKey)) return "MatchKey不一致";
            return "";
        }

        private static bool HasExactTransportName(PeerInfo existing, PeerInfo incoming)
        {
            string existingName = GetTransportName(existing);
            string incomingName = GetTransportName(incoming);
            return HasSameValue(existingName, incomingName);
        }

        private static string GetTransportName(PeerInfo peer)
        {
            if (peer.DiscoveredByBle && !string.IsNullOrWhiteSpace(peer.BleName)) return peer.BleName;
            if (peer.DiscoveredByWiFiDirect && !string.IsNullOrWhiteSpace(peer.WiFiDirectName)) return peer.WiFiDirectName;
            return peer.DisplayName;
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
    }
}
