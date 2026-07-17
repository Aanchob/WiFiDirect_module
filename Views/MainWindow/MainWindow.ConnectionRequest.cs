using direct_module.Discovery;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private async Task SendBleConnectionRequestAsync(PeerInfo peer)
        {
            if (!peer.DiscoveredByBle || string.IsNullOrWhiteSpace(peer.ShortSessionId))
            {
                AddLog("接続依頼にはBLEで発見した相手が必要です", LogLevel.Error);
                return;
            }

            peer.StatusText = "接続依頼送信中";
            RefreshPeerDisplay(peer);
            try
            {
                await _discoveryManager.SendConnectionRequestAsync(peer.ShortSessionId);
                peer.StatusText = "接続依頼送信済み";
                AddLog($"{peer.DisplayName}へ接続依頼を送信しました。相手の承認を待ちます", LogLevel.Success);
            }
            catch (Exception ex)
            {
                peer.StatusText = "接続依頼失敗";
                AddLog($"BLE接続依頼送信失敗: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                _peerConnectionStateService.UpdateConnectAvailability(peer);
                RefreshPeerDisplay(peer);
            }
        }

        private void OnBleConnectionRequestReceived(BleConnectionRequest request)
        {
            if (!string.Equals(
                    request.TargetShortSessionId,
                    GetLocalShortSessionId(),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    request.SourceShortSessionId,
                    GetLocalShortSessionId(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EnqueueAsyncSafely(
                () => HandleBleConnectionRequestAsync(request),
                "BLE接続依頼");
        }

        private async Task HandleBleConnectionRequestAsync(BleConnectionRequest request)
        {
            await _connectionRequestDialogGate.WaitAsync();
            try
            {
                PeerInfo? sourcePeer = _peerRegistryService.FindByShortSessionId(request.SourceShortSessionId);
                if (sourcePeer == null)
                {
                    sourcePeer = AddOrMergePeer(new PeerInfo
                    {
                        DisplayName = $"Peer {request.SourceShortSessionId}",
                        BleName = $"Peer {request.SourceShortSessionId}",
                        DiscoveredByBle = true,
                        ShortSessionId = request.SourceShortSessionId,
                        MatchKey = request.SourceShortSessionId,
                        RoleKey = request.SourceRoleKey,
                        TcpPort = LocalTcpPort
                    });
                }

                if (!string.IsNullOrWhiteSpace(sourcePeer.RoleKey) &&
                    !string.Equals(sourcePeer.RoleKey, request.SourceRoleKey, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("BLE接続依頼を拒否しました: 登録済みRoleKeyと一致しません", LogLevel.Error);
                    return;
                }

                if (!_connectionRoleService.IsLocalClientForWifiDirect(sourcePeer))
                {
                    AddLog("BLE接続依頼を拒否しました: この端末はClientロールではありません", LogLevel.Error);
                    return;
                }

                var requestDialog = new ContentDialog
                {
                    Title = "接続依頼",
                    Content = $"{sourcePeer.DisplayName} から接続依頼が届きました。接続しますか？",
                    PrimaryButtonText = "接続する",
                    CloseButtonText = "今回はしない",
                    DefaultButton = ContentDialogButton.Primary
                };
                SetDialogXamlRoot(requestDialog);
                if (await requestDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    AddLog($"接続依頼を辞退しました: Peer={sourcePeer.DisplayName}");
                    return;
                }

                PeerInfo? selectedCandidate = await ResolveRequestedWiFiCandidateAsync(sourcePeer);
                if (selectedCandidate == null)
                {
                    AddLog("接続依頼に対応するWi-Fi Direct候補を選択できませんでした", LogLevel.Error);
                    return;
                }

                if (!ReferenceEquals(selectedCandidate, sourcePeer))
                {
                    if (!_peerRegistryService.ConfirmUserSelectedWiFiCandidate(sourcePeer, selectedCandidate))
                    {
                        AddLog("選択したWi-Fi Direct候補を相手へ紐付けできませんでした", LogLevel.Error);
                        return;
                    }

                    PeerList.Items.Remove(selectedCandidate);
                    _peerConnectionStateService.UpdateConnectAvailability(sourcePeer);
                    RefreshPeerDisplay(sourcePeer);
                    UpdatePeerCount();
                }

                PeerList.SelectedItem = sourcePeer;
                sourcePeer.StatusText = "接続依頼を承認";
                RefreshPeerDisplay(sourcePeer);
                await ConnectPeerAsync(sourcePeer, refreshCandidate: false);
            }
            finally
            {
                _connectionRequestDialogGate.Release();
            }
        }

        private async Task<PeerInfo?> ResolveRequestedWiFiCandidateAsync(PeerInfo sourcePeer)
        {
            if (HasUsableWiFiDirectCandidate(sourcePeer))
            {
                return sourcePeer;
            }

            await _manager.StartAssociationEndpointScanAsync();
            if (await WaitForWiFiDirectCandidateAsync(sourcePeer, TimeSpan.FromSeconds(5)))
            {
                return sourcePeer;
            }

            List<PeerInfo> candidates = _peerRegistryService.Peers
                .Where(peer =>
                    !ReferenceEquals(peer, sourcePeer) &&
                    !peer.IsGroupChat &&
                    !peer.IsRelayPeer &&
                    peer.DiscoveredByWiFiDirect &&
                    !peer.DiscoveredByBle &&
                    !peer.IsConnected &&
                    !peer.IsTcpConnected &&
                    (string.IsNullOrWhiteSpace(peer.ShortSessionId) ||
                     string.Equals(peer.ShortSessionId, sourcePeer.ShortSessionId, StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(peer.WiFiDirectDeviceIdForConnection) &&
                    !peer.WiFiDirectDeviceIdForConnection.Contains("_PendingRequest", StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<PeerInfo> partialMatches = candidates
                .Where(candidate => PeerMatchService.IsPartialNameMatchCandidate(sourcePeer, candidate))
                .ToList();
            if (partialMatches.Count == 1)
            {
                return partialMatches[0];
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var candidateSelector = new ComboBox
            {
                Header = "接続するWi-Fi Direct端末",
                ItemsSource = candidates,
                DisplayMemberPath = nameof(PeerInfo.DisplayName),
                SelectedIndex = candidates.Count == 1 ? 0 : -1,
                MinWidth = 320
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "BLE名とWi-Fi Direct名が一致しないため、相手の端末名を選択してください。",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(candidateSelector);

            var selectDialog = new ContentDialog
            {
                Title = "接続先の確認",
                Content = panel,
                PrimaryButtonText = "この端末に接続",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary
            };
            SetDialogXamlRoot(selectDialog);
            ContentDialogResult result = await selectDialog.ShowAsync();
            return result == ContentDialogResult.Primary
                ? candidateSelector.SelectedItem as PeerInfo
                : null;
        }

        private void SetDialogXamlRoot(ContentDialog dialog)
        {
            if (Content is FrameworkElement root && root.XamlRoot != null)
            {
                dialog.XamlRoot = root.XamlRoot;
            }
        }
    }
}
