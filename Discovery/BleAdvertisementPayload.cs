using System;
using System.Buffers.Binary;
using System.Text;

namespace direct_module.Discovery;

internal readonly record struct BleAdvertisementData(
    string DisplayName,
    string PeerIdentity,
    string ShortSessionId,
    string RoleKey,
    int TcpPort);

internal static class BleAdvertisementPayload
{
    public const byte CurrentVersion = 1;
    public const int MaximumPayloadBytes = 19;

    private const int IdentityBytes = 12;
    private const int HeaderBytes = 1 + IdentityBytes + sizeof(ushort);
    private const int MaximumNameBytes = MaximumPayloadBytes - HeaderBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Create(string displayName, Guid sessionId, int tcpPort)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty discovery session ID is required.", nameof(sessionId));
        }

        if (tcpPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort), "TCP port must be between 1 and 65535.");
        }

        byte[] nameBytes = EncodeName(SanitizeName(displayName ?? ""));
        byte[] payload = new byte[HeaderBytes + nameBytes.Length];
        payload[0] = CurrentVersion;

        byte[] identity = Convert.FromHexString(sessionId.ToString("N")[..(IdentityBytes * 2)]);
        identity.CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1 + IdentityBytes, sizeof(ushort)), (ushort)tcpPort);
        nameBytes.CopyTo(payload, HeaderBytes);

        return payload;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out BleAdvertisementData data)
    {
        data = default;

        if (payload.Length is < HeaderBytes or > MaximumPayloadBytes || payload[0] != CurrentVersion)
        {
            return false;
        }

        ReadOnlySpan<byte> identityBytes = payload.Slice(1, IdentityBytes);
        if (IsAllZero(identityBytes))
        {
            return false;
        }

        int tcpPort = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1 + IdentityBytes, sizeof(ushort)));
        if (tcpPort == 0)
        {
            return false;
        }

        string peerIdentity = Convert.ToHexString(identityBytes).ToLowerInvariant();
        string displayName;

        try
        {
            displayName = payload.Length == HeaderBytes
                ? $"Peer {peerIdentity[..4]}"
                : StrictUtf8.GetString(payload[HeaderBytes..]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        displayName = SanitizeName(displayName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = $"Peer {peerIdentity[..4]}";
        }

        data = new BleAdvertisementData(
            displayName,
            peerIdentity,
            peerIdentity[..4],
            peerIdentity[..8],
            tcpPort);
        return true;
    }

    private static byte[] EncodeName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Array.Empty<byte>();
        }

        Span<byte> buffer = stackalloc byte[MaximumNameBytes];
        int written = 0;

        foreach (Rune rune in displayName.Trim().EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (written + runeBytes > buffer.Length)
            {
                break;
            }

            rune.EncodeToUtf8(buffer[written..]);
            written += runeBytes;
        }

        return buffer[..written].ToArray();
    }

    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!Rune.IsControl(rune) &&
                Rune.GetUnicodeCategory(rune) is not (System.Globalization.UnicodeCategory.Format or
                    System.Globalization.UnicodeCategory.LineSeparator or
                    System.Globalization.UnicodeCategory.ParagraphSeparator))
            {
                builder.Append(rune);
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }

        return true;
    }

}
