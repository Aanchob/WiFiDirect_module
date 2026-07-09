using System;
using System.Linq;
using direct_module.WiFiDirect.Models;

namespace direct_module.Services
{
    public static class PeerDisplayService
    {
        public static string GetStatusText(PeerInfo peer)
        {
            if (!string.IsNullOrWhiteSpace(peer.StatusText))
            {
                return peer.StatusText;
            }

            if (peer.IsChatReady)
            {
                return "チャット準備完了";
            }

            if (peer.IsHelloVerified)
            {
                return "HELLO確認済み";
            }

            if (peer.IsTcpConnected)
            {
                return "TCP接続済み";
            }

            if (peer.IsPreparingChatTcp)
            {
                return "TCP準備中";
            }

            if (peer.IsConnectingWiFiDirect)
            {
                return "Wi-Fi Direct接続中";
            }

            if (peer.IsConnected)
            {
                return "Wi-Fi Direct接続済み";
            }

            return "接続前";
        }

        public static double GetProgressValue(PeerInfo peer)
        {
            if (peer.IsChatReady)
            {
                return 100;
            }

            if (peer.IsHelloVerified)
            {
                return 84;
            }

            if (peer.IsTcpConnected)
            {
                return 68;
            }

            if (peer.IsPreparingChatTcp || peer.IsConnectingWiFiDirect || peer.IsConnected)
            {
                return 48;
            }

            if (peer.DiscoveredByBle || peer.DiscoveredByWiFiDirect)
            {
                return 24;
            }

            return 10;
        }

        public static string GetDisplayValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        public static string CreateInitials(string displayName)
        {
            string normalized = displayName.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "--";
            }

            string[] parts = normalized
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
            }

            return normalized.Length <= 2
                ? normalized.ToUpperInvariant()
                : normalized[..2].ToUpperInvariant();
        }
    }
}
