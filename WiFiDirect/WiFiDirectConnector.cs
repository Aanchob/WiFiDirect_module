using direct_module.WiFiDirect.Models;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFiDirect;

namespace direct_module.WiFiDirect
{
    public class WiFiDirectConnector
    {
        private const int ConnectRetryCount = 4;
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FromIdTimeout = TimeSpan.FromSeconds(15);
        private readonly SemaphoreSlim _outgoingConnectionGate = new(1, 1);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _acceptGates =
            new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? LogReceived;
        public event Action<WiFiDirectSession>? Connected;

        public async Task ConnectAsync(PeerInfo peer)
        {
            string connectionDeviceId = peer.WiFiDirectDeviceIdForConnection;
            if (string.IsNullOrWhiteSpace(connectionDeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct接続失敗: DeviceIdが空です");
                return;
            }

            if (IsPendingRequestDeviceId(connectionDeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct接続失敗: PendingRequest用DeviceIdは通常接続に使用できません");
                return;
            }

            await _outgoingConnectionGate.WaitAsync();
            try
            {
                LogReceived?.Invoke($"Wi-Fi Direct接続開始: Name={peer.DisplayName}, DeviceId={connectionDeviceId}");
                if (!await ConnectWithRetryAsync(peer, connectionDeviceId))
                {
                    LogReceived?.Invoke($"Wi-Fi Direct接続失敗: 再試行回数を超えました。Peer={peer.DisplayName}");
                }
            }
            finally
            {
                _outgoingConnectionGate.Release();
            }
        }

        private async Task<bool> ConnectWithRetryAsync(PeerInfo peer, string connectionDeviceId)
        {
            for (int attempt = 1; attempt <= ConnectRetryCount; attempt++)
            {
                WiFiDirectPairingProcedure pairingProcedure = attempt <= ConnectRetryCount / 2
                    ? WiFiDirectPairingProcedure.GroupOwnerNegotiation
                    : WiFiDirectPairingProcedure.Invitation;

                try
                {
                    LogReceived?.Invoke(
                        $"Wi-Fi Direct接続試行: Attempt={attempt}/{ConnectRetryCount}, Procedure={pairingProcedure}");
                    WiFiDirectDevice? device = await CreateDeviceFromIdAsync(
                        connectionDeviceId,
                        preferClientRole: true,
                        pairingProcedure: pairingProcedure);

                    if (device != null)
                    {
                        CompleteConnection(peer, device, "Wi-Fi Direct接続成功");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke(
                        $"Wi-Fi Direct接続試行失敗: Attempt={attempt}/{ConnectRetryCount}, " +
                        $"{ex.GetType().Name}: {ex.Message}, HResult=0x{ex.HResult:X8}");
                }

                if (attempt < ConnectRetryCount)
                {
                    await Task.Delay(ConnectRetryDelay);
                }
            }

            return false;
        }

        public async Task AcceptIncomingConnectionAsync(
            PeerInfo peer,
            WiFiDirectConnectionRequest request)
        {
            if (string.IsNullOrWhiteSpace(peer.DeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct Accept失敗: DeviceIdが空です");
                return;
            }

            if (!peer.IsIncomingConnectionRequest && !IsPendingRequestDeviceId(peer.DeviceId))
            {
                LogReceived?.Invoke("Wi-Fi Direct Accept失敗: 接続要求ではないDeviceIdです");
                return;
            }

            string requestDeviceId = request.DeviceInformation.Id;
            SemaphoreSlim acceptGate = _acceptGates.GetOrAdd(requestDeviceId, _ => new SemaphoreSlim(1, 1));
            // 同じ端末から重複した要求だけを直列化し、別端末からの参加要求は並行してAcceptする。
            await acceptGate.WaitAsync();
            try
            {
                LogReceived?.Invoke($"Wi-Fi Direct Accept開始: DeviceId={requestDeviceId}");
                WiFiDirectDevice? device = await CreateDeviceFromIdAsync(requestDeviceId);
                if (device == null)
                {
                    LogReceived?.Invoke("Wi-Fi Direct Accept失敗: FromIdAsyncがnullを返しました");
                    return;
                }

                CompleteConnection(peer, device, "Wi-Fi Direct Accept成功");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(
                    $"Wi-Fi Direct Accept失敗: {ex.GetType().Name}: {ex.Message}, HResult=0x{ex.HResult:X8}");
            }
            finally
            {
                acceptGate.Release();
            }
        }

        private async Task<WiFiDirectDevice?> CreateDeviceFromIdAsync(
            string deviceId,
            bool preferClientRole = false,
            WiFiDirectPairingProcedure? pairingProcedure = null)
        {
            using var timeout = new CancellationTokenSource(FromIdTimeout);
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

                    return await WiFiDirectDevice
                        .FromIdAsync(deviceId, parameters)
                        .AsTask(timeout.Token);
                }

                return await WiFiDirectDevice
                    .FromIdAsync(deviceId)
                    .AsTask(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"WiFiDirectDevice.FromIdAsyncが{FromIdTimeout.TotalSeconds:0}秒でタイムアウトしました");
            }
        }

        private void CompleteConnection(PeerInfo peer, WiFiDirectDevice device, string successMessage)
        {
            var endpoints = device.GetConnectionEndpointPairs();
            if (endpoints.Count == 0)
            {
                device.Dispose();
                throw new InvalidOperationException("Wi-Fi Direct接続後のEndpointが取得できませんでした");
            }

            string remoteIpAddress = "";
            foreach (var endpoint in endpoints)
            {
                string candidate = endpoint.RemoteHostName?.DisplayName ?? "";
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    remoteIpAddress = candidate;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(remoteIpAddress))
            {
                device.Dispose();
                throw new InvalidOperationException("Wi-Fi Direct接続後のRemoteHostNameが取得できませんでした");
            }

            peer.RemoteIpAddress = remoteIpAddress;
            peer.IsConnected = true;
            LogReceived?.Invoke($"{successMessage}: RemoteIpAddress={remoteIpAddress}, EndpointCount={endpoints.Count}");
            Connected?.Invoke(new WiFiDirectSession(peer, device));
        }

        private static bool IsPendingRequestDeviceId(string deviceId) =>
            deviceId.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase);
    }
}
