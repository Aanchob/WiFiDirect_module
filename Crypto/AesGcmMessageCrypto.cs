using System;
using System.Security.Cryptography;

namespace direct_module.Crypto
{
    public sealed class AesGcmMessageCrypto : IMessageCrypto, IDisposable
    {
        public const int KeySizeBytes = 32;
        public const int NonceSizeBytes = 12;
        public const int TagSizeBytes = 16;
        private const int MinimumPayloadSize = NonceSizeBytes + TagSizeBytes;

        private readonly byte[] _key;
        private bool _disposed;

        public AesGcmMessageCrypto(byte[] key)
        {
            if (key == null || key.Length != KeySizeBytes)
            {
                throw new ArgumentException(
                    "AES-256では32バイトの鍵が必要です。",
                    nameof(key));
            }

            _key = (byte[])key.Clone();
        }

        public byte[] Encrypt(byte[] plainBytes)
        {
            ThrowIfDisposed();

            if (plainBytes == null)
            {
                throw new ArgumentNullException(nameof(plainBytes));
            }

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            byte[] cipherText = new byte[plainBytes.Length];
            byte[] tag = new byte[TagSizeBytes];

            using var aes = new AesGcm(_key, TagSizeBytes);
            aes.Encrypt(nonce, plainBytes, cipherText, tag);

            byte[] encryptedBytes = new byte[MinimumPayloadSize + cipherText.Length];
            Buffer.BlockCopy(nonce, 0, encryptedBytes, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, encryptedBytes, nonce.Length, tag.Length);
            Buffer.BlockCopy(
                cipherText,
                0,
                encryptedBytes,
                MinimumPayloadSize,
                cipherText.Length);

            return encryptedBytes;
        }

        public byte[] Decrypt(byte[] encryptedBytes)
        {
            ThrowIfDisposed();

            if (encryptedBytes == null || encryptedBytes.Length < MinimumPayloadSize)
            {
                throw new ArgumentException(
                    "暗号データが不正です。",
                    nameof(encryptedBytes));
            }

            byte[] nonce = new byte[NonceSizeBytes];
            byte[] tag = new byte[TagSizeBytes];
            byte[] cipherText = new byte[encryptedBytes.Length - MinimumPayloadSize];

            Buffer.BlockCopy(encryptedBytes, 0, nonce, 0, nonce.Length);
            Buffer.BlockCopy(encryptedBytes, nonce.Length, tag, 0, tag.Length);
            Buffer.BlockCopy(
                encryptedBytes,
                MinimumPayloadSize,
                cipherText,
                0,
                cipherText.Length);

            byte[] plainBytes = new byte[cipherText.Length];

            using var aes = new AesGcm(_key, TagSizeBytes);
            aes.Decrypt(nonce, cipherText, tag, plainBytes);

            return plainBytes;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AesGcmMessageCrypto));
            }
        }
    }
}
