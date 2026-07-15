using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;
using Windows.Networking;

namespace direct_module.WiFiDirect;

public sealed class WiFiDirectConnector
{
    private const int ConnectRetryCount = 3;
    private const int MaximumDeviceIdLength = 4096;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(15);

    private readonly object _lifetimeGate = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private CancellationTokenSource _lifetimeCts = new();
    private int _lifetimeGeneration;

    public event Action<string>? LogReceived;
    internal event Func<WiFiDirectSession, bool>? Connected;

    public async Task<bool> ConnectAsync(PeerInfo peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        LogPeer("Wi-Fi Direct connection starting", peer);

        if (!IsValidDeviceId(peer.DeviceId))
        {
            SafeLog("Wi-Fi Direct connection rejected because DeviceId is invalid.");
            return false;
        }

        if (IsPendingRequestDeviceId(peer.DeviceId))
        {
            SafeLog("A pending-request DeviceId can only be used by the incoming accept path.");
            return false;
        }

        using CancellationTokenSource operation = CreateOperationCancellation(
            cancellationToken,
            out int operationGeneration);
        bool entered = false;
        try
        {
            await _connectionGate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;

            for (int attempt = 1; attempt <= ConnectRetryCount; attempt++)
            {
                operation.Token.ThrowIfCancellationRequested();
                WiFiDirectPairingProcedure pairingProcedure = attempt == 1
                    ? WiFiDirectPairingProcedure.GroupOwnerNegotiation
                    : WiFiDirectPairingProcedure.Invitation;

                try
                {
                    SafeLog($"Wi-Fi Direct connection attempt {attempt}/{ConnectRetryCount}.");
                    WiFiDirectDevice? device = await CreateDeviceFromIdAsync(
                        peer.DeviceId,
                        preferClientRole: true,
                        pairingProcedure,
                        operation.Token).ConfigureAwait(false);

                    if (device != null && CompleteConnectionIfCurrent(
                            peer,
                            device,
                            WiFiDirectConnectionDirection.Outgoing,
                            "Wi-Fi Direct connection succeeded",
                            operationGeneration,
                            operation.Token))
                    {
                        return true;
                    }
                }
                catch (TimeoutException ex)
                {
                    SafeLog($"Wi-Fi Direct connection attempt {attempt} timed out: {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailure("Wi-Fi Direct connection attempt failed", ex, peer);
                }

                if (attempt < ConnectRetryCount)
                {
                    await Task.Delay(ConnectRetryDelay, operation.Token).ConfigureAwait(false);
                }
            }

            SafeLog("Wi-Fi Direct connection failed after all retry attempts.");
            return false;
        }
        catch (OperationCanceledException)
        {
            SafeLog("Wi-Fi Direct connection was canceled.");
            return false;
        }
        finally
        {
            if (entered) _connectionGate.Release();
        }
    }

    public async Task<bool> AcceptIncomingConnectionAsync(
        PeerInfo peer,
        WiFiDirectConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);
        LogPeer("Wi-Fi Direct incoming connection acceptance starting", peer);

        if (!IsValidDeviceId(peer.DeviceId) || !IsPendingRequestDeviceId(peer.DeviceId))
        {
            SafeLog("Incoming Wi-Fi Direct connection rejected because it is not a pending-request DeviceId.");
            return false;
        }

        string requestDeviceId = request.DeviceInformation?.Id ?? "";
        if (!IsValidDeviceId(requestDeviceId))
        {
            SafeLog("Incoming Wi-Fi Direct connection rejected because the request DeviceId is invalid.");
            return false;
        }

        using CancellationTokenSource operation = CreateOperationCancellation(
            cancellationToken,
            out int operationGeneration);
        bool entered = false;
        try
        {
            await _connectionGate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;

            WiFiDirectDevice? device = await CreateDeviceFromIdAsync(
                requestDeviceId,
                preferClientRole: false,
                pairingProcedure: null,
                operation.Token).ConfigureAwait(false);

            return device != null && CompleteConnectionIfCurrent(
                peer,
                device,
                WiFiDirectConnectionDirection.Incoming,
                "Incoming Wi-Fi Direct connection accepted",
                operationGeneration,
                operation.Token);
        }
        catch (TimeoutException ex)
        {
            SafeLog($"Incoming Wi-Fi Direct connection timed out: {ex.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            SafeLog("Incoming Wi-Fi Direct connection acceptance was canceled.");
            return false;
        }
        catch (Exception ex)
        {
            LogFailure("Incoming Wi-Fi Direct connection acceptance failed", ex, peer);
            return false;
        }
        finally
        {
            if (entered) _connectionGate.Release();
        }
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource previous;
        lock (_lifetimeGate)
        {
            previous = _lifetimeCts;
            _lifetimeCts = new CancellationTokenSource();
            _lifetimeGeneration++;
        }

        try
        {
            previous.Cancel();
        }
        catch (Exception ex)
        {
            SafeLog($"Canceling Wi-Fi Direct operations raised {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            previous.Dispose();
        }

        SafeLog("Pending Wi-Fi Direct operations were canceled.");
    }

    private async Task<WiFiDirectDevice?> CreateDeviceFromIdAsync(
        string deviceId,
        bool preferClientRole,
        WiFiDirectPairingProcedure? pairingProcedure,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectAttemptTimeout);

        try
        {
            if (preferClientRole || pairingProcedure.HasValue)
            {
                var parameters = new WiFiDirectConnectionParameters
                {
                    GroupOwnerIntent = preferClientRole ? (short)0 : (short)7
                };
                parameters.PreferenceOrderedConfigurationMethods.Add(WiFiDirectConfigurationMethod.PushButton);
                if (pairingProcedure.HasValue)
                {
                    parameters.PreferredPairingProcedure = pairingProcedure.Value;
                }

                return await WiFiDirectDevice.FromIdAsync(deviceId, parameters)
                    .AsTask(timeout.Token)
                    .ConfigureAwait(false);
            }

            return await WiFiDirectDevice.FromIdAsync(deviceId)
                .AsTask(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"FromIdAsync did not finish within {ConnectAttemptTimeout.TotalSeconds:0} seconds.");
        }
    }

    private bool CompleteConnection(
        PeerInfo peer,
        WiFiDirectDevice device,
        WiFiDirectConnectionDirection direction,
        string successMessage,
        ICollection<string> pendingLogs)
    {
        WiFiDirectSession? session = null;
        try
        {
            if (device.ConnectionStatus != WiFiDirectConnectionStatus.Connected)
            {
                pendingLogs.Add($"{successMessage} rejected: OS connection status is {device.ConnectionStatus}.");
                DisposeDeviceSafely(device, pendingLogs);
                return false;
            }

            var endpoint = device.GetConnectionEndpointPairs()
                .Where(item => IsUsableRemoteHost(item.RemoteHostName))
                .OrderBy(item => item.RemoteHostName.Type == HostNameType.Ipv4 ? 0 : 1)
                .FirstOrDefault();

            if (endpoint == null)
            {
                pendingLogs.Add($"{successMessage} rejected: no usable remote endpoint was returned.");
                DisposeDeviceSafely(device, pendingLogs);
                return false;
            }

            string remoteIpAddress = endpoint.RemoteHostName.DisplayName;
            session = new WiFiDirectSession(peer, device, direction, remoteIpAddress);
            Func<WiFiDirectSession, bool>? connected = Connected;
            if (connected == null)
            {
                pendingLogs.Add("Wi-Fi Direct session rejected because it has no owner.");
                session.Dispose();
                return false;
            }

            if (connected.GetInvocationList().Length != 1 || !connected.Invoke(session))
            {
                pendingLogs.Add("Wi-Fi Direct session was rejected by its owner.");
                session.Dispose();
                return false;
            }
            session = null; // Ownership was transferred to the manager.

            pendingLogs.Add(successMessage);
            pendingLogs.Add($"Wi-Fi Direct remote IP address: {remoteIpAddress}");
            return true;
        }
        catch
        {
            if (session != null)
            {
                try { session.Dispose(); }
                catch (Exception ex)
                {
                    pendingLogs.Add($"Wi-Fi Direct session disposal failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                DisposeDeviceSafely(device, pendingLogs);
            }

            throw;
        }
    }

    private bool CompleteConnectionIfCurrent(
        PeerInfo peer,
        WiFiDirectDevice device,
        WiFiDirectConnectionDirection direction,
        string successMessage,
        int operationGeneration,
        CancellationToken cancellationToken)
    {
        var pendingLogs = new List<string>();
        try
        {
            lock (_lifetimeGate)
            {
                if (operationGeneration != _lifetimeGeneration || cancellationToken.IsCancellationRequested)
                {
                    DisposeDeviceSafely(device, pendingLogs);
                    throw new OperationCanceledException(cancellationToken);
                }

                // Ownership is transferred while holding the same generation gate used by
                // CancelPendingOperations. This prevents an old manager lifetime from being
                // accepted after Stop followed immediately by Start.
                return CompleteConnection(peer, device, direction, successMessage, pendingLogs);
            }
        }
        finally
        {
            foreach (string message in pendingLogs) SafeLog(message);
        }
    }

    private CancellationTokenSource CreateOperationCancellation(
        CancellationToken cancellationToken,
        out int operationGeneration)
    {
        lock (_lifetimeGate)
        {
            operationGeneration = _lifetimeGeneration;
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        }
    }

    private static bool IsUsableRemoteHost(HostName? hostName)
    {
        return hostName != null &&
               !string.IsNullOrWhiteSpace(hostName.DisplayName) &&
               hostName.Type is HostNameType.Ipv4 or HostNameType.Ipv6;
    }

    private static bool IsValidDeviceId(string? deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) &&
               deviceId.Length <= MaximumDeviceIdLength &&
               !deviceId.Any(char.IsControl);
    }

    private void LogPeer(string title, PeerInfo peer)
    {
        SafeLog(title);
        SafeLog($"Target name: {FormatLogValue(peer.DisplayName)}");
        SafeLog($"Target DeviceId: {FormatLogValue(peer.DeviceId)}");
    }

    private void LogFailure(string title, Exception ex, PeerInfo peer)
    {
        SafeLog(title);
        SafeLog($"Exception: {ex.GetType().Name}");
        SafeLog($"HResult: 0x{ex.HResult:X8}");
        SafeLog($"Message: {FormatLogValue(ex.Message)}");
        SafeLog($"Target name: {FormatLogValue(peer.DisplayName)}");
        SafeLog($"Target DeviceId: {FormatLogValue(peer.DeviceId)}");
    }

    private static bool IsPendingRequestDeviceId(string deviceId) =>
        deviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string sanitized = new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return sanitized.Length <= 256 ? sanitized : sanitized[..256] + "...";
    }

    private static void DisposeDeviceSafely(WiFiDirectDevice device, ICollection<string> pendingLogs)
    {
        try { device.Dispose(); }
        catch (Exception ex)
        {
            pendingLogs.Add($"Wi-Fi Direct device disposal failed: {ex.GetType().Name}: {ex.Message}");
        }
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
                Debug.WriteLine($"WiFiDirectConnector log handler failed: {ex}");
            }
        }
    }
}
