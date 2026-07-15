using System.Linq;
using System.Threading;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PeerInfo? selectedPeer = PeerList.SelectedItem as PeerInfo;
            UpdateSelectedPeerDetails(selectedPeer);
            SwitchConversation(selectedPeer);
            UpdateSendButtonState();
        }

        private void UpdatePeerCount()
        {
            int discoveredCount = PeerList.Items.Cast<PeerInfo>().Count(peer => !peer.IsGroupChat);
            int connectedCount = _chatConnectionManager.Connections.Count(connection => connection.IsConnected && connection.IsReady);
            PeerCountText.Text = $"検出済み {discoveredCount} / 接続 {connectedCount}";
        }

        private void UpdateSelectedPeerDetails(PeerInfo? peer)
        {
            if (peer is null)
            {
                ChatHeaderAvatarText.Text = "--";
                ChatHeaderTitleText.Text = "相手未選択";
                ChatHeaderStatusText.Text = "Peerを選択すると状態が表示されます";
                SelectedPeerAvatarText.Text = "--";
                SelectedPeerNameText.Text = "未選択";
                SelectedPeerStatusText.Text = "相手を選択してください";
                SelectedPeerSourceText.Text = "BLE / Wi-Fi Direct の検出状況がここに表示されます。";
                SelectedPeerProgress.Value = 10;
                SelectedPeerIpText.Text = "Remote IP: -";
                SelectedPeerSessionText.Text = "Session: -";
                SelectedPeerDeviceText.Text = "DeviceId: -";
                SelectedPeerOnlineDot.Fill = new SolidColorBrush(Colors.Gray);
                ChatHeaderOnlineDot.Fill = new SolidColorBrush(Colors.Gray);
                SetSelectedStatusVisual(Colors.Gray, ColorHelper.FromArgb(32, 128, 128, 128));
                return;
            }

            if (peer.IsGroupChat)
            {
                int readyCount = _chatConnectionManager.Connections.Count(connection => connection.IsConnected && connection.IsReady);
                bool isAvailable = readyCount > 0;
                ChatHeaderAvatarText.Text = "GR";
                ChatHeaderTitleText.Text = peer.DisplayName;
                ChatHeaderStatusText.Text = isAvailable
                    ? $"接続中の相手 {readyCount} 台へ送信します"
                    : "送信可能な相手はいません";
                SelectedPeerAvatarText.Text = "GR";
                SelectedPeerNameText.Text = peer.DisplayName;
                SelectedPeerStatusText.Text = isAvailable ? $"オンライン {readyCount} 台" : "オフライン";
                SelectedPeerSourceText.Text = peer.DisplayText;
                SelectedPeerProgress.Value = isAvailable ? 100 : 0;
                SelectedPeerIpText.Text = "Remote IP: -";
                SelectedPeerSessionText.Text = "Session: group";
                SelectedPeerDeviceText.Text = "DeviceId: -";
                Color groupColor = isAvailable ? Colors.LimeGreen : Colors.Gray;
                SelectedPeerOnlineDot.Fill = new SolidColorBrush(groupColor);
                ChatHeaderOnlineDot.Fill = new SolidColorBrush(groupColor);
                SetSelectedStatusVisual(groupColor, ColorHelper.FromArgb(32, groupColor.R, groupColor.G, groupColor.B));
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(peer.DisplayName)
                ? "Unknown Peer"
                : SanitizeUntrustedDisplayText(peer.DisplayName);
            string status = PeerDisplayService.GetStatusText(peer);

            ChatHeaderAvatarText.Text = PeerDisplayService.CreateInitials(displayName);
            ChatHeaderTitleText.Text = displayName;
            ChatHeaderStatusText.Text = status;
            SelectedPeerAvatarText.Text = PeerDisplayService.CreateInitials(displayName);
            SelectedPeerNameText.Text = displayName;
            SelectedPeerStatusText.Text = status;
            SelectedPeerSourceText.Text = $"{peer.SourceText} / TCP:{(peer.IsTcpConnected ? "接続済み" : peer.IsPreparingChatTcp ? "準備中" : "未接続")} / HELLO:{(peer.IsHelloVerified ? "確認済み" : "未確認")}";
            SelectedPeerProgress.Value = PeerDisplayService.GetProgressValue(peer);
            SelectedPeerIpText.Text = $"Remote IP: {PeerDisplayService.GetDisplayValue(peer.RemoteIpAddress)}";
            SelectedPeerSessionText.Text = $"Session: {PeerDisplayService.GetDisplayValue(peer.ShortSessionId)}";
            SelectedPeerDeviceText.Text = $"DeviceId: {PeerDisplayService.GetDisplayValue(peer.DeviceId)}";
            Color stateColor = peer.IsChatReady
                ? Colors.LimeGreen
                : peer.IsTcpConnected ? Colors.DeepSkyBlue
                : string.Equals(peer.StatusText, "エラー", System.StringComparison.OrdinalIgnoreCase) ||
                  peer.StatusText.Contains("失敗", System.StringComparison.OrdinalIgnoreCase) ||
                  peer.StatusText.Contains("切断", System.StringComparison.OrdinalIgnoreCase)
                    ? Colors.OrangeRed
                    : Colors.Gray;
            SelectedPeerOnlineDot.Fill = new SolidColorBrush(stateColor);
            ChatHeaderOnlineDot.Fill = new SolidColorBrush(stateColor);
            SetSelectedStatusVisual(stateColor, ColorHelper.FromArgb(32, stateColor.R, stateColor.G, stateColor.B));
        }

        private void UpdateSendButtonState()
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(UpdateSendButtonState);
                return;
            }

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            bool canSend = false;

            if (_localIdentityReady && PeerList.SelectedItem is PeerInfo peer)
            {
                canSend = peer.IsGroupChat
                    ? _chatConnectionManager.Connections.Any(connection => connection.IsConnected && connection.IsReady)
                    : PeerConnectionStateService.IsChatReady(peer) && GetConnectionForPeer(peer)?.IsReady == true;
            }
            SendMessageButton.IsEnabled = canSend;
            AttachFileButton.IsEnabled = canSend &&
                Volatile.Read(ref _fileStorageReady) != 0 &&
                _outgoingFileSendGate.CurrentCount > 0;

            AddLog(
                canSend
                    ? "選択中Peerの状態によりSendMessageButtonを有効化"
                    : "選択中Peerの状態によりSendMessageButtonを無効化",
                LogLevel.Debug);

            UpdateReconnectButtonState();
        }

        private void UpdateReconnectButtonState()
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(UpdateReconnectButtonState);
                return;
            }

            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            bool canReconnect = _localIdentityReady &&
                PeerList.SelectedItem is PeerInfo peer &&
                _peerConnectionStateService.CanReconnect(peer);

            ReconnectButton.IsEnabled = canReconnect;
        }

        private void RefreshPeerDisplay(PeerInfo peer)
        {
            _peerConnectionStateService.UpdateConnectAvailability(peer);
            UpdatePeerCount();
            if (ReferenceEquals(PeerList.SelectedItem, peer))
            {
                UpdateSelectedPeerDetails(peer);
            }
            UpdateSendButtonState();
        }

        private void SetSelectedStatusVisual(Color foreground, Color background)
        {
            SelectedPeerStatusText.Foreground = new SolidColorBrush(foreground);
            SelectedPeerStatusChip.BorderBrush = new SolidColorBrush(foreground);
            SelectedPeerStatusChip.Background = new SolidColorBrush(background);
        }
    }
}
