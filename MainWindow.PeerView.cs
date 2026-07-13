using System.Linq;
using direct_module.Services;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void PeerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            UpdateSendButtonState();
        }

        private void UpdatePeerCount()
        {
            PeerCountText.Text = $"検出済み {PeerList.Items.Count}";
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
                return;
            }

            if (peer.IsGroupChat)
            {
                ChatHeaderAvatarText.Text = "GR";
                ChatHeaderTitleText.Text = peer.DisplayName;
                ChatHeaderStatusText.Text = "接続中の相手全員に送信します";
                SelectedPeerAvatarText.Text = "GR";
                SelectedPeerNameText.Text = peer.DisplayName;
                SelectedPeerStatusText.Text = "グループ宛先";
                SelectedPeerSourceText.Text = peer.DisplayText;
                SelectedPeerProgress.Value = 100;
                SelectedPeerIpText.Text = "Remote IP: -";
                SelectedPeerSessionText.Text = "Session: group";
                SelectedPeerDeviceText.Text = "DeviceId: -";
                SelectedPeerOnlineDot.Fill = new SolidColorBrush(Colors.LimeGreen);
                return;
            }

            string displayName = string.IsNullOrWhiteSpace(peer.DisplayName)
                ? "Unknown Peer"
                : peer.DisplayName;
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
            SelectedPeerOnlineDot.Fill = new SolidColorBrush(peer.IsChatReady ? Colors.LimeGreen : peer.IsTcpConnected ? Colors.DeepSkyBlue : Colors.Gray);
        }

        private void UpdateSendButtonState()
        {
            bool canSend = false;

            if (PeerList.SelectedItem is PeerInfo peer)
            {
                canSend = peer.IsGroupChat
                    ? _chatConnectionManager.Connections.Any(connection => connection.IsConnected && connection.IsReady)
                    : PeerConnectionStateService.IsChatReady(peer) && GetConnectionForPeer(peer)?.IsReady == true;
            }
            else if (_chatRole == ChatRole.Host)
            {
                canSend = _chatConnectionManager.Connections.Any(connection => connection.IsConnected && connection.IsReady);
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                SendMessageButton.IsEnabled = canSend;
                AttachFileButton.IsEnabled = canSend;
            });

            AddLog(
                canSend
                    ? "選択中Peerの状態によりSendMessageButtonを有効化"
                    : "選択中Peerの状態によりSendMessageButtonを無効化",
                LogLevel.Debug);

            UpdateReconnectButtonState();
        }

        private void UpdateReconnectButtonState()
        {
            bool canReconnect = PeerList.SelectedItem is PeerInfo peer &&
                _peerConnectionStateService.CanReconnect(peer);

            DispatcherQueue.TryEnqueue(() =>
            {
                ReconnectButton.IsEnabled = canReconnect;
            });
        }

        private void RefreshPeerDisplay(PeerInfo peer)
        {
            _peerConnectionStateService.UpdateConnectAvailability(peer);

            int selectedIndex = PeerList.SelectedIndex;
            int index = PeerList.Items.IndexOf(peer);
            if (index < 0)
            {
                return;
            }

            PeerList.Items.RemoveAt(index);
            PeerList.Items.Insert(index, peer);

            if (selectedIndex >= 0 && selectedIndex < PeerList.Items.Count)
            {
                PeerList.SelectedIndex = selectedIndex;
            }

            UpdatePeerCount();
            UpdateSelectedPeerDetails(PeerList.SelectedItem as PeerInfo);
            UpdateSendButtonState();
        }
    }
}
