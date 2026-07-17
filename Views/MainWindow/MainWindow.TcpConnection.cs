using direct_module.Discovery;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void StartTcpServer_Click(object sender, RoutedEventArgs e)
        {
            _chatRole = ChatRole.Host;
            AddLog("Chat Role: Host");
            RunSafelyInBackground(
                () => EnsureTcpServerStartedAsync("手動操作"),
                "TCP待ち受け開始");
        }

        private void OnTcpConnectionAccepted(StreamSocket socket)
        {
            EnqueueAsyncSafely(async () =>
            {
                _chatRole = ChatRole.Host;

                var connection = new ChatConnection
                {
                    PeerId = $"{socket.Information.RemoteAddress?.DisplayName}:{socket.Information.RemotePort}",
                    PeerName = socket.Information.RemoteAddress?.DisplayName ?? "Client",
                    RemoteIpAddress = socket.Information.RemoteAddress?.DisplayName ?? ""
                };

                _chatConnectionManager.AddConnection(connection);
                await connection.AttachAcceptedSocketAsync(socket);

                AddLog($"Host: Client TCP接続を追加 Count={_chatConnectionManager.ConnectedCount}");
                AddConnectedPeerDisplay(connection);

                if (connection.IsConnected && connection.IsReceiveLoopStarted)
                {
                    AddLog("TCP接続受信後、HELLO確認を開始します");
                    await SendHelloAsync(connection);
                    StartHelloTimeout(connection);
                }
            }, "TCP接続受け入れ処理");
        }

        private ChatConnection? GetSelectedPeerPreparedConnection()
        {
            if (PeerList.SelectedItem is not PeerInfo peer)
            {
                AddLog("送信する相手を選択してください", LogLevel.Error);
                return null;
            }

            if (!peer.IsRelayPeer && string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("送信先RemoteIpAddressがありません。先にWi-Fi Direct接続してください。", LogLevel.Error);
                return null;
            }

            ChatConnection? connection = GetConnectionForPeer(peer);

            if (connection == null)
            {
                AddLog("Chat TCP未接続です。事前接続を確認してください。", LogLevel.Error);
                return null;
            }

            AddLog($"送信先PeerのChatConnectionを取得: {peer.DisplayName}", LogLevel.Debug);

            return connection;
        }

        private async System.Threading.Tasks.Task PrepareChatTcpConnectionAsync(PeerInfo peer, string preparingStatusText = "TCP準備中")
        {
            if (peer.IsPreparingChatTcp || _chatConnectionManager.IsPreparingForPeer(peer))
            {
                AddLog($"PeerごとのTCP準備中のためスキップ: {peer.DisplayName}", LogLevel.Debug);
                AddLog("Chat TCP接続準備中のためスキップします", LogLevel.Debug);
                return;
            }

            if (peer.IsChatReady)
            {
                AddLog($"PeerごとのTCP準備スキップ: すでにチャット準備完了 {peer.DisplayName}", LogLevel.Debug);
                return;
            }

            peer.IsPreparingChatTcp = true;
            peer.StatusText = preparingStatusText;
            RefreshPeerDisplay(peer);
            UpdateSendButtonState();
            AddLog($"PeerごとのTCP準備開始: {peer.DisplayName}");

            try
            {
                UpdateSendButtonState();

                if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
                {
                    AddLog("Chat TCP事前接続失敗: RemoteIpAddressがありません", LogLevel.Error);
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.StatusText = "送信不可";
                    RefreshPeerDisplay(peer);
                    return;
                }

                var totalWatch = Stopwatch.StartNew();
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.StatusText = preparingStatusText;
                RefreshPeerDisplay(peer);
                AddLog("チャット準備中: TCP事前接続を開始します");
                AddLog("Chat TCP事前接続開始");
                AddLog($"接続先IP: {peer.RemoteIpAddress}");
                AddLog($"接続先Port: {LocalTcpPort}");

                ChatConnection? connection = await GetOrCreatePeerConnectionAsync(peer);

                if (connection?.IsConnected == true && connection.IsReceiveLoopStarted)
                {
                    peer.IsTcpConnected = true;
                    peer.IsChatReady = false;
                    peer.StatusText = "HELLO確認中";
                    RefreshPeerDisplay(peer);
                    UpdateSendButtonState();
                    AddLog($"PeerごとのTCP接続成功: {peer.DisplayName}", LogLevel.Success);
                    AddLog("Chat TCP事前接続成功", LogLevel.Success);
                    AddLog("Chat TCP接続済み", LogLevel.Success);
                    AddLog("Chat TCP ReceiveLoop開始済み", LogLevel.Success);
                    await SendHelloAsync(connection);
                    StartHelloTimeout(connection);
                    AddLog("HELLO応答待ち");
                    AddLog($"Chat TCP事前接続合計: {totalWatch.ElapsedMilliseconds}ms", LogLevel.Debug);
                }
                else
                {
                    peer.IsTcpConnected = false;
                    peer.IsChatReady = false;
                    peer.StatusText = "エラー";
                    RefreshPeerDisplay(peer);
                    UpdateSendButtonState();
                    AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                    AddLog("Chat TCP事前接続失敗: TCP接続またはReceiveLoopが未完了です", LogLevel.Error);
                    AddLog($"PeerごとのTCP準備失敗: {peer.DisplayName}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                UpdateSendButtonState();
                AddLog($"PeerごとのTCP準備失敗: {peer.DisplayName}", LogLevel.Error);
                peer.IsTcpConnected = false;
                peer.IsChatReady = false;
                peer.StatusText = "エラー";
                RefreshPeerDisplay(peer);

                AddLog("チャット準備状態をErrorに変更", LogLevel.Error);
                AddLog("Chat TCP事前接続エラー", LogLevel.Error);
                AddLog($"例外名: {ex.GetType().Name}", LogLevel.Error);
                AddLog($"HResult: 0x{ex.HResult:X8}", LogLevel.Error);
                AddLog($"Message: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                peer.IsPreparingChatTcp = false;
                RefreshPeerDisplay(peer);
                UpdateSendButtonState();
                AddLog($"PeerごとのTCP準備終了: {peer.DisplayName}", LogLevel.Debug);
            }
        }

        private async System.Threading.Tasks.Task<ChatConnection?> GetOrCreatePeerConnectionAsync(PeerInfo peer)
        {
            if (string.IsNullOrWhiteSpace(peer.RemoteIpAddress))
            {
                AddLog("RemoteIpAddressがないためChat TCP自動接続をスキップします");
                return null;
            }

            ChatConnection? existing = _chatConnectionManager.FindForPeer(peer);

            if (existing?.IsConnected == true)
            {
                AddLog("Chat TCP接続済みなので再利用");
                peer.IsTcpConnected = true;
                peer.IsChatReady = peer.IsChatReady && existing.IsReceiveLoopStarted;
                peer.StatusText = peer.IsChatReady ? "チャット準備完了" : "HELLO確認中";
                RefreshPeerDisplay(peer);
                return existing;
            }

            var connection = new ChatConnection
            {
                PeerId = PeerIdentityService.GetConnectionId(peer),
                PeerName = peer.DisplayName,
                RemoteIpAddress = peer.RemoteIpAddress,
                ShortSessionId = peer.ShortSessionId
            };

            _chatConnectionManager.AddConnection(connection);
            AddLog($"Chat TCP自動接続開始: {peer.RemoteIpAddress}:{LocalTcpPort}");
            connection.IsPreparing = true;
            try
            {
                await connection.ConnectAsync(peer.RemoteIpAddress, LocalTcpPort);
            }
            finally
            {
                connection.IsPreparing = false;
            }

            peer.IsTcpConnected = connection.IsConnected;
            peer.IsChatReady = false;
            peer.StatusText = connection.IsConnected ? "HELLO確認中" : "送信不可";
            RefreshPeerDisplay(peer);

            if (!connection.IsConnected)
            {
                _chatConnectionManager.RemoveConnection(connection);
            }

            return connection;
        }

        private async System.Threading.Tasks.Task EnsureTcpServerStartedAsync(string reason)
        {
            await _tcpServerStartGate.WaitAsync();
            try
            {
                if (_tcpServer.IsStarted)
                {
                    AddLog($"TCP待ち受けは開始済みです: Reason={reason}", LogLevel.Debug);
                    return;
                }

                AddLog($"TCP待ち受け開始: Port={LocalTcpPort}, Reason={reason}");
                await _tcpServer.StartAsync(LocalTcpPort);
            }
            finally
            {
                _tcpServerStartGate.Release();
            }
        }

        private ChatConnection? GetConnectionForPeer(PeerInfo peer)
        {
            if (peer.IsRelayPeer)
            {
                return _chatConnectionManager.Connections
                    .FirstOrDefault(connection => connection.IsConnected && connection.IsReady);
            }

            return _chatConnectionManager.FindForPeer(peer);
        }
    }
}
