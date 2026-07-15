using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using direct_module.WiFiDirect.Models;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace direct_module.Discovery
{
    public sealed class BleScanner
    {
        private const ushort ManufacturerId = 0x1234;
        private const int MaximumTrackedPeers = 256;
        private const int MaximumNewPeersPerMinute = 240;
        private const int MaximumPublishedEventsPerMinute = 600;
        private static readonly TimeSpan PresencePublishInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PresenceExpiry = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PresenceSweepInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan InvalidAdvertisementLogInterval = TimeSpan.FromSeconds(30);

        private readonly object _gate = new();
        private readonly Dictionary<string, PresenceEntry> _presence = new(StringComparer.OrdinalIgnoreCase);
        private BluetoothLEAdvertisementWatcher? _watcher;
        private Timer? _presenceTimer;
        private int _generation;
        private bool _acceptingAdvertisements;
        private long _lastInvalidAdvertisementLog;
        private long _eventWindowStartedAt = Stopwatch.GetTimestamp();
        private int _newPeersInWindow;
        private int _publishedEventsInWindow;

        public event Action<string>? LogReceived;
        public event Action<PeerInfo>? PeerFound;
        public event Action<PeerInfo>? PeerRemoved;

        public void Start()
        {
            BluetoothLEAdvertisementWatcher? attemptedWatcher = null;
            List<PeerInfo> removedFromInactiveWatcher = new();
            var pendingLogs = new List<string>();
            string? alreadyRunningLog = null;

            try
            {
                lock (_gate)
                {
                    if (_watcher != null &&
                        (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created ||
                         _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started))
                    {
                        alreadyRunningLog = $"BLE scan is already active. Status={_watcher.Status}";
                    }
                    else
                    {
                        removedFromInactiveWatcher = ReleaseInactiveStateLocked();
                        int generation = ++_generation;
                        _acceptingAdvertisements = false;
                        _eventWindowStartedAt = Stopwatch.GetTimestamp();
                        _newPeersInWindow = 0;
                        _publishedEventsInWindow = 0;

                        var watcher = new BluetoothLEAdvertisementWatcher
                        {
                            ScanningMode = BluetoothLEScanningMode.Passive
                        };
                        attemptedWatcher = watcher;
                        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                            new BluetoothLEManufacturerData { CompanyId = ManufacturerId });
                        watcher.Received += OnReceived;
                        watcher.Stopped += OnStopped;
                        _watcher = watcher;

                        pendingLogs.Add($"BLE scan start requested. Status={watcher.Status}");
                        watcher.Start();
                        if (ReferenceEquals(_watcher, watcher))
                        {
                            _presenceTimer = new Timer(
                                static state => ((PresenceTimerState)state!).Scanner.ExpireStalePeers(
                                    ((PresenceTimerState)state).Generation),
                                new PresenceTimerState(this, generation),
                                PresenceSweepInterval,
                                PresenceSweepInterval);
                        }
                        pendingLogs.Add($"BLE scan start returned. Status={watcher.Status}");
                    }
                }

                if (alreadyRunningLog != null)
                {
                    SafeLog(alreadyRunningLog);
                    return;
                }

                // Publish removals before accepting advertisements from the replacement watcher.
                PublishRemovedPeers(removedFromInactiveWatcher, "BLE watcher restart");
                foreach (string message in pendingLogs) SafeLog(message);

                lock (_gate)
                {
                    if (ReferenceEquals(_watcher, attemptedWatcher))
                    {
                        _acceptingAdvertisements = true;
                    }
                }
            }
            catch (Exception ex)
            {
                PublishRemovedPeers(removedFromInactiveWatcher, "BLE watcher restart failed");
                SafeLog($"BLE scan start failed: {ex.GetType().Name}: {ex.Message}");
                if (attemptedWatcher != null) ReleaseWatcher(attemptedWatcher, stopWatcher: true);
            }
        }

        public void Stop()
        {
            BluetoothLEAdvertisementWatcher? watcher;
            lock (_gate) watcher = _watcher;
            if (watcher == null)
            {
                SafeLog("BLE scan is not active.");
                return;
            }

            ReleaseWatcher(watcher, stopWatcher: true);
        }

        private void OnReceived(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_watcher, sender) || !_acceptingAdvertisements) return;
            }

            foreach (BluetoothLEManufacturerData manufacturerData in
                     args.Advertisement.GetManufacturerDataByCompanyId(ManufacturerId))
            {
                BleAdvertisementData advertisement;
                try
                {
                    byte[] payload = ReadBuffer(manufacturerData.Data);
                    if (!BleAdvertisementPayload.TryParse(payload, out advertisement))
                    {
                        LogInvalidAdvertisement(payload.Length);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    LogInvalidAdvertisement(0, ex.GetType().Name);
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                DateTimeOffset seenAt = DateTimeOffset.UtcNow;
                string key = advertisement.PeerIdentity.Length == 24
                    ? advertisement.PeerIdentity
                    : $"{advertisement.PeerIdentity}:{args.BluetoothAddress:X12}";
                PeerInfo? removedPeer = null;
                PeerInfo? publishedPeer = null;

                lock (_gate)
                {
                    if (!ReferenceEquals(_watcher, sender) || !_acceptingAdvertisements) return;
                    if (!_presence.TryGetValue(key, out PresenceEntry? entry))
                    {
                        ResetEventWindowIfNeeded(now);
                        if (_newPeersInWindow >= MaximumNewPeersPerMinute) continue;
                        _newPeersInWindow++;
                        if (_presence.Count >= MaximumTrackedPeers)
                        {
                            KeyValuePair<string, PresenceEntry> oldest = _presence
                                .OrderBy(item => item.Value.LastSeenTimestamp)
                                .First();
                            _presence.Remove(oldest.Key);
                            removedPeer = oldest.Value.LastPublishedPeer;
                        }

                        entry = new PresenceEntry();
                        _presence.Add(key, entry);
                    }

                    entry.Advertisement = advertisement;
                    entry.LastSeenTimestamp = now;
                    entry.LastSeenAtUtc = seenAt;
                    if (entry.LastPublishedTimestamp == 0 ||
                        Stopwatch.GetElapsedTime(entry.LastPublishedTimestamp, now) >= PresencePublishInterval)
                    {
                        ResetEventWindowIfNeeded(now);
                        if (_publishedEventsInWindow < MaximumPublishedEventsPerMinute)
                        {
                            _publishedEventsInWindow++;
                            publishedPeer = CreatePeer(advertisement, seenAt);
                            entry.LastPublishedPeer = publishedPeer;
                            entry.LastPublishedTimestamp = now;
                        }
                    }
                }

                if (removedPeer != null) InvokeSafely(PeerRemoved, removedPeer);
                if (publishedPeer != null)
                {
                    SafeLog($"BLE peer found or updated: Name={publishedPeer.DisplayName}, ShortSessionId={publishedPeer.ShortSessionId}, Port={publishedPeer.TcpPort}, RoleKey={publishedPeer.RoleKey}");
                    InvokeSafely(PeerFound, publishedPeer);
                }
            }
        }

        private void ExpireStalePeers(int generation)
        {
            List<PeerInfo> expired = new();
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_generation != generation || _watcher == null) return;
                foreach ((string key, PresenceEntry entry) in _presence.ToList())
                {
                    if (Stopwatch.GetElapsedTime(entry.LastSeenTimestamp, now) < PresenceExpiry) continue;
                    _presence.Remove(key);
                    if (entry.LastPublishedPeer != null) expired.Add(entry.LastPublishedPeer);
                }
            }

            foreach (PeerInfo peer in expired)
            {
                SafeLog($"BLE peer expired: Name={peer.DisplayName}, Identity={peer.MatchKey}");
                InvokeSafely(PeerRemoved, peer);
            }
        }

        private void OnStopped(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            string error = args.Error.ToString();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                ReleaseWatcher(sender, stopWatcher: false);
                SafeLog($"BLE scan stopped: {error}");
            });
        }

        private void ReleaseWatcher(BluetoothLEAdvertisementWatcher watcher, bool stopWatcher)
        {
            Timer? timer;
            List<PeerInfo> removed;
            lock (_gate)
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
                if (!ReferenceEquals(_watcher, watcher)) return;

                _watcher = null;
                _acceptingAdvertisements = false;
                _generation++;
                timer = _presenceTimer;
                _presenceTimer = null;
                removed = _presence.Values
                    .Select(entry => entry.LastPublishedPeer)
                    .Where(peer => peer != null)
                    .Cast<PeerInfo>()
                    .ToList();
                _presence.Clear();
            }

            timer?.Dispose();
            if (stopWatcher)
            {
                try
                {
                    if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created or
                        BluetoothLEAdvertisementWatcherStatus.Started)
                    {
                        watcher.Stop();
                    }
                }
                catch (Exception ex)
                {
                    SafeLog($"BLE scan stop failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (PeerInfo peer in removed) InvokeSafely(PeerRemoved, peer);
        }

        private List<PeerInfo> ReleaseInactiveStateLocked()
        {
            List<PeerInfo> removed = _presence.Values
                .Select(entry => entry.LastPublishedPeer)
                .Where(peer => peer != null)
                .Cast<PeerInfo>()
                .ToList();
            if (_watcher != null)
            {
                _watcher.Received -= OnReceived;
                _watcher.Stopped -= OnStopped;
                _watcher = null;
            }
            _acceptingAdvertisements = false;
            _presenceTimer?.Dispose();
            _presenceTimer = null;
            _presence.Clear();
            return removed;
        }

        private void PublishRemovedPeers(IEnumerable<PeerInfo> peers, string reason)
        {
            foreach (PeerInfo peer in peers)
            {
                SafeLog($"{reason}: removed peer Name={peer.DisplayName}, Identity={peer.MatchKey}");
                InvokeSafely(PeerRemoved, peer);
            }
        }

        private void LogInvalidAdvertisement(int length, string? detail = null)
        {
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_lastInvalidAdvertisementLog != 0 &&
                    Stopwatch.GetElapsedTime(_lastInvalidAdvertisementLog, now) < InvalidAdvertisementLogInterval)
                {
                    return;
                }
                _lastInvalidAdvertisementLog = now;
            }
            SafeLog($"Invalid BLE advertisement ignored. Bytes={length}{(detail == null ? "" : $", Error={detail}")}");
        }

        private void ResetEventWindowIfNeeded(long now)
        {
            if (Stopwatch.GetElapsedTime(_eventWindowStartedAt, now) < TimeSpan.FromMinutes(1)) return;
            _eventWindowStartedAt = now;
            _newPeersInWindow = 0;
            _publishedEventsInWindow = 0;
        }

        private static PeerInfo CreatePeer(BleAdvertisementData advertisement, DateTimeOffset seenAt) => new()
        {
            DisplayName = advertisement.DisplayName,
            BleName = advertisement.DisplayName,
            DeviceId = "",
            DiscoveredByBle = true,
            ShortSessionId = advertisement.ShortSessionId,
            RoleKey = advertisement.RoleKey,
            MatchKey = advertisement.PeerIdentity,
            TcpPort = advertisement.TcpPort,
            IpAddress = "",
            IsConnected = false,
            LastSeenAtUtc = seenAt
        };

        private static byte[] ReadBuffer(IBuffer buffer)
        {
            byte[] bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }

        private void SafeLog(string message)
        {
            Action<string>? handlers = LogReceived;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
            {
                try { handler(message); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"BLE log handler failed: {ex}");
                }
            }
        }

        private static void InvokeSafely(Action<PeerInfo>? handlers, PeerInfo peer)
        {
            if (handlers == null) return;
            foreach (Action<PeerInfo> handler in handlers.GetInvocationList().Cast<Action<PeerInfo>>())
            {
                try { handler(peer); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"BLE peer event handler failed: {ex}");
                }
            }
        }

        private sealed class PresenceEntry
        {
            public BleAdvertisementData Advertisement { get; set; }
            public long LastSeenTimestamp { get; set; }
            public DateTimeOffset LastSeenAtUtc { get; set; }
            public long LastPublishedTimestamp { get; set; }
            public PeerInfo? LastPublishedPeer { get; set; }
        }

        private sealed record PresenceTimerState(BleScanner Scanner, int Generation);
    }
}
