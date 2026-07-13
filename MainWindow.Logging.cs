using System;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private void OnFileTransferProgressChanged(FileTransferProgress progress)
        {
            string status = progress.IsComplete ? "完了" : $"{progress.Percent:F0}%";
            EnqueueLog($"File transfer: {progress.FileName} {status}", LogLevel.Debug);
        }

        private void EnqueueLog(string message, LogLevel level = LogLevel.Info)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AddLog(message, level);
            });
        }

        private void SaveChatMessageSafely(ChatMessage message, bool isOutgoing, PeerInfo? peer, ChatConnection? connection)
        {
            if (_chatHistoryService == null)
            {
                AddLog("履歴保存失敗: ChatHistoryServiceが初期化されていません", LogLevel.Error);
                return;
            }

            ChatHistorySaveResult result = _chatHistoryService.SaveMessage(message, isOutgoing, peer, connection);
            switch (result.Status)
            {
                case ChatHistorySaveStatus.SkippedNonChat:
                    return;
                case ChatHistorySaveStatus.DuplicateMessageId:
                    AddLog($"重複MessageIdのため履歴保存をスキップ: {result.MessageId}", LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Saved:
                    AddLog(
                        isOutgoing
                            ? $"送信メッセージ履歴保存成功: MessageId={result.MessageId}"
                            : $"受信メッセージ履歴保存成功: MessageId={result.MessageId}",
                        LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Failed:
                    AddLog($"履歴保存失敗: MessageId={result.MessageId}, Error={result.ErrorMessage}", LogLevel.Error);
                    return;
                default:
                    return;
            }
        }

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            LogLevel effectiveLevel = level == LogLevel.Info
                ? LogClassifier.Classify(message)
                : level;

            if (effectiveLevel == LogLevel.Debug && ShowDebugLogCheckBox?.IsChecked != true)
            {
                return;
            }

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{effectiveLevel}] {message}";
            _logLines.Add(line);

            bool trimmed = false;
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
                trimmed = true;
            }

            if (trimmed)
            {
                LogTextBox.Text = string.Join(Environment.NewLine, _logLines);
            }
            else
            {
                LogTextBox.Text = string.IsNullOrEmpty(LogTextBox.Text)
                    ? line
                    : $"{LogTextBox.Text}{Environment.NewLine}{line}";
            }

            MoveLogCaretToEnd();
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
        }
    }
}
