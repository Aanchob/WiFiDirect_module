using System.Linq;
using Windows.Networking;
using Windows.Networking.Connectivity;

namespace direct_module.Network
{
    public static class LocalNetworkInfo
    {
        public static string GetLocalIpv4Address()
        {
            var hostNames = NetworkInformation.GetHostNames();

            var ipv4 = hostNames
                .Where(h => h.Type == HostNameType.Ipv4)
                .Select(h => h.CanonicalName)
                .FirstOrDefault(ip =>
                    !ip.StartsWith("127.") &&
                    !ip.StartsWith("169.254."));

            return ipv4 ?? "";
        }
    }
}