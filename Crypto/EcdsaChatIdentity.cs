using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace direct_module.Crypto
{
    internal enum ChatHandshakeRole : byte
    {
        Initiator = 1,
        Acceptor = 2
    }

    /// <summary>
    /// A stable signing identity used to authenticate ephemeral ECDH handshake keys.
    /// Persist ExportPkcs8PrivateKey() in OS-protected application storage and restore
    /// it with ImportPkcs8PrivateKey() on subsequent launches.
    /// </summary>
    public sealed class EcdsaChatIdentity : IDisposable
    {
        private static readonly byte[] SignatureContext = Encoding.ASCII.GetBytes("NOVA-CHAT-IDENTITY-2|");
        private readonly ECDsa _key;
        private readonly object _keyLock = new();
        private bool _disposed;

        private EcdsaChatIdentity(ECDsa key)
        {
            _key = key;
        }

        public static EcdsaChatIdentity Create() =>
            new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        public static EcdsaChatIdentity ImportPkcs8PrivateKey(ReadOnlySpan<byte> privateKey)
        {
            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKey, out int bytesRead);
                if (bytesRead != privateKey.Length) throw new CryptographicException("Trailing data followed the identity private key.");
                if (key.KeySize != 256) throw new CryptographicException("The identity key must use the P-256 curve.");
                return new EcdsaChatIdentity(key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        public byte[] ExportPkcs8PrivateKey()
        {
            lock (_keyLock)
            {
                ThrowIfDisposed();
                return _key.ExportPkcs8PrivateKey();
            }
        }

        public byte[] ExportPublicKey()
        {
            lock (_keyLock)
            {
                ThrowIfDisposed();
                return _key.ExportSubjectPublicKeyInfo();
            }
        }

        public string Fingerprint
        {
            get
            {
                byte[] publicKey = ExportPublicKey();
                try { return Convert.ToHexString(SHA256.HashData(publicKey)); }
                finally { CryptographicOperations.ZeroMemory(publicKey); }
            }
        }

        internal byte[] SignEphemeralKey(
            ReadOnlySpan<byte> ephemeralPublicKey,
            ChatHandshakeRole role)
        {
            byte[] signedData = CreateSignedData(ephemeralPublicKey, role);
            try
            {
                lock (_keyLock)
                {
                    ThrowIfDisposed();
                    return _key.SignData(signedData, HashAlgorithmName.SHA256);
                }
            }
            finally { CryptographicOperations.ZeroMemory(signedData); }
        }

        internal static bool VerifyEphemeralKey(
            ReadOnlySpan<byte> identityPublicKey,
            ReadOnlySpan<byte> ephemeralPublicKey,
            ReadOnlySpan<byte> signature,
            ChatHandshakeRole role)
        {
            byte[] signedData = CreateSignedData(ephemeralPublicKey, role);
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(identityPublicKey, out int bytesRead);
                return bytesRead == identityPublicKey.Length && key.KeySize == 256 &&
                       key.VerifyData(signedData, signature, HashAlgorithmName.SHA256);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signedData);
            }
        }

        internal static string CreatePublicKeyFingerprint(ReadOnlySpan<byte> publicKey) =>
            Convert.ToHexString(SHA256.HashData(publicKey));

        public void Dispose()
        {
            if (_disposed) return;
            lock (_keyLock)
            {
                if (_disposed) return;
                _key.Dispose();
                _disposed = true;
            }
        }

        private static byte[] CreateSignedData(
            ReadOnlySpan<byte> ephemeralPublicKey,
            ChatHandshakeRole role)
        {
            if (role is not (ChatHandshakeRole.Initiator or ChatHandshakeRole.Acceptor))
                throw new ArgumentOutOfRangeException(nameof(role));
            if (ephemeralPublicKey.IsEmpty || ephemeralPublicKey.Length > 1024)
                throw new ArgumentException("A bounded ephemeral public key is required.", nameof(ephemeralPublicKey));

            byte[] data = new byte[checked(
                SignatureContext.Length + sizeof(byte) + sizeof(int) + ephemeralPublicKey.Length)];
            SignatureContext.CopyTo(data, 0);
            int offset = SignatureContext.Length;
            data[offset++] = (byte)role;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, sizeof(int)), ephemeralPublicKey.Length);
            offset += sizeof(int);
            ephemeralPublicKey.CopyTo(data.AsSpan(offset));
            return data;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
