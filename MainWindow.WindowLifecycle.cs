using System;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(15);
        private string LocalPeerId => _localPeerId;
        private string LocalHistoryPeerId => _localIdentityReady
            ? _localPeerId
            : "peer:local-identity-unavailable";
        private AppWindow? _appWindow;
        private Task? _shutdownTask;
        private int _shutdownCompleted;

        private string GetLocalShortSessionId()
        {
            return _localSessionId.ToString("N")[..4];
        }

        private string GetLocalRoleKey()
        {
            return _localSessionId.ToString("N")[..8];
        }

        private string GetLocalDiscoveryIdentity()
        {
            return _localSessionId.ToString("N")[..24];
        }

        private void InitializeWindowLifecycle()
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += MainAppWindow_Closing;
            Closed += MainWindow_Closed;
        }

        private void MainAppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (Volatile.Read(ref _shutdownCompleted) != 0)
            {
                return;
            }

            // AppWindow has no asynchronous closing deferral. Keep the window alive
            // while all transports and owned tasks are shut down, then destroy it in
            // a second, explicitly allowed close pass.
            args.Cancel = true;
            StartWindowShutdown(sender);
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (_appWindow != null)
            {
                _appWindow.Closing -= MainAppWindow_Closing;
            }

            // Defensive fallback for close paths that do not raise AppWindow.Closing.
            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                StartWindowShutdown(appWindowToDestroy: null);
            }
        }

        private void StartWindowShutdown(AppWindow? appWindowToDestroy)
        {
            if (!TryBeginWindowShutdown())
            {
                return;
            }

            TryRunShutdownStep(
                _windowLifetimeCancellation.Cancel,
                "window lifetime cancellation");
            if (Content is FrameworkElement rootContent)
            {
                rootContent.Loaded -= MainWindow_LocalIdentityUnavailableLoaded;
            }
            BleRoleGenerationState? bleRoleGenerationState;
            bool disposeBleRoleGenerationState;
            lock (_bleRoleGenerationGate)
            {
                bleRoleGenerationState = _bleRoleGenerationState;
                if (bleRoleGenerationState != null)
                {
                    bleRoleGenerationState.IsRetired = true;
                    _bleRoleGenerationState = null;
                }
                disposeBleRoleGenerationState = bleRoleGenerationState is { LeaseCount: 0 };
            }
            if (bleRoleGenerationState != null)
            {
                TryRunShutdownStep(
                    bleRoleGenerationState.Cancel,
                    "BLE role cancellation");
                if (disposeBleRoleGenerationState)
                {
                    TryRunShutdownStep(
                        bleRoleGenerationState.Dispose,
                        "BLE role cancellation disposal");
                }
            }

            _manager.LogReceived -= OnLogReceived;
            _manager.ConnectionRequested -= OnConnectionRequested;
            _manager.IncomingConnectionApprovalAsync = null;
            _manager.PeerFound -= OnPeerFound;
            _manager.SessionConnected -= OnWiFiDirectSessionConnected;
            _manager.PeerRemoved -= OnWiFiDirectPeerRemoved;
            _manager.Disconnected -= OnWiFiDirectPeerRemoved;

            _discoveryManager.LogReceived -= OnLogReceived;
            _discoveryManager.PeerFound -= OnPeerFound;
            _discoveryManager.PeerRemoved -= OnBlePeerRemoved;
            _tcpServer.LogReceived -= OnLogReceived;
            _tcpServer.ConnectionAccepted -= OnTcpConnectionAccepted;
            _chatConnectionManager.LogReceived -= OnLogReceived;
            _chatConnectionManager.MessageReceived -= OnChatMessageReceived;
            _chatConnectionManager.ConnectionDisconnected -= OnChatConnectionDisconnected;
            _chatConnectionManager.ConnectionsChanged -= OnChatConnectionsChanged;
            _fileTransferService.LogReceived -= OnLogReceived;
            _fileTransferService.ProgressChanged -= OnFileTransferProgressChanged;

            // No individual synchronous transport failure may strand a canceled
            // window behind AppWindow.Closing's deferral-by-cancellation pattern.
            // The asynchronous owned-operation drain must always be started.
            TryRunShutdownStep(_discoveryManager.StopAdvertise, "BLE advertisement stop");
            TryRunShutdownStep(_discoveryManager.StopScan, "BLE scan stop");
            TryRunShutdownStep(_manager.Stop, "Wi-Fi Direct stop");
            TryRunShutdownStep(_tcpServer.Stop, "TCP listener stop");

            _shutdownTask = ShutdownAndCloseAsync(appWindowToDestroy);
        }

        private static void TryRunShutdownStep(Action step, string description)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Shutdown step failed ({description}): {ex}");
            }
        }

        private async Task ShutdownAndCloseAsync(AppWindow? appWindowToDestroy)
        {
            Task cleanup = CompleteResourceShutdownAsync();
            try
            {
                using var timeout = new CancellationTokenSource(GracefulShutdownTimeout);
                await cleanup.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Shutdown exceeded {GracefulShutdownTimeout.TotalSeconds:F0} seconds; closing the window best-effort.");
                // WaitAsync does not cancel the underlying cleanup. Continue to
                // observe it after the UI closes so a later transport/disposal
                // failure cannot surface as an unobserved task exception.
                _ = ObserveLateResourceShutdownAsync(cleanup);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Shutdown failed: {ex}");
            }
            finally
            {
                Volatile.Write(ref _shutdownCompleted, 1);

                if (appWindowToDestroy != null)
                {
                    if (DispatcherQueue.HasThreadAccess)
                    {
                        DestroyAppWindowBestEffort(appWindowToDestroy);
                    }
                    else
                    {
                        if (!DispatcherQueue.TryEnqueue(
                                () => DestroyAppWindowBestEffort(appWindowToDestroy)))
                        {
                            // A failed enqueue normally means the UI dispatcher is
                            // already shutting down. AppWindow is agile; make one
                            // final best-effort call so an otherwise live dispatcher
                            // edge case cannot leave the canceled close pass stranded.
                            DestroyAppWindowBestEffort(appWindowToDestroy);
                        }
                    }
                }
            }
        }

        private static void DestroyAppWindowBestEffort(AppWindow appWindow)
        {
            try
            {
                appWindow.Destroy();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Final window destruction failed: {ex}");
            }
        }

        private static async Task ObserveLateResourceShutdownAsync(Task cleanup)
        {
            try
            {
                await cleanup;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Late shutdown cleanup failed: {ex}");
            }
        }

        private async Task CompleteResourceShutdownAsync()
        {
            try
            {
                await Task.WhenAll(
                    _fileTransferService.AbortAllIncomingTransfersAsync(),
                    _chatConnectionManager.CloseAllAsync());
            }
            finally
            {
                try
                {
                    // Transport cleanup can fail. Background operations must still
                    // drain before the shared signing identity is released.
                    await DrainBackgroundOperationsAsync();
                }
                finally
                {
                    // If the UI's best-effort timeout expires, this continuation owns
                    // the shared signing identity until all users have actually stopped.
                    _localHandshakeIdentity?.Dispose();
                }
            }
        }

        private void AddGroupChatPeer()
        {
            var groupPeer = new PeerInfo
            {
                DisplayName = "グループチャット",
                IsGroupChat = true,
                StatusText = "接続中の相手全員に送信"
            };
            _peerRegistryService.AddSpecialPeer(groupPeer);
            PeerList.Items.Add(groupPeer);
        }

        private void ResizeWindow(int width, int height)
        {
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            int availableWidth = Math.Max(320, displayArea.WorkArea.Width - 32);
            int availableHeight = Math.Max(360, displayArea.WorkArea.Height - 32);
            appWindow.Resize(new SizeInt32(
                Math.Min(width, availableWidth),
                Math.Min(height, availableHeight)));
        }
    }
}
