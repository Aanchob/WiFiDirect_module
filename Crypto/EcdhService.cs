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
            if (localKey.KeySize != 256)
                throw new CryptographicException("The local ECDH key must use the P-256 curve.");

            if (remotePublicKey == null || remotePublicKey.Length == 0 || remotePublicKey.Length > 1024)
            {
                throw new ArgumentException(
                    "相手の公開鍵が不正です。",
                    nameof(remotePublicKey));
            }

            using ECDiffieHellman remoteKey = ECDiffieHellman.Create();
            remoteKey.ImportSubjectPublicKeyInfo(remotePublicKey, out int bytesRead);
            if (bytesRead != remotePublicKey.Length)
            {
                throw new CryptographicException("Trailing data followed the remote ECDH public key.");
            }
            if (remoteKey.KeySize != 256)
                throw new CryptographicException("The remote ECDH key must use the P-256 curve.");

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
            return Convert.ToHexString(hash);
        }

        public static bool FingerprintsEqual(string first, string second)
        {
            string normalizedFirst = NormalizeFingerprint(first);
            string normalizedSecond = NormalizeFingerprint(second);
            if (normalizedFirst.Length == 0 || normalizedFirst.Length != normalizedSecond.Length)
            {
                return false;
            }

            byte[] firstBytes;
            byte[] secondBytes;
            try
            {
                firstBytes = Convert.FromHexString(normalizedFirst);
                secondBytes = Convert.FromHexString(normalizedSecond);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
        }

        private static string NormalizeFingerprint(string value) =>
            (value ?? "").Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
    }
}
