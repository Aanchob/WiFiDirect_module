using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace direct_module.Crypto
{
    public sealed class AesGcmMessageCrypto : IMessageCrypto, IDisposable
    {
        public const int KeySizeBytes = 32;
        public const int SequenceSizeBytes = sizeof(ulong);
        public const int NonceSizeBytes = 12;
        public const int TagSizeBytes = 16;
        public const int OverheadSizeBytes = SequenceSizeBytes + NonceSizeBytes + TagSizeBytes;

        private readonly object _gate = new();
        private readonly byte[] _sendKey;
        private readonly byte[] _receiveKey;
        private ulong _nextSendSequence;
        private ulong _nextReceiveSequence;
        private bool _disposed;

        public AesGcmMessageCrypto(byte[] sendKey, byte[] receiveKey)
        {
            if (sendKey == null || sendKey.Length != KeySizeBytes)
                throw new ArgumentException("AES-256 requires a 32-byte send key.", nameof(sendKey));
            if (receiveKey == null || receiveKey.Length != KeySizeBytes)
                throw new ArgumentException("AES-256 requires a 32-byte receive key.", nameof(receiveKey));
            if (CryptographicOperations.FixedTimeEquals(sendKey, receiveKey))
                throw new ArgumentException("Send and receive keys must be direction-specific.", nameof(receiveKey));
            _sendKey = (byte[])sendKey.Clone();
            _receiveKey = (byte[])receiveKey.Clone();
        }

        public byte[] Encrypt(byte[] plainBytes)
        {
            ArgumentNullException.ThrowIfNull(plainBytes);
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_nextSendSequence == ulong.MaxValue)
                    throw new CryptographicException("The encrypted message sequence was exhausted.");

                byte[] encryptedBytes = new byte[checked(OverheadSizeBytes + plainBytes.Length)];
                Span<byte> sequenceBytes = encryptedBytes.AsSpan(0, SequenceSizeBytes);
                Span<byte> nonce = encryptedBytes.AsSpan(SequenceSizeBytes, NonceSizeBytes);
                Span<byte> tag = encryptedBytes.AsSpan(SequenceSizeBytes + NonceSizeBytes, TagSizeBytes);
                Span<byte> cipherText = encryptedBytes.AsSpan(OverheadSizeBytes);
                BinaryPrimitives.WriteUInt64LittleEndian(sequenceBytes, _nextSendSequence);
                RandomNumberGenerator.Fill(nonce);

                using var aes = new AesGcm(_sendKey, TagSizeBytes);
                aes.Encrypt(nonce, plainBytes, cipherText, tag, sequenceBytes);
                _nextSendSequence++;
                return encryptedBytes;
            }
        }

        public byte[] Decrypt(byte[] encryptedBytes)
        {
            ArgumentNullException.ThrowIfNull(encryptedBytes);
            if (encryptedBytes.Length < OverheadSizeBytes)
                throw new ArgumentException("The encrypted payload is truncated.", nameof(encryptedBytes));

            lock (_gate)
            {
                ThrowIfDisposed();
                ReadOnlySpan<byte> sequenceBytes = encryptedBytes.AsSpan(0, SequenceSizeBytes);
                ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(sequenceBytes);
                if (sequence != _nextReceiveSequence)
                    throw new CryptographicException("An encrypted message was replayed or arrived out of sequence.");
                if (_nextReceiveSequence == ulong.MaxValue)
                    throw new CryptographicException("The encrypted message sequence was exhausted.");

                ReadOnlySpan<byte> nonce = encryptedBytes.AsSpan(SequenceSizeBytes, NonceSizeBytes);
                ReadOnlySpan<byte> tag = encryptedBytes.AsSpan(SequenceSizeBytes + NonceSizeBytes, TagSizeBytes);
                ReadOnlySpan<byte> cipherText = encryptedBytes.AsSpan(OverheadSizeBytes);
                byte[] plainBytes = new byte[cipherText.Length];
                try
                {
                    using var aes = new AesGcm(_receiveKey, TagSizeBytes);
                    aes.Decrypt(nonce, cipherText, tag, plainBytes, sequenceBytes);
                    _nextReceiveSequence++;
                    return plainBytes;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                CryptographicOperations.ZeroMemory(_sendKey);
                CryptographicOperations.ZeroMemory(_receiveKey);
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AesGcmMessageCrypto));
        }
    }
}
