using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect;

public enum WiFiDirectScanSelectorType
{
    Default,
    AssociationEndpoint
}

public sealed class WiFiDirectScanner
{
    private const int MaximumTrackedDevices = 512;
    private const int MaximumUnidentifiedDevices = 64;
    private const int MaximumDeviceIdCharacters = 4096;
    private const int MaximumNewDevicesPerMinute = 512;
    private const int MaximumUpdateEventsPerMinute = 600;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerInfo> _publishedPeers = new(StringComparer.OrdinalIgnoreCase);
    private DeviceWatcher? _watcher;
    private CancellationTokenSource? _scanTimeoutCts;
    private int _generation;
    private long _lastInvalidDeviceLog;
    private long _lastCapacityLog;
    private long _lastEventRateLog;
    private long _eventWindowStartedAt = Stopwatch.GetTimestamp();
    private int _newDevicesInWindow;
    private int _updatesInWindow;

    public event Action<string>? LogReceived;
    public event Action<PeerInfo>? PeerFound;
    public event Action<PeerInfo>? PeerRemoved;

    public Task StartAsync(int scanSeconds = 30) =>
        StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);

    public Task StartDefaultAsync(int scanSeconds = 30) =>
        StartAsync(WiFiDirectScanSelectorType.Default, scanSeconds);

    public Task StartAssociationEndpointAsync(int scanSeconds = 0) =>
        StartAsync(WiFiDirectScanSelectorType.AssociationEndpoint, scanSeconds);

    public Task StartAsync(WiFiDirectScanSelectorType selectorType, int scanSeconds = 30)
    {
        if (scanSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanSeconds));
        }

        DeviceWatcher? watcher = null;
        DeviceWatcher? previousWatcher = null;
        CancellationTokenSource? previousTimeout = null;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? failedTimeout = null;
        Exception? startError = null;
        int generation = 0;
        string? alreadyRunningMessage = null;
        var pendingLogs = new List<string>();
        var removedFromPreviousWatcher = new List<PeerInfo>();

        try
        {
            lock (_gate)
            {
                if (_watcher != null && IsActive(_watcher.Status))
                {
                    alreadyRunningMessage = $"Wi-Fi Direct探索はすでに起動中です: Status={_watcher.Status}";
                }
                else
                {
                    if (_watcher != null)
                    {
                        previousWatcher = _watcher;
                        DetachWatcherLocked(previousWatcher);
                        _watcher = null;
                    }

                    previousTimeout = _scanTimeoutCts;
                    _scanTimeoutCts = null;
                    removedFromPreviousWatcher.AddRange(_publishedPeers.Values);
                    _devices.Clear();
                    _appDeviceIds.Clear();
                    _publishedPeers.Clear();
                    generation = ++_generation;
                    _eventWindowStartedAt = Stopwatch.GetTimestamp();
                    _newDevicesInWindow = 0;
                    _updatesInWindow = 0;

                    string selector = selectorType == WiFiDirectScanSelectorType.AssociationEndpoint
                        ? WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint)
                        : WiFiDirectDevice.GetDeviceSelector();

                    watcher = DeviceInformation.CreateWatcher(selector);
                    watcher.Added += OnDeviceAdded;
                    watcher.Updated += OnDeviceUpdated;
                    watcher.Removed += OnDeviceRemoved;
                    watcher.EnumerationCompleted += OnEnumerationCompleted;
                    watcher.Stopped += OnStopped;
                    _watcher = watcher;

                    if (scanSeconds > 0)
                    {
                        timeout = new CancellationTokenSource();
                        _scanTimeoutCts = timeout;
                    }

                    pendingLogs.Add("Wi-Fi Direct探索開始");
                    pendingLogs.Add($"Selector Type: {selectorType}");
                    pendingLogs.Add($"探索時間: {(scanSeconds == 0 ? "継続" : $"{scanSeconds}秒")}");

                    try
                    {
                        watcher.Start();
                        pendingLogs.Add($"Watcher Status after Start: {watcher.Status}");
                        if (timeout != null)
                        {
                            _ = StopAfterDelayAsync(watcher, generation, scanSeconds, timeout.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        startError = ex;
                        DetachWatcherLocked(watcher);
                        if (ReferenceEquals(_watcher, watcher))
                        {
                            _watcher = null;
                            _generation++;
                        }

                        failedTimeout = _scanTimeoutCts;
                        _scanTimeoutCts = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            startError = ex;
            lock (_gate)
            {
                if (watcher != null)
                {
                    DetachWatcherLocked(watcher);
                }

                if (ReferenceEquals(_watcher, watcher))
                {
                    _watcher = null;
                    _generation++;
                }

                failedTimeout = _scanTimeoutCts;
                _scanTimeoutCts = null;
            }
        }

        if (alreadyRunningMessage != null)
        {
            SafeLog(alreadyRunningMessage);
            return Task.CompletedTask;
        }

        CancelAndDispose(previousTimeout);
        StopDetachedWatcher(previousWatcher);
        CancelAndDispose(failedTimeout);
        PublishRemovedPeers(removedFromPreviousWatcher);
        foreach (string message in pendingLogs) SafeLog(message);
        if (startError != null)
        {
            SafeLog($"Wi-Fi Direct探索エラー: {startError.GetType().Name}");
            SafeLog($"Message: {startError.Message}");
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        DeviceWatcher? watcher;
        int generation;

        lock (_gate)
        {
            watcher = _watcher;
            generation = _generation;
        }

        if (watcher == null)
        {
            SafeLog("Wi-Fi Direct探索は開始されていません");
            return;
        }

        ReleaseWatcher(watcher, generation, stopWatcher: true);
    }

    private async Task StopAfterDelayAsync(
        DeviceWatcher watcher,
        int generation,
        int scanSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(scanSeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SafeLog($"{scanSeconds}秒経過したのでWi-Fi Direct探索を停止します");
        ReleaseWatcher(watcher, generation, stopWatcher: true);
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
    {
        if (Monitor.IsEntered(_gate))
        {
            ThreadPool.QueueUserWorkItem(_ => OnDeviceAdded(sender, device));
            return;
        }

        if (!IsAcceptableDeviceId(device.Id))
        {
            LogInvalidDeviceId();
            return;
        }

        bool capacityReached = false;
        bool eventRateReached = false;
        bool hasAppInformation = DchatInformationElement.TryParse(device, out _);
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender))
            {
                return;
            }

            bool isNew = !_devices.ContainsKey(device.Id);
            if (isNew && !hasAppInformation &&
                _devices.Count - _appDeviceIds.Count >= MaximumUnidentifiedDevices)
            {
                capacityReached = true;
            }
            if (isNew && _devices.Count >= MaximumTrackedDevices)
            {
                if (hasAppInformation)
                {
                    string? unidentifiedId = _devices.Keys.FirstOrDefault(id => !_appDeviceIds.Contains(id));
                    if (unidentifiedId != null)
                    {
                        _devices.Remove(unidentifiedId);
                        _publishedPeers.Remove(unidentifiedId);
                    }
                    else
                    {
                        capacityReached = true;
                    }
                }
                else
                {
                    capacityReached = true;
                }
            }
            if (!capacityReached)
            {
                ResetEventWindowIfNeeded(Stopwatch.GetTimestamp());
                if (isNew)
                {
                    if (_newDevicesInWindow >= MaximumNewDevicesPerMinute)
                    {
                        eventRateReached = true;
                    }
                    else
                    {
                        _newDevicesInWindow++;
                        _devices[device.Id] = device;
                        if (hasAppInformation) _appDeviceIds.Add(device.Id);
                    }
                }
                else if (_updatesInWindow >= MaximumUpdateEventsPerMinute)
                {
                    eventRateReached = true;
                    _devices[device.Id] = device;
                }
                else
                {
                    _updatesInWindow++;
                    _devices[device.Id] = device;
                    if (hasAppInformation) _appDeviceIds.Add(device.Id);
                }
            }
        }

        if (capacityReached)
        {
            LogCapacityReached();
            return;
        }
        if (eventRateReached)
        {
            LogEventRateReached();
            return;
        }

        PublishPeer(sender, device, "Added");
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (Monitor.IsEntered(_gate))
        {
            ThreadPool.QueueUserWorkItem(_ => OnDeviceUpdated(sender, update));
            return;
        }

        if (!IsAcceptableDeviceId(update.Id))
        {
            LogInvalidDeviceId();
            return;
        }

        DeviceInformation? device;
        bool eventRateReached = false;
        string? updateError = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender) || !_devices.TryGetValue(update.Id, out device))
            {
                return;
            }

            try
            {
                device.Update(update);
            }
            catch (Exception ex)
            {
                updateError = $"Wi-Fi Direct device update failed: {ex.GetType().Name}: {ex.Message}";
            }

            if (updateError == null)
            {
                ResetEventWindowIfNeeded(Stopwatch.GetTimestamp());
                if (_updatesInWindow >= MaximumUpdateEventsPerMinute)
                {
                    eventRateReached = true;
                }
                else
                {
                    _updatesInWindow++;
                }
            }
        }

        if (updateError != null)
        {
            SafeLog(updateError);
            return;
        }

        if (eventRateReached)
        {
            LogEventRateReached();
            return;
        }

        PublishPeer(sender, device, "Updated");
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (Monitor.IsEntered(_gate))
        {
            ThreadPool.QueueUserWorkItem(_ => OnDeviceRemoved(sender, update));
            return;
        }

        if (!IsAcceptableDeviceId(update.Id))
        {
            LogInvalidDeviceId();
            return;
        }

        DeviceInformation? removed;
        PeerInfo? publishedPeer;
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender) || !_devices.Remove(update.Id, out removed))
            {
                return;
            }

            _appDeviceIds.Remove(update.Id);
            _publishedPeers.Remove(update.Id, out publishedPeer);
        }

        SafeLog($"Removed: {FormatDeviceIdForLog(update.Id)}");
        if (publishedPeer != null)
        {
            InvokeSafely(PeerRemoved, publishedPeer, nameof(PeerRemoved));
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        if (Monitor.IsEntered(_gate))
        {
            ThreadPool.QueueUserWorkItem(_ => OnEnumerationCompleted(sender, args));
            return;
        }

        int count;
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender))
            {
                return;
            }

            count = _devices.Count;
        }

        SafeLog($"Wi-Fi Direct初回探索完了: Status={sender.Status}, CandidateCount={count}");
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        int generation;
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender))
            {
                return;
            }

            generation = _generation;
        }

        string status = sender.Status.ToString();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            ReleaseWatcher(sender, generation, stopWatcher: false);
            SafeLog($"Wi-Fi Direct探索停止: Status={status}");
        });
    }

    private void PublishPeer(DeviceWatcher sender, DeviceInformation device, string reason)
    {
        if (!TryCreatePeer(device, out PeerInfo peer))
        {
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, sender) || !_devices.ContainsKey(device.Id)) return;
            _appDeviceIds.Add(device.Id);
            _publishedPeers[device.Id] = peer;
        }
        SafeLog($"{reason}: Name={peer.DisplayName}, DeviceId={FormatDeviceIdForLog(peer.DeviceId)}, Identity={peer.MatchKey}, TcpPort={peer.TcpPort}");
        InvokeSafely(PeerFound, peer, nameof(PeerFound));
    }

    private static bool TryCreatePeer(DeviceInformation device, out PeerInfo peer)
    {
        bool hasAppInformation = DchatInformationElement.TryParse(device, out DchatInformation appInformation);
        if (!hasAppInformation)
        {
            peer = null!;
            return false;
        }

        string displayName = !string.IsNullOrWhiteSpace(appInformation.DisplayName)
            ? appInformation.DisplayName
            : string.IsNullOrWhiteSpace(device.Name)
                ? "Unknown Wi-Fi Direct device"
                : SanitizeDisplayName(device.Name);

        peer = new PeerInfo
        {
            DisplayName = displayName,
            WiFiDirectName = displayName,
            DeviceId = device.Id,
            DeviceKind = device.Kind.ToString(),
            IsEnabled = device.IsEnabled,
            DiscoveredByBle = false,
            DiscoveredByWiFiDirect = true,
            TcpPort = appInformation.TcpPort,
            ShortSessionId = appInformation.ShortSessionId,
            RoleKey = appInformation.RoleKey,
            MatchKey = appInformation.PeerIdentity,
            IsConnected = false,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };
        return true;
    }

    private void ReleaseWatcher(DeviceWatcher watcher, int generation, bool stopWatcher)
    {
        CancellationTokenSource? timeout;
        List<PeerInfo> removedPeers;
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, watcher) || _generation != generation)
            {
                return;
            }

            DetachWatcherLocked(watcher);
            timeout = _scanTimeoutCts;
            _scanTimeoutCts = null;
            _watcher = null;
            _devices.Clear();
            _appDeviceIds.Clear();
            removedPeers = _publishedPeers.Values.ToList();
            _publishedPeers.Clear();
            _generation++;
        }

        SafeLog("Wi-Fi Direct探索停止要求");
        CancelAndDispose(timeout);
        if (stopWatcher)
        {
            StopDetachedWatcher(watcher);
        }
        PublishRemovedPeers(removedPeers);
    }

    private void DetachWatcherLocked(DeviceWatcher watcher)
    {
        watcher.Added -= OnDeviceAdded;
        watcher.Updated -= OnDeviceUpdated;
        watcher.Removed -= OnDeviceRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        watcher.Stopped -= OnStopped;
    }

    private static bool IsActive(DeviceWatcherStatus status) =>
        status is DeviceWatcherStatus.Created or DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted;

    private void StopDetachedWatcher(DeviceWatcher? watcher)
    {
        if (watcher == null)
        {
            return;
        }

        try
        {
            if (IsActive(watcher.Status))
            {
                watcher.Stop();
            }
        }
        catch (Exception ex)
        {
            SafeLog($"Wi-Fi Direct探索停止エラー: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex)
        {
            SafeLog($"Wi-Fi Direct scan cancellation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { cancellation.Dispose(); }
            catch (Exception ex)
            {
                SafeLog($"Wi-Fi Direct scan cancellation disposal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void PublishRemovedPeers(IEnumerable<PeerInfo> peers)
    {
        foreach (PeerInfo peer in peers)
        {
            InvokeSafely(PeerRemoved, peer, nameof(PeerRemoved));
        }
    }

    private void InvokeSafely(Action<PeerInfo>? handlers, PeerInfo peer, string eventName)
    {
        if (handlers == null) return;
        foreach (Action<PeerInfo> handler in handlers.GetInvocationList().Cast<Action<PeerInfo>>())
        {
            try { handler(peer); }
            catch (Exception ex)
            {
                SafeLog($"{eventName} handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void LogInvalidDeviceId()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastInvalidDeviceLog != 0 &&
                Stopwatch.GetElapsedTime(_lastInvalidDeviceLog, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastInvalidDeviceLog = now;
        }

        SafeLog("Wi-Fi Direct candidate with an invalid DeviceId was ignored.");
    }

    private void LogCapacityReached()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastCapacityLog != 0 &&
                Stopwatch.GetElapsedTime(_lastCapacityLog, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastCapacityLog = now;
        }

        SafeLog("Wi-Fi Direct candidate limit reached; additional devices are ignored.");
    }

    private void LogEventRateReached()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastEventRateLog != 0 &&
                Stopwatch.GetElapsedTime(_lastEventRateLog, now) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastEventRateLog = now;
        }

        SafeLog("Wi-Fi Direct discovery event rate limit reached; excess updates are coalesced.");
    }

    private void ResetEventWindowIfNeeded(long now)
    {
        if (Stopwatch.GetElapsedTime(_eventWindowStartedAt, now) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _eventWindowStartedAt = now;
        _newDevicesInWindow = 0;
        _updatesInWindow = 0;
    }

    private void SafeLog(string message)
    {
        Action<string>? handlers = LogReceived;
        if (handlers == null) return;
        foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WiFiDirectScanner log handler failed: {ex}");
            }
        }
    }

    private static bool IsAcceptableDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        deviceId.Length <= MaximumDeviceIdCharacters &&
        !deviceId.Any(character => char.IsControl(character));

    private static string FormatDeviceIdForLog(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "(empty)";
        var builder = new StringBuilder(Math.Min(deviceId.Length, 256));
        foreach (Rune rune in deviceId.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or
                System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.LineSeparator or
                System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > 256) break;
            builder.Append(rune);
        }
        return builder.Length == 0 ? "(redacted)" : builder.ToString();
    }

    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown Wi-Fi Direct device";
        var builder = new StringBuilder(Math.Min(value.Length, 256));
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or
                System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.LineSeparator or
                System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            if (builder.Length + rune.Utf16SequenceLength > 256) break;
            builder.Append(rune);
        }
        string sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "Unknown Wi-Fi Direct device" : sanitized;
    }
}
