using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Windows.Devices.WiFiDirect;
using direct_module.WiFiDirect.Models;

namespace direct_module.WiFiDirect;

public sealed class WiFiDirectListener
{
    private readonly object _gate = new();
    private readonly WiFiDirectConnectionListener _listener;
    private bool _isStarted;
    private long _lastRequestLog;

    public event Action<string>? LogReceived;
    public event Action<PeerInfo>? ConnectionRequested;
    public event Action<PeerInfo, WiFiDirectConnectionRequest>? IncomingConnectionRequested;

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _isStarted;
            }
        }
    }

    public WiFiDirectListener()
    {
        _listener = new WiFiDirectConnectionListener();
    }

    public bool Start()
    {
        string logMessage;
        bool started;
        lock (_gate)
        {
            if (_isStarted)
            {
                logMessage = "Wi-Fi Direct Listener はすでに起動中です";
                started = true;
            }
            else
            {
                try
                {
                    _listener.ConnectionRequested += OnConnectionRequested;
                    _isStarted = true;
                    logMessage = "Wi-Fi Direct接続要求待ち受け中";
                    started = true;
                }
                catch (Exception ex)
                {
                    _listener.ConnectionRequested -= OnConnectionRequested;
                    logMessage = $"Wi-Fi Direct Listener起動失敗: {ex.GetType().Name}: {ex.Message}";
                    started = false;
                }
            }
        }

        SafeLog(logMessage);
        return started;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_isStarted)
            {
                return;
            }

            _isStarted = false;
            _listener.ConnectionRequested -= OnConnectionRequested;
        }

        SafeLog("Wi-Fi Direct Listener停止");
    }

    private void OnConnectionRequested(
        WiFiDirectConnectionListener sender,
        WiFiDirectConnectionRequestedEventArgs args)
    {
        WiFiDirectConnectionRequest? request = null;

        try
        {
            if (!IsStarted)
            {
                return;
            }

            request = args.GetConnectionRequest();
            if (!IsStarted)
            {
                DisposeRequestSafely(request);
                return;
            }

            var deviceInfo = request.DeviceInformation;
            string deviceId = deviceInfo.Id?.Trim() ?? "";
            if (!IsAcceptableDeviceId(deviceId))
            {
                LogRequest("Wi-Fi Direct接続要求を拒否: DeviceIdが不正です");
                DisposeRequestSafely(request);
                return;
            }

            bool hasAppInformation = DchatInformationElement.TryParse(deviceInfo, out DchatInformation appInformation);
            string displayName = hasAppInformation && !string.IsNullOrWhiteSpace(appInformation.DisplayName)
                ? appInformation.DisplayName
                : string.IsNullOrWhiteSpace(deviceInfo.Name)
                    ? "Unknown Wi-Fi Direct device"
                    : SanitizeDisplayName(deviceInfo.Name);

            LogRequest(
                $"Wi-Fi Direct接続要求を受信: Name={FormatName(deviceInfo.Name)}, " +
                $"Kind={deviceInfo.Kind}, Enabled={deviceInfo.IsEnabled}");

            PeerInfo peer = new PeerInfo
            {
                DisplayName = displayName,
                WiFiDirectName = displayName,
                DeviceId = deviceId,
                DeviceKind = deviceInfo.Kind.ToString(),
                IsEnabled = deviceInfo.IsEnabled,
                DiscoveredByBle = false,
                DiscoveredByWiFiDirect = true,
                MatchKey = hasAppInformation ? appInformation.PeerIdentity : "",
                ShortSessionId = hasAppInformation ? appInformation.ShortSessionId : "",
                RoleKey = hasAppInformation ? appInformation.RoleKey : "",
                TcpPort = hasAppInformation ? appInformation.TcpPort : 0,
                IsConnected = false
            };

            InvokeSafely(ConnectionRequested, peer);

            Action<PeerInfo, WiFiDirectConnectionRequest>? incomingHandler = IncomingConnectionRequested;
            if (incomingHandler == null)
            {
                SafeLog("IncomingConnectionRequested購読なし: 接続要求を破棄します");
                request.Dispose();
                return;
            }

            // このrequestはaccept処理が終わるまで破棄しない。
            // _PendingRequestのFromIdAsyncは、元のWiFiDirectConnectionRequestを保持した状態で行う。
            Delegate[] incomingHandlers = incomingHandler.GetInvocationList();
            if (incomingHandlers.Length != 1)
            {
                SafeLog("Incoming Wi-Fi Direct request rejected because ownership is ambiguous.");
                DisposeRequestSafely(request);
                return;
            }
            ((Action<PeerInfo, WiFiDirectConnectionRequest>)incomingHandlers[0]).Invoke(peer, request);
            request = null;
        }
        catch (Exception ex)
        {
            SafeLog($"ConnectionRequested処理失敗: {ex.GetType().Name}");
            SafeLog($"HResult: 0x{ex.HResult:X8}");
            SafeLog($"Message: {ex.Message}");
            DisposeRequestSafely(request);
        }
    }

    private static string FormatName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "(empty)" : SanitizeDisplayName(name);
    }

    private void LogRequest(string message)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_lastRequestLog != 0 &&
                Stopwatch.GetElapsedTime(_lastRequestLog, now) < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastRequestLog = now;
        }

        SafeLog(message);
    }

    private void DisposeRequestSafely(WiFiDirectConnectionRequest? request)
    {
        if (request == null) return;
        try
        {
            request.Dispose();
        }
        catch (Exception ex)
        {
            SafeLog($"Wi-Fi Direct request disposal failed: {ex.GetType().Name}: {ex.Message}");
        }
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
                Debug.WriteLine($"WiFiDirectListener log handler failed: {ex}");
            }
        }
    }

    private static bool IsAcceptableDeviceId(string deviceId) =>
        deviceId.Length is > 0 and <= 4096 &&
        !deviceId.Any(character => char.IsControl(character));

    private static string SanitizeDisplayName(string value)
    {
        var builder = new StringBuilder(Math.Min(value?.Length ?? 0, 256));
        foreach (Rune rune in (value ?? "").EnumerateRunes())
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

    private void InvokeSafely(Action<PeerInfo>? handlers, PeerInfo peer)
    {
        if (handlers == null) return;
        foreach (Action<PeerInfo> handler in handlers.GetInvocationList().Cast<Action<PeerInfo>>())
        {
            try { handler(peer); }
            catch (Exception ex)
            {
                SafeLog($"Wi-Fi Direct connection request observer failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
