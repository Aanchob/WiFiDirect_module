using direct_module.Discovery;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect;
using direct_module.WiFiDirect.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Networking.Sockets;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            LogTextBox.Text = string.Empty;
        }

        private void ScrollLogBottom_Click(object sender, RoutedEventArgs e)
        {
            MoveLogCaretToEnd();
        }

        private void OnLogReceived(string message)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog(message, LogClassifier.Classify(message));
            });
        }

        private void EnqueueAsyncSafely(Func<System.Threading.Tasks.Task> action, string context)
        {
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        AddLog($"{context}に失敗しました: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                    }
                }))
            {
                EnqueueLog($"{context}をUIスレッドへ送れませんでした", LogLevel.Error);
            }
        }

        private async void RunSafelyInBackground(
            Func<System.Threading.Tasks.Task> action,
            string context)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                EnqueueLog($"{context}に失敗しました: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
        }
    }
}
