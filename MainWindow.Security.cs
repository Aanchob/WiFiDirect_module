using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using direct_module.Crypto;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private readonly SemaphoreSlim _securityDialogGate = new(1, 1);
        private int _pinStoreRecoveryDialogScheduled;
        private int _localIdentityRecoveryDialogScheduled;
        private int _localIdentityUnavailableLogged;

        private static EcdsaChatIdentity LoadLocalHandshakeIdentity()
        {
            byte[] privateKey = LocalIdentityService.GetOrCreateChatIdentityPrivateKey(() =>
            {
                using EcdsaChatIdentity created = EcdsaChatIdentity.Create();
                return created.ExportPkcs8PrivateKey();
            });

            try
            {
                return EcdsaChatIdentity.ImportPkcs8PrivateKey(privateKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        private ChatConnection CreateAuthenticatedChatConnection(PeerInfo? expectedPeer = null)
        {
            EcdsaChatIdentity localHandshakeIdentity = _localIdentityReady
                ? _localHandshakeIdentity ?? throw new InvalidOperationException(
                    "The local signing identity is unavailable.")
                : throw new LocalIdentityStoreUnavailableException(
                    "The local peer/signing identity is unavailable; networking remains disabled.");

            var connection = new ChatConnection
            {
                LocalHandshakeIdentity = localHandshakeIdentity,
                RequireAuthenticatedRemoteIdentity = true
            };

            connection.RemoteIdentityVerifier = VerifyRemoteIdentityDuringHandshake;
            if (expectedPeer != null)
            {
                connection.SetExpectedRemoteIdentity(expectedPeer.PeerId, expectedPeer.ShortSessionId);
                if (!string.IsNullOrWhiteSpace(expectedPeer.PeerId))
                {
                    string? pin;
                    try
                    {
                        pin = LocalIdentityService.GetPinnedRemoteIdentityFingerprint(expectedPeer.PeerId);
                    }
                    catch (RemoteIdentityPinStoreUnavailableException)
                    {
                        SchedulePinStoreRecoveryDialog();
                        throw;
                    }
                    if (!string.IsNullOrWhiteSpace(pin))
                    {
                        connection.SetExpectedRemoteCryptographicIdentity(pin);
                    }
                }
            }

            return connection;
        }

        private bool EnsureLocalIdentityReadyForNetworking()
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return false;
            }

            if (_localIdentityReady)
            {
                return true;
            }

            if (Interlocked.CompareExchange(ref _localIdentityUnavailableLogged, 1, 0) == 0)
            {
                EnqueueLog(
                    "ローカル暗号IDを安全に読み込めないため、すべての通信機能を無効にしました。",
                    LogLevel.Error);
            }
            ScheduleLocalIdentityRecoveryDialog();
            return false;
        }

        private void MainWindow_LocalIdentityUnavailableLoaded(
            object sender,
            RoutedEventArgs args)
        {
            if (sender is FrameworkElement rootContent)
            {
                rootContent.Loaded -= MainWindow_LocalIdentityUnavailableLoaded;
            }
            EnsureLocalIdentityReadyForNetworking();
        }

        private void ScheduleLocalIdentityRecoveryDialog()
        {
            if (_localIdentityReady ||
                Interlocked.CompareExchange(ref _localIdentityRecoveryDialogScheduled, 1, 0) != 0)
            {
                return;
            }

            if (!TryEnqueueBackgroundOperation(
                async () =>
                {
                    bool keepDialogSuppressed = false;
                    try
                    {
                        string failureDetail = SanitizeUntrustedDisplayText(
                            _localIdentityFailureMessage,
                            320);
                        if (!_localIdentityRecoveryRequired)
                        {
                            await ShowSecurityConfirmationAsync(
                                "通信機能を無効にしました",
                                $"ローカル暗号IDを読み込めませんでした。データ保存先とアクセス権を確認してから、アプリを再起動してください。\n\n詳細: {failureDetail}",
                                "確認",
                                _windowLifetimeCancellation.Token);
                            return;
                        }

                        bool approved = await ShowSecurityConfirmationAsync(
                            "ローカル暗号IDの復旧が必要です",
                            "保存済みのローカルPeer IDまたは署名鍵が欠損・破損しています。安全のため通信は停止されています。復旧準備を行うと既存IDを隔離し、次回起動時に新しいIDを作成します。相手側では別の端末として再確認が必要です。",
                            "既存IDを隔離",
                            _windowLifetimeCancellation.Token);
                        if (!approved)
                        {
                            return;
                        }

                        LocalIdentityService.ResetLocalIdentityAfterCorruption();
                        keepDialogSuppressed = true;
                        EnqueueLog(
                            "ローカル暗号IDを隔離しました。通信は停止したままです。アプリを再起動してください。",
                            LogLevel.Success);
                        await ShowSecurityConfirmationAsync(
                            "再起動が必要です",
                            "復旧準備が完了しました。このプロセスでは新しいIDを作成しません。アプリを終了して再起動してください。",
                            "確認",
                            _windowLifetimeCancellation.Token);
                    }
                    catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
                    {
                        keepDialogSuppressed = true;
                    }
                    catch (Exception ex)
                    {
                        EnqueueLog($"ローカル暗号IDを復旧準備できませんでした: {ex.Message}", LogLevel.Error);
                    }
                    finally
                    {
                        if (!keepDialogSuppressed)
                        {
                            Interlocked.Exchange(ref _localIdentityRecoveryDialogScheduled, 0);
                        }
                    }
                },
                "ローカル暗号ID復旧ダイアログ"))
            {
                Interlocked.Exchange(ref _localIdentityRecoveryDialogScheduled, 0);
            }
        }

        private bool VerifyRemoteIdentityDuringHandshake(ChatConnection connection, string fingerprint)
        {
            try
            {
                string expectedPeerId = connection.ExpectedRemotePeerId;
                if (string.IsNullOrWhiteSpace(expectedPeerId))
                {
                    // The signed identity is provisionally accepted. It is bound to the
                    // discovery identity and explicitly approved after HELLO.
                    return true;
                }

                string? pinned = LocalIdentityService.GetPinnedRemoteIdentityFingerprint(expectedPeerId);
                return string.IsNullOrWhiteSpace(pinned) ||
                       EcdhService.FingerprintsEqual(pinned, fingerprint);
            }
            catch (RemoteIdentityPinStoreUnavailableException)
            {
                SchedulePinStoreRecoveryDialog();
                EnqueueLog(
                    "保存済み暗号IDストアが破損しているため接続を拒否しました。設定から明示的な復旧操作が必要です。",
                    LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                EnqueueLog($"保存済み暗号IDの検証に失敗しました: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private async Task<bool> ApproveAndPinRemoteIdentityAsync(
            PeerInfo peer,
            ChatMessage hello,
            ChatConnection connection,
            CancellationToken cancellationToken = default)
        {
            if (!connection.IsCryptographicIdentityVerified ||
                string.IsNullOrWhiteSpace(connection.RemoteIdentityFingerprint) ||
                string.IsNullOrWhiteSpace(hello.SenderId))
            {
                return false;
            }

            try
            {
                string? existingPin = LocalIdentityService.GetPinnedRemoteIdentityFingerprint(hello.SenderId);
                if (!string.IsNullOrWhiteSpace(existingPin))
                {
                    return EcdhService.FingerprintsEqual(
                        existingPin,
                        connection.RemoteIdentityFingerprint);
                }

                string displayName = SanitizeUntrustedDisplayText(
                    string.IsNullOrWhiteSpace(hello.SenderName)
                        ? peer.DisplayName
                        : hello.SenderName);
                string displayPeerId = SanitizeUntrustedDisplayText(hello.SenderId, 160);
                string displayFingerprint = SanitizeUntrustedDisplayText(
                    connection.RemoteIdentityFingerprint,
                    160);
                bool approved = await ShowSecurityConfirmationAsync(
                    "新しい相手を信頼しますか？",
                    $"相手: {displayName}\nPeer ID: {displayPeerId}\n暗号ID: {displayFingerprint}\n\n表示内容を確認できる場合だけ信頼してください。",
                    "信頼して接続",
                    cancellationToken);
                if (!approved) return false;

                // Approval and persistence are intentionally separate. Another
                // simultaneous connection may pin the peer while this dialog is
                // open; the atomic verifier then accepts only the same identity.
                return LocalIdentityService.VerifyOrPinRemoteIdentityFingerprint(
                    hello.SenderId,
                    connection.RemoteIdentityFingerprint);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested ||
                _windowLifetimeCancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (RemoteIdentityPinStoreUnavailableException)
            {
                SchedulePinStoreRecoveryDialog();
                EnqueueLog(
                    "保存済み暗号IDストアが破損しているため接続を拒否しました。自動初期化は行いません。",
                    LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                EnqueueLog($"暗号IDの確認または保存に失敗しました: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private void SchedulePinStoreRecoveryDialog()
        {
            if (Interlocked.CompareExchange(ref _pinStoreRecoveryDialogScheduled, 1, 0) != 0)
            {
                return;
            }

            if (!TryEnqueueBackgroundOperation(
                async () =>
                {
                    try
                    {
                        bool approved = await ShowSecurityConfirmationAsync(
                            "保存済み暗号IDを復旧しますか？",
                            "保存済み暗号IDストアが破損しています。復旧すると全相手のpinが失われ、次回接続時にすべての相手の暗号IDを別経路で再確認する必要があります。現在の接続は拒否されます。",
                            "全pinを失って復旧",
                            _windowLifetimeCancellation.Token);
                        if (!approved)
                        {
                            AddLog("暗号IDストアの復旧を行いませんでした。接続は引き続き拒否されます。", LogLevel.Error);
                            return;
                        }

                        LocalIdentityService.ResetRemoteIdentityPinStoreAfterCorruption();
                        AddLog("暗号IDストアを明示的に復旧しました。相手のfingerprintを再確認して接続し直してください。", LogLevel.Success);
                    }
                    catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        AddLog($"暗号IDストアを復旧できませんでした: {ex.Message}", LogLevel.Error);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _pinStoreRecoveryDialogScheduled, 0);
                    }
                },
                "暗号IDストア復旧ダイアログ"))
            {
                Interlocked.Exchange(ref _pinStoreRecoveryDialogScheduled, 0);
            }
        }

        private Task<bool> ApproveIncomingConnectionAsync(PeerInfo peer, CancellationToken cancellationToken)
        {
            string identity = string.IsNullOrWhiteSpace(peer.MatchKey)
                ? string.IsNullOrWhiteSpace(peer.ShortSessionId) ? "未確認" : peer.ShortSessionId
                : peer.MatchKey;
            string displayName = SanitizeUntrustedDisplayText(peer.DisplayName);
            identity = SanitizeUntrustedDisplayText(identity, 160);
            return ShowSecurityConfirmationAsync(
                "Wi-Fi Direct接続要求",
                $"{displayName} から接続要求を受信しました。\n探索ID: {identity}",
                "接続を許可",
                cancellationToken);
        }

        private async Task<bool> ShowSecurityConfirmationAsync(
            string title,
            string message,
            string primaryButtonText,
            CancellationToken cancellationToken)
        {
            if (_windowLifetimeCancellation.IsCancellationRequested)
            {
                return false;
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _windowLifetimeCancellation.Token);
            CancellationToken effectiveCancellation = linkedCancellation.Token;

            if (!DispatcherQueue.HasThreadAccess)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!StartBackgroundOperation(
                            async () =>
                            {
                                try
                                {
                                    completion.TrySetResult(await ShowSecurityConfirmationAsync(
                                        title,
                                        message,
                                        primaryButtonText,
                                        effectiveCancellation));
                                }
                                catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
                                {
                                    completion.TrySetResult(false);
                                }
                                catch (Exception ex)
                                {
                                    completion.TrySetException(ex);
                                }
                            },
                            "セキュリティ確認ダイアログ"))
                        {
                            completion.TrySetResult(false);
                        }
                    }))
                {
                    return false;
                }

                // Keep the linked source alive until the queued UI operation has
                // observed cancellation. Returning early via WaitAsync would
                // dispose the source while that operation still owns its token;
                // a later linked-token registration can then throw
                // ObjectDisposedException instead of closing the dialog cleanly.
                return await completion.Task;
            }

            try
            {
                await _securityDialogGate.WaitAsync(effectiveCancellation);
            }
            catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
            {
                return false;
            }
            try
            {
                if (Content is not FrameworkElement root || root.XamlRoot == null) return false;

                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = title,
                    Content = message,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = "拒否",
                    DefaultButton = ContentDialogButton.Close
                };
                try
                {
                    ContentDialogResult result = await dialog.ShowAsync().AsTask(effectiveCancellation);
                    return result == ContentDialogResult.Primary;
                }
                catch (OperationCanceledException) when (effectiveCancellation.IsCancellationRequested)
                {
                    try
                    {
                        dialog.Hide();
                    }
                    catch (Exception)
                    {
                        // The dialog may already have completed or lost its XamlRoot.
                    }
                    return false;
                }
            }
            finally
            {
                _securityDialogGate.Release();
            }
        }
    }
}
