using System;
using System.Globalization;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Storage.Streams;

namespace direct_module.WiFiDirect;

internal readonly record struct DchatInformation(
    string DisplayName,
    string PeerIdentity,
    string ShortSessionId,
    string RoleKey,
    int TcpPort);

internal static class DchatInformationElement
{
    public const byte OuiType = 1;
    private const int MaximumDisplayNameBytes = 64;
    private const int MaximumValueBytes = 97;
    private static readonly byte[] AppOui = { 0x44, 0x43, 0x48 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static WiFiDirectInformationElement Create(
        string displayName,
        string peerIdentity,
        string shortSessionId,
        int tcpPort)
    {
        string identity = NormalizeIdentity(peerIdentity);
        if (string.IsNullOrEmpty(identity))
        {
            identity = NormalizeIdentity(shortSessionId);
        }

        if (identity.Length != 24)
        {
            throw new ArgumentException("A v2 96-bit hexadecimal peer identity is required.", nameof(peerIdentity));
        }

        string normalizedShortSessionId = NormalizeIdentity(shortSessionId);
        if (!string.IsNullOrEmpty(normalizedShortSessionId) &&
            !identity.StartsWith(normalizedShortSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Peer identity and short session ID are inconsistent.", nameof(shortSessionId));
        }

        if (tcpPort is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }

        string safeName = TruncateUtf8(
            SanitizeDisplayName((displayName ?? "").Replace('|', ' ')),
            MaximumDisplayNameBytes);
        string value = $"2|{identity}|{tcpPort}|{safeName}";

        return new WiFiDirectInformationElement
        {
            Oui = CreateBuffer(AppOui),
            OuiType = OuiType,
            Value = CreateBuffer(Encoding.UTF8.GetBytes(value))
        };
    }

    public static bool TryParse(WiFiDirectInformationElement element, out DchatInformation information)
    {
        information = default;
        if (element == null || element.OuiType != OuiType ||
            element.Oui == null || element.Oui.Length != (uint)AppOui.Length ||
            element.Value == null || element.Value.Length > (uint)MaximumValueBytes)
        {
            return false;
        }

        string value;
        try
        {
            if (!ReadBuffer(element.Oui).AsSpan().SequenceEqual(AppOui))
            {
                return false;
            }

            value = StrictUtf8.GetString(ReadBuffer(element.Value));
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or InvalidOperationException)
        {
            return false;
        }

        string[] parts = value.Split('|', 4);
        if (parts.Length == 4 &&
            string.Equals(parts[0], "2", StringComparison.Ordinal) &&
            TryNormalizeIdentity(parts[1], out string identity) &&
            identity.Length == 24 &&
            int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int tcpPort) &&
            tcpPort is > 0 and <= ushort.MaxValue &&
            !parts[3].Contains('|'))
        {
            if (StrictUtf8.GetByteCount(parts[3]) > MaximumDisplayNameBytes)
            {
                return false;
            }

            string safeName = SanitizeDisplayName(parts[3]);
            string displayName = string.IsNullOrWhiteSpace(safeName)
                ? $"Peer {identity[..4]}"
                : safeName;

            information = new DchatInformation(
                displayName,
                identity,
                identity[..4],
                identity.Length >= 8 ? identity[..8] : "",
                tcpPort);
            return true;
        }

        return false;
    }

    public static bool TryParse(DeviceInformation deviceInformation, out DchatInformation information)
    {
        information = default;
        try
        {
            DchatInformation? candidate = null;
            foreach (WiFiDirectInformationElement element in
                     WiFiDirectInformationElement.CreateFromDeviceInformation(deviceInformation))
            {
                if (!TryParse(element, out DchatInformation parsed))
                {
                    continue;
                }

                if (candidate.HasValue && candidate.Value != parsed)
                {
                    // Multiple contradictory app elements make the discovery
                    // identity ambiguous. Never let enumeration order choose which
                    // endpoint/identity tuple is trusted by peer reconciliation.
                    information = default;
                    return false;
                }

                candidate = parsed;
            }

            if (candidate.HasValue)
            {
                information = candidate.Value;
                return true;
            }
        }
        catch (Exception)
        {
            // Some Windows/device combinations do not expose information elements.
        }

        return false;
    }

    private static string NormalizeIdentity(string value)
    {
        return TryNormalizeIdentity(value, out string normalized) ? normalized : "";
    }

    private static bool TryNormalizeIdentity(string value, out string normalized)
    {
        normalized = (value ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (normalized.Length is < 4 or > 64 || normalized.Length % 2 != 0)
        {
            normalized = "";
            return false;
        }

        foreach (char character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                normalized = "";
                return false;
            }
        }

        return true;
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder();
        int bytes = 0;
        foreach (Rune rune in value.Trim().EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static string SanitizeDisplayName(string value)
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

    private static IBuffer CreateBuffer(ReadOnlySpan<byte> bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes.ToArray());
        return writer.DetachBuffer();
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        byte[] bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
