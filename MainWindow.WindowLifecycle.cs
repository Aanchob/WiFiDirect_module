using System;
using direct_module.WiFiDirect.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private string LocalPeerId => _localSessionId.ToString("N");

        private string GetLocalShortSessionId()
        {
            return _localSessionId.ToString("N")[..4];
        }

        private string GetLocalRoleKey()
        {
            return _localSessionId.ToString("N")[..8];
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _chatConnectionManager.StopKeepAlive();
            _chatConnectionManager.CloseAll();
            _tcpServer.Stop();
            _discoveryManager.StopScan();
            _discoveryManager.StopAdvertise();
            _manager.Stop();
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
            appWindow.Resize(new SizeInt32(width, height));
        }
    }
}
