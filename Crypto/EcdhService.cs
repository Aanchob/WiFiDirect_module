using System;
using System.Security.Cryptography;

namespace direct_module.Crypto
{
    public static class EcdhService
    {
        private static readonly byte[] KeyDerivationPrepend =
            "NOVA Chat ECDH v1"u8.ToArray();

        public static ECDiffieHellman Create()
        {
            return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        }

        public static byte[] GetPublicKey(ECDiffieHellman ecdh)
        {
            if (ecdh == null)
            {
                throw new ArgumentNullException(nameof(ecdh));
            }

            return ecdh.ExportSubjectPublicKeyInfo();
        }

        public static byte[] CreateSharedKey(
            ECDiffieHellman localKey,
            byte[] remotePublicKey)
        {
            if (localKey == null)
            {
                throw new ArgumentNullException(nameof(localKey));
            }

            if (remotePublicKey == null || remotePublicKey.Length == 0)
            {
                throw new ArgumentException(
                    "相手の公開鍵が不正です。",
                    nameof(remotePublicKey));
            }

            using ECDiffieHellman remoteKey = ECDiffieHellman.Create();
            remoteKey.ImportSubjectPublicKeyInfo(remotePublicKey, out _);

            return localKey.DeriveKeyFromHash(
                remoteKey.PublicKey,
                HashAlgorithmName.SHA256,
                KeyDerivationPrepend,
                null);
        }

        public static string CreateFingerprint(byte[] publicKey)
        {
            if (publicKey == null || publicKey.Length == 0)
            {
                return "";
            }

            byte[] hash = SHA256.HashData(publicKey);
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
