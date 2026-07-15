using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace direct_module.Network
{
    /// <summary>
    /// Pure protocol validation shared by the transport and source-linked unit tests.
    /// This type intentionally has no WinRT or socket dependencies.
    /// </summary>
    internal static class ChatMessageValidator
    {
        public const int MaximumRelayHopCount = 1;

        private const int MaximumMessageTypeCharacters = 64;
        private const int MaximumIdentityCharacters = 512;
        private const int MaximumDisplayNameCharacters = 256;
        private const int MaximumShortSessionIdCharacters = 64;
        private const int MaximumConversationIdCharacters = 512;
        private const int MaximumFileNameCharacters = 512;
        private const int MaximumChunkBase64Characters = 174_764;
        private const long MaximumFileSizeBytes = 50L * 1024 * 1024;
        private const int MaximumFileChunkCount = 400;
        private const string DefaultGroupConversationId = "group:all-peers:v1";

        public static void ValidateOutbound(ChatMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (!IsCanonicalWireGuid(message.MessageId))
                throw new InvalidDataException("MessageId must be a canonical D or N format GUID.");
            ValidateBoundedText(message.Type, nameof(message.Type), MaximumMessageTypeCharacters, required: true);
            string normalizedType = (message.Type ?? "").Trim().ToLowerInvariant();
            if (normalizedType is not ("chat" or "hello" or "ping" or "pong" or
                "file_start" or "file_chunk" or "file_end" or "file_abort" or "file_ack"))
            {
                throw new InvalidDataException("The message type is not supported by this protocol version.");
            }
            ValidateStablePeerId(message.SenderId, nameof(message.SenderId));
            ValidateShortSessionId(message.ShortSessionId, nameof(message.ShortSessionId));
            ValidateDisplayName(message.SenderName, nameof(message.SenderName));
            ValidateBoundedText(message.ConversationId, nameof(message.ConversationId), MaximumConversationIdCharacters);
            ValidateSafeUiText(message.FileName, nameof(message.FileName), MaximumFileNameCharacters);
            ValidateBoundedText(message.FileId, nameof(message.FileId), 64);
            ValidateBoundedText(
                message.ChunkBase64,
                nameof(message.ChunkBase64),
                MaximumChunkBase64Characters);
            ValidateDisplayName(message.RelaySenderName, nameof(message.RelaySenderName));

            if (message.FileSize is < 0 or > MaximumFileSizeBytes)
                throw new InvalidDataException("FileSize is outside the protocol limit.");
            if (message.ChunkIndex is < 0 or >= MaximumFileChunkCount)
                throw new InvalidDataException("ChunkIndex is outside the protocol limit.");
            if (message.ChunkCount is < 0 or > MaximumFileChunkCount)
                throw new InvalidDataException("ChunkCount is outside the protocol limit.");

            foreach (char character in message.Type ?? "")
            {
                if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                    throw new InvalidDataException("Message Type contains an unsupported character.");
            }

            if (message.HopCount < 0 || message.HopCount > MaximumRelayHopCount)
                throw new InvalidDataException("The group relay hop count is invalid.");
            if (message.IsGroup &&
                !string.Equals(message.ConversationId, DefaultGroupConversationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The group conversation ID is not the canonical all-peers room.");
            }
            if (message.HopCount == 0 &&
                (!string.IsNullOrEmpty(message.RelaySenderId) ||
                 !string.IsNullOrEmpty(message.RelaySenderName) ||
                 !string.IsNullOrEmpty(message.RelayShortSessionId)))
            {
                throw new InvalidDataException("A direct message cannot contain a relay identity.");
            }
            if (message.HopCount > 0 &&
                (!message.IsGroup || string.IsNullOrWhiteSpace(message.RelaySenderId) ||
                 string.IsNullOrWhiteSpace(message.RelayShortSessionId)))
            {
                throw new InvalidDataException("A relayed group message requires a relay identity.");
            }

            if (message.HopCount > 0)
            {
                ValidateStablePeerId(message.RelaySenderId, nameof(message.RelaySenderId));
                ValidateShortSessionId(message.RelayShortSessionId, nameof(message.RelayShortSessionId));
            }

            bool isAcknowledgement = IsMessageType(message, "file_ack");
            if (isAcknowledgement)
            {
                if (message.HopCount != 0 ||
                    !string.IsNullOrEmpty(message.RelaySenderId) ||
                    !string.IsNullOrEmpty(message.RelaySenderName) ||
                    !string.IsNullOrEmpty(message.RelayShortSessionId))
                {
                    throw new InvalidDataException("A file acknowledgement cannot be relayed.");
                }
                ValidateStablePeerId(message.AcknowledgementTargetId, nameof(message.AcknowledgementTargetId));
                ValidateStablePeerId(message.AcknowledgementSenderId, nameof(message.AcknowledgementSenderId));
                if (!string.Equals(message.AcknowledgementSenderId, message.SenderId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The file acknowledgement sender does not match SenderId.");
                if (!Guid.TryParse(message.FileId, out _))
                    throw new InvalidDataException("A file acknowledgement requires a GUID FileId.");
                ValidateSafeUiText(message.Body, nameof(message.Body), 2048);
            }
            else if (!string.IsNullOrEmpty(message.AcknowledgementTargetId) ||
                     !string.IsNullOrEmpty(message.AcknowledgementSenderId))
            {
                throw new InvalidDataException("Only file_ack may contain acknowledgement identities.");
            }

            if ((normalizedType is "file_start" or "file_chunk" or "file_end" or "file_abort") &&
                !Guid.TryParse(message.FileId, out _))
            {
                throw new InvalidDataException("A file-transfer message requires a GUID FileId.");
            }

            if ((normalizedType is "ping" or "pong") &&
                (message.IsGroup || message.HopCount != 0 ||
                 !string.IsNullOrEmpty(message.Body) ||
                 !string.IsNullOrEmpty(message.ConversationId)))
            {
                throw new InvalidDataException("Keepalive messages must be empty direct messages.");
            }

            if (normalizedType is "hello" or "chat" or "ping" or "pong")
            {
                if (!string.IsNullOrEmpty(message.FileId) ||
                    !string.IsNullOrEmpty(message.FileName) ||
                    message.FileSize.HasValue ||
                    message.ChunkIndex.HasValue ||
                    message.ChunkCount.HasValue ||
                    !string.IsNullOrEmpty(message.ChunkBase64))
                {
                    throw new InvalidDataException($"{normalizedType} messages cannot contain a file-transfer envelope.");
                }
            }

            if ((normalizedType is "file_start" or "file_end" or "file_abort") &&
                (message.ChunkIndex.HasValue || !string.IsNullOrEmpty(message.ChunkBase64)))
            {
                throw new InvalidDataException($"{normalizedType} messages cannot contain chunk payload data.");
            }

            if (normalizedType == "file_ack" &&
                (!string.IsNullOrEmpty(message.FileName) || message.FileSize.HasValue ||
                 message.ChunkIndex.HasValue || message.ChunkCount.HasValue ||
                 !string.IsNullOrEmpty(message.ChunkBase64)))
            {
                throw new InvalidDataException("file_ack cannot contain file payload metadata.");
            }

            if ((normalizedType is "hello" or "file_start" or "file_chunk" or "file_end" or "file_abort") &&
                !string.IsNullOrEmpty(message.Body))
            {
                throw new InvalidDataException($"{normalizedType} messages cannot contain Body data.");
            }

            int maximumBodyCharacters = normalizedType switch
            {
                "chat" => 16 * 1024,
                "file_ack" => 2048,
                _ => 256
            };
            ValidateBoundedText(message.Body, nameof(message.Body), maximumBodyCharacters);
        }

        public static void ValidateHello(ChatMessage message)
        {
            ValidateOutbound(message);
            if (!IsMessageType(message, "hello") || message.HopCount != 0 || message.IsGroup ||
                string.IsNullOrWhiteSpace(message.SenderName) ||
                !string.IsNullOrEmpty(message.ConversationId))
                throw new InvalidDataException("HELLO must be a direct, non-group hello message.");
        }

        internal static void ValidateStablePeerId(string? value, string fieldName)
        {
            ValidateBoundedText(value, fieldName, MaximumIdentityCharacters, required: true);
            string candidate = value!.Trim();
            const string prefix = "peer:";
            if (!string.Equals(value, candidate, StringComparison.Ordinal) ||
                !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(candidate[prefix.Length..], "N", out _))
            {
                throw new InvalidDataException($"{fieldName} must be a v2 stable peer identity.");
            }
        }

        internal static void ValidateShortSessionId(string? value, string fieldName)
        {
            ValidateBoundedText(value, fieldName, MaximumShortSessionIdCharacters, required: true);
            string candidate = value!.Trim();
            if (!string.Equals(value, candidate, StringComparison.Ordinal) ||
                candidate.Length != 4 || candidate.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"{fieldName} must be a four-character hexadecimal session ID.");
        }

        private static void ValidateDisplayName(string? value, string fieldName) =>
            ValidateSafeUiText(value, fieldName, MaximumDisplayNameCharacters);

        private static void ValidateSafeUiText(string? value, string fieldName, int maximumCharacters)
        {
            ValidateBoundedText(value, fieldName, maximumCharacters);
            foreach (Rune rune in (value ?? "").EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    throw new InvalidDataException($"{fieldName} contains unsafe control or formatting characters.");
                }
            }
        }

        private static void ValidateBoundedText(
            string? value,
            string fieldName,
            int maximumCharacters,
            bool required = false)
        {
            if (required && string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"{fieldName} is required.");
            if ((value?.Length ?? 0) > maximumCharacters)
                throw new InvalidDataException($"{fieldName} exceeds its {maximumCharacters}-character limit.");
        }

        private static bool IsMessageType(ChatMessage message, string type) =>
            string.Equals(message.Type, type, StringComparison.OrdinalIgnoreCase);

        private static bool IsCanonicalWireGuid(string? value) =>
            Guid.TryParseExact(value, "D", out _) || Guid.TryParseExact(value, "N", out _);
    }
}
