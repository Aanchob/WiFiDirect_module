using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace direct_module.Crypto
{
    internal enum ChatKeyDirection : byte
    {
        InitiatorToAcceptor = 1,
        AcceptorToInitiator = 2
    }

    /// <summary>
    /// Derives one transport key per direction while binding both ephemeral keys,
    /// both authenticated identities, and the handshake roles into the KDF transcript.
    /// </summary>
    internal static class ChatSessionKeyDerivation
    {
        private const int MaximumTranscriptFieldLength = 4096;
        private static readonly byte[] Context = Encoding.ASCII.GetBytes("NOVA Chat ECDH v3|");

        public static byte[] DeriveDirectionalKey(
            ReadOnlySpan<byte> sharedKey,
            ChatKeyDirection direction,
            ReadOnlySpan<byte> initiatorEphemeralPublicKey,
            ReadOnlySpan<byte> acceptorEphemeralPublicKey,
            ReadOnlySpan<byte> initiatorIdentityPublicKey,
            ReadOnlySpan<byte> acceptorIdentityPublicKey)
        {
            if (sharedKey.Length != AesGcmMessageCrypto.KeySizeBytes)
                throw new ArgumentException("A 32-byte ECDH shared key is required.", nameof(sharedKey));
            if (direction is not (ChatKeyDirection.InitiatorToAcceptor or ChatKeyDirection.AcceptorToInitiator))
                throw new ArgumentOutOfRangeException(nameof(direction));

            ValidateField(initiatorEphemeralPublicKey, nameof(initiatorEphemeralPublicKey));
            ValidateField(acceptorEphemeralPublicKey, nameof(acceptorEphemeralPublicKey));
            ValidateField(initiatorIdentityPublicKey, nameof(initiatorIdentityPublicKey));
            ValidateField(acceptorIdentityPublicKey, nameof(acceptorIdentityPublicKey));

            int transcriptLength = checked(
                Context.Length + sizeof(byte) +
                sizeof(int) * 4 +
                initiatorEphemeralPublicKey.Length +
                acceptorEphemeralPublicKey.Length +
                initiatorIdentityPublicKey.Length +
                acceptorIdentityPublicKey.Length);
            byte[] transcript = new byte[transcriptLength];
            try
            {
                int offset = 0;
                Context.CopyTo(transcript, offset);
                offset += Context.Length;
                transcript[offset++] = (byte)direction;
                WriteField(transcript, ref offset, initiatorEphemeralPublicKey);
                WriteField(transcript, ref offset, acceptorEphemeralPublicKey);
                WriteField(transcript, ref offset, initiatorIdentityPublicKey);
                WriteField(transcript, ref offset, acceptorIdentityPublicKey);
                return HMACSHA256.HashData(sharedKey, transcript);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(transcript);
            }
        }

        private static void ValidateField(ReadOnlySpan<byte> value, string parameterName)
        {
            if (value.IsEmpty || value.Length > MaximumTranscriptFieldLength)
                throw new ArgumentException("A non-empty bounded transcript field is required.", parameterName);
        }

        private static void WriteField(byte[] destination, ref int offset, ReadOnlySpan<byte> value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, sizeof(int)), value.Length);
            offset += sizeof(int);
            value.CopyTo(destination.AsSpan(offset));
            offset += value.Length;
        }
    }
}
