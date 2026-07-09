using System;
using System.Linq;

namespace direct_module.Services
{
    public static class LogClassifier
    {
        private static readonly string[] ErrorKeywords =
        {
            "失敗",
            "エラー",
            "例外",
            "Exception",
            "HResult",
            "Message:",
            "不正",
            "切断",
            "未接続",
            "送信できません",
            "接続できません",
            "ありません",
            "注意:"
        };

        private static readonly string[] SuccessKeywords =
        {
            "成功",
            "完了",
            "接続済み",
            "送信成功",
            "受信",
            "RemoteIpAddress保存",
            "チャット準備完了",
            "SendMessageButton有効化",
            "Peer統合"
        };

        private static readonly string[] DebugKeywords =
        {
            "Selector",
            "Watcher Status",
            "Added",
            "Updated",
            "Removed",
            "EnumerationCompleted",
            "Stopped",
            "Kind",
            "IsEnabled",
            "InformationElements.Count",
            "LegacySettings.IsEnabled",
            "ListenStateDiscoverability",
            "LocalServiceName",
            "RemoteServiceName",
            "WriteUInt32",
            "WriteBytes",
            "StoreAsync",
            "FlushAsync",
            "平文Bytes",
            "暗号化後Bytes",
            "送信Bytes",
            "送信フレームBytes",
            "length読み取り",
            "本文読み取り",
            "ConnectAsync:",
            "Stopwatch",
            "Elapsed",
            "合計:",
            "ms",
            "Local IP",
            "Local SessionId",
            "Local ShortSessionId",
            "Local TCP Port",
            "Peer照合開始",
            "DeviceIdあり",
            "接続中Peer数",
            "SendAsync内でConnectが必要か",
            "接続状態: IsConnected",
            "MessageId:",
            "SenderName:",
            "MessageCrypto:"
        };

        public static LogLevel Classify(string message)
        {
            if (ContainsAny(message, ErrorKeywords))
            {
                return LogLevel.Error;
            }

            if (ContainsAny(message, DebugKeywords))
            {
                return LogLevel.Debug;
            }

            if (ContainsAny(message, SuccessKeywords))
            {
                return LogLevel.Success;
            }

            return LogLevel.Info;
        }

        private static bool ContainsAny(string message, string[] keywords)
        {
            return keywords.Any(keyword =>
                message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
    }
}
