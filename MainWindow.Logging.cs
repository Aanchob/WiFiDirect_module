using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using direct_module.Network;
using direct_module.Services;
using direct_module.WiFiDirect.Models;

namespace direct_module
{
    public sealed partial class MainWindow
    {
        private const int MaximumPendingLogEntries = 512;
        private const int LogDrainBatchSize = 64;
        private const int MaximumPendingHistorySaves = 512;
        private readonly ConcurrentQueue<(string Message, LogLevel Level)> _pendingLogEntries = new();
        private readonly ConcurrentQueue<HistorySaveRequest> _pendingHistorySaves = new();
        private int _pendingLogEntryCount;
        private int _pendingLogDrainScheduled;
        private int _droppedPendingLogEntries;
        private int _pendingHistorySaveCount;
        private int _historySaveDrainRunning;

        private void OnFileTransferProgressChanged(FileTransferProgress progress)
        {
            string status = progress.IsComplete ? "完了" : $"{progress.Percent:F0}%";
            EnqueueLog($"File transfer: {progress.FileName} {status}", LogLevel.Debug);
        }

        private void EnqueueLog(string message, LogLevel level = LogLevel.Info)
        {
            while (true)
            {
                int current = Volatile.Read(ref _pendingLogEntryCount);
                if (current >= MaximumPendingLogEntries)
                {
                    Interlocked.Increment(ref _droppedPendingLogEntries);
                    SchedulePendingLogDrain();
                    return;
                }

                if (Interlocked.CompareExchange(
                        ref _pendingLogEntryCount,
                        current + 1,
                        current) == current)
                {
                    break;
                }
            }

            _pendingLogEntries.Enqueue((message, level));
            SchedulePendingLogDrain();
        }

        private void SchedulePendingLogDrain()
        {
            if (Interlocked.CompareExchange(ref _pendingLogDrainScheduled, 1, 0) != 0)
            {
                return;
            }

            if (DispatcherQueue.TryEnqueue(DrainPendingLogEntries))
            {
                return;
            }

            Interlocked.Exchange(ref _pendingLogDrainScheduled, 0);
            while (_pendingLogEntries.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingLogEntryCount);
            }
            Interlocked.Exchange(ref _droppedPendingLogEntries, 0);
        }

        private void DrainPendingLogEntries()
        {
            int processed = 0;
            while (processed < LogDrainBatchSize &&
                   _pendingLogEntries.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _pendingLogEntryCount);
                AddLog(entry.Message, entry.Level);
                processed++;
            }

            int dropped = Interlocked.Exchange(ref _droppedPendingLogEntries, 0);
            if (dropped > 0)
            {
                AddLog(
                    $"ログの過負荷を防ぐため {dropped} 件を省略しました。",
                    LogLevel.Error);
            }

            Interlocked.Exchange(ref _pendingLogDrainScheduled, 0);
            if (Volatile.Read(ref _pendingLogEntryCount) > 0 ||
                Volatile.Read(ref _droppedPendingLogEntries) > 0)
            {
                SchedulePendingLogDrain();
            }
        }

        private void SaveChatMessageSafely(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection,
            ChatHistoryAttachment? attachment = null)
        {
            if (Volatile.Read(ref _shutdownStarted) != 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _pendingHistorySaveCount) > MaximumPendingHistorySaves)
            {
                Interlocked.Decrement(ref _pendingHistorySaveCount);
                EnqueueLog("会話履歴の保存待ち上限を超えました。", LogLevel.Error);
                if (!isOutgoing)
                {
                    connection?.Close();
                }
                return;
            }

            _pendingHistorySaves.Enqueue(new HistorySaveRequest(
                message,
                isOutgoing,
                peer,
                connection,
                attachment));
            EnsureHistorySaveDrainRunning();
        }

        private void EnsureHistorySaveDrainRunning()
        {
            if (Interlocked.CompareExchange(ref _historySaveDrainRunning, 1, 0) != 0)
            {
                return;
            }

            if (!StartBackgroundOperation(DrainHistorySaveQueueAsync, "会話履歴の保存キュー"))
            {
                Interlocked.Exchange(ref _historySaveDrainRunning, 0);
                ClearPendingHistorySaves();
            }
        }

        private async System.Threading.Tasks.Task DrainHistorySaveQueueAsync()
        {
            try
            {
                while (_pendingHistorySaves.TryDequeue(out HistorySaveRequest request))
                {
                    Interlocked.Decrement(ref _pendingHistorySaveCount);
                    await SaveChatMessageSafelyAsync(
                        request.Message,
                        request.IsOutgoing,
                        request.Peer,
                        request.Connection,
                        request.Attachment);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _historySaveDrainRunning, 0);
                if (Volatile.Read(ref _pendingHistorySaveCount) > 0)
                {
                    EnsureHistorySaveDrainRunning();
                }
            }
        }

        private void ClearPendingHistorySaves()
        {
            while (_pendingHistorySaves.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingHistorySaveCount);
            }
        }

        private async System.Threading.Tasks.Task SaveChatMessageSafelyAsync(
            ChatMessage message,
            bool isOutgoing,
            PeerInfo? peer,
            ChatConnection? connection,
            ChatHistoryAttachment? attachment)
        {
            ChatHistoryService? chatHistoryService =
                await GetChatHistoryServiceWhenReadyForSaveAsync();
            if (chatHistoryService == null)
            {
                EnqueueLog("履歴保存失敗: ChatHistoryServiceが初期化されていません", LogLevel.Error);
                return;
            }

            ChatHistorySaveResult result;
            try
            {
                result = await chatHistoryService.SaveMessageAsync(
                    message,
                    isOutgoing,
                    peer,
                    connection,
                    attachment,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                EnqueueLog($"履歴保存処理に失敗しました: {ex.Message}", LogLevel.Error);
                return;
            }

            switch (result.Status)
            {
                case ChatHistorySaveStatus.SkippedNonChat:
                    return;
                case ChatHistorySaveStatus.DuplicateMessageId:
                    EnqueueLog($"重複MessageIdのため履歴保存をスキップ: {result.MessageId}", LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Saved:
                    EnqueueLog(
                        isOutgoing
                            ? $"送信メッセージ履歴保存成功: MessageId={result.MessageId}"
                            : $"受信メッセージ履歴保存成功: MessageId={result.MessageId}",
                        LogLevel.Debug);
                    return;
                case ChatHistorySaveStatus.Failed:
                    EnqueueLog($"履歴保存失敗: MessageId={result.MessageId}, Error={result.ErrorMessage}", LogLevel.Error);
                    return;
                default:
                    return;
            }
        }

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            // Several transport callbacks originate on WinRT/socket worker threads.
            // Keep this low-level UI sink safe even when a caller did not first go
            // through OnLogReceived; the bounded queue also prevents a remote peer
            // from creating unbounded dispatcher work.
            if (!DispatcherQueue.HasThreadAccess)
            {
                EnqueueLog(message, level);
                return;
            }

            message = SanitizeLogMessage(message);
            LogLevel effectiveLevel = level == LogLevel.Info
                ? LogClassifier.Classify(message)
                : level;

            if (effectiveLevel == LogLevel.Debug && ShowDebugLogCheckBox?.IsChecked != true)
            {
                return;
            }

            bool shouldAutoScroll = LogTextBox.SelectionStart + LogTextBox.SelectionLength >=
                                    Math.Max(0, LogTextBox.Text.Length - 1);
            string line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{effectiveLevel}] {message}";
            _logLines.Add(line);

            bool trimmed = false;
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveRange(
                    0,
                    Math.Min(LogTrimBatchSize, _logLines.Count));
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

            if (shouldAutoScroll)
            {
                MoveLogCaretToEnd();
            }
        }

        private static string SanitizeLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";

            const int maxLogMessageLength = 2048;
            var builder = new StringBuilder(Math.Min(message.Length, maxLogMessageLength));
            bool truncated = false;
            foreach (Rune rune in message.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category == UnicodeCategory.Format)
                {
                    continue;
                }

                string addition = category is UnicodeCategory.Control or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    ? " "
                    : rune.ToString();
                if (builder.Length + addition.Length > maxLogMessageLength)
                {
                    truncated = true;
                    break;
                }
                builder.Append(addition);
            }

            return truncated ? $"{builder}…" : builder.ToString();
        }

        private static string SanitizeUntrustedDisplayText(string? value, int maximumLength = 128)
        {
            if (string.IsNullOrWhiteSpace(value) || maximumLength <= 0) return "";

            var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
            bool truncated = false;
            foreach (Rune rune in value.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    continue;
                }

                string addition = rune.ToString();
                if (builder.Length + addition.Length > maximumLength)
                {
                    truncated = true;
                    break;
                }
                builder.Append(addition);
            }

            string sanitized = builder.ToString().Trim();
            return truncated ? $"{sanitized}…" : sanitized;
        }

        private void MoveLogCaretToEnd()
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.SelectionLength = 0;
        }

        private readonly record struct HistorySaveRequest(
            ChatMessage Message,
            bool IsOutgoing,
            PeerInfo? Peer,
            ChatConnection? Connection,
            ChatHistoryAttachment? Attachment);
    }
}
