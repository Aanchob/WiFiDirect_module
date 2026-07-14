using direct_module.Services;
using direct_module.WiFiDirect.Models;

Run("different names stay unmatched", () =>
{
    PeerMatchEvaluation result = PeerMatchService.Evaluate(
        Ble("K_HA", "7a1a"),
        Wifi("MP5055JPN_c5d9"),
        roleCompatible: true);
    Equal(PeerMatchState.Unmatched, result.State);
});

Run("single registry candidate is not merged", () =>
{
    PeerRegistryService registry = RegistryForLocalClient();
    registry.Register(Ble("K_HA", "7a1a"));
    PeerRegistrationResult result = registry.Register(Wifi("MP5055JPN_c5d9"));
    Equal(PeerRegistrationKind.Added, result.Kind);
    Equal(2, registry.Peers.Count);
});

Run("partial names stay unmatched", () =>
{
    PeerMatchEvaluation result = PeerMatchService.Evaluate(
        Ble("ACHO", "1111"),
        Wifi("ACHO_TEST"),
        roleCompatible: true);
    Equal(PeerMatchState.Unmatched, result.State);
    True(result.IsPartialNameCandidate);
});

Run("role conflict prevents provisional match", () =>
{
    PeerMatchEvaluation result = PeerMatchService.Evaluate(
        Ble("SAME_NAME", "1212"),
        Wifi("SAME_NAME"),
        roleCompatible: false);
    Equal(PeerMatchState.Unmatched, result.State);
    True(result.IsRoleConflict);
});

Run("exact name is provisional and does not overwrite DeviceId", () =>
{
    PeerInfo peer = Ble("K_HARUKI", "2222");
    PeerInfo candidate = Wifi("K_HARUKI", "wifi-device-1");
    PeerMatchEvaluation result = PeerMatchService.Evaluate(peer, candidate, roleCompatible: true);
    Equal(PeerMatchState.Provisional, result.State);
    Equal(55, result.Score);

    PeerMergeService.ApplyProvisional(peer, candidate, result.Reason, result.Score);
    Equal("", peer.DeviceId);
    Equal("wifi-device-1", peer.PendingWiFiDirectDeviceId);

    PeerMergeService.ConfirmAfterHello(peer, "HELLO confirmed");
    Equal(PeerMatchState.Confirmed, peer.MatchState);
    Equal("wifi-device-1", peer.DeviceId);
    Equal("", peer.PendingWiFiDirectDeviceId);
});

Run("registry keeps an exact-name DeviceId provisional", () =>
{
    PeerRegistryService registry = RegistryForLocalClient();
    registry.Register(Ble("K_HARUKI", "2323"));
    PeerRegistrationResult result = registry.Register(Wifi("K_HARUKI", "wifi-device-3"));
    Equal(PeerRegistrationKind.Provisional, result.Kind);
    Equal("", result.Peer.DeviceId);
    Equal("wifi-device-3", result.Peer.PendingWiFiDirectDeviceId);
    Equal(1, registry.Peers.Count);
});

Run("Wi-Fi-first discovery also keeps DeviceId provisional", () =>
{
    PeerRegistryService registry = RegistryForLocalClient();
    registry.Register(Wifi("ORDER_TEST", "wifi-device-4"));
    PeerRegistrationResult result = registry.Register(Ble("ORDER_TEST", "2424"));
    Equal(PeerRegistrationKind.Provisional, result.Kind);
    Equal("", result.Peer.DeviceId);
    Equal("wifi-device-4", result.Peer.PendingWiFiDirectDeviceId);
    Equal("2424", result.Peer.ShortSessionId);
});

Run("matching ShortSessionId is confirmed", () =>
{
    PeerMatchEvaluation result = PeerMatchService.Evaluate(
        Ble("BLE_NAME", "7a1a"),
        Wifi("WIFI_NAME", shortSessionId: "7a1a"),
        roleCompatible: true);
    Equal(PeerMatchState.Confirmed, result.State);
    Equal(100, result.Score);
});

Run("matching ShortSessionId wins over a refreshed DeviceId", () =>
{
    PeerInfo existing = Ble("BLE_NAME", "7b2b");
    existing.DeviceId = "old-device";
    PeerInfo incoming = Wifi("WIFI_NAME", "new-device", "7b2b");
    PeerMatchEvaluation result = PeerMatchService.Evaluate(existing, incoming, roleCompatible: true);
    Equal(PeerMatchState.Confirmed, result.State);
    Equal(100, result.Score);
});

Run("rejected provisional clears pending identity", () =>
{
    PeerInfo peer = Ble("SAME_NAME", "3333");
    PeerMergeService.ApplyProvisional(peer, Wifi("SAME_NAME", "wifi-device-2"), "weak match", 55);
    PeerMergeService.RejectProvisional(peer, "HELLO mismatch");
    Equal(PeerMatchState.Rejected, peer.MatchState);
    Equal("", peer.PendingWiFiDirectDeviceId);
    True(!peer.IsHelloVerified && !peer.IsChatReady);
});

Console.WriteLine("All peer matching checks passed.");

static PeerInfo Ble(string name, string shortSessionId)
{
    return new PeerInfo
    {
        DisplayName = name,
        BleName = name,
        DiscoveredByBle = true,
        ShortSessionId = shortSessionId,
        MatchKey = shortSessionId,
        RoleKey = $"{shortSessionId}abcd"
    };
}

static PeerInfo Wifi(string name, string deviceId = "wifi-device", string shortSessionId = "")
{
    return new PeerInfo
    {
        DisplayName = name,
        WiFiDirectName = name,
        DeviceId = deviceId,
        DiscoveredByWiFiDirect = true,
        ShortSessionId = shortSessionId,
        MatchKey = shortSessionId
    };
}

static PeerRegistryService RegistryForLocalClient()
{
    return new PeerRegistryService(new ConnectionRoleService("local", "00000000"));
}

static void Run(string name, Action test)
{
    test();
    Console.WriteLine($"PASS: {name}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected condition to be true.");
}
