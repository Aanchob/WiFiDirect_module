using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using direct_module.Database;

namespace direct_module.Services
{
    public sealed class RemoteIdentityPinStoreUnavailableException : CryptographicException
    {
        public RemoteIdentityPinStoreUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public sealed class LocalIdentityStoreUnavailableException : CryptographicException
    {
        public LocalIdentityStoreUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public static class LocalIdentityService
    {
        private const string PeerIdPrefix = "peer:";
        private const int MaxPrivateKeyBytes = 64 * 1024;
        private const int MaxIdentityFileBytes = 64 * 1024;
        private const int MaxPrivateKeyFileBytes = 256 * 1024;
        private const int MaxPinStoreFileBytes = 4 * 1024 * 1024;
        private const int MaxPinnedIdentities = 4_096;
        private const int Sha256FingerprintCharacters = 64;
        private const string PeerIdentityFileName = "identity.dat";
        private const string ChatIdentityKeyFileName = "chat-identity.key";
        private const string LocalIdentityInitializedMarkerFileName = "local-identity.initialized";
        private const string LocalIdentityRecoveryMarkerFileName = "local-identity.recovery-required";
        private const string LocalIdentityLockFileName = ".local-identity.lock";
        private const string RemoteIdentityPinStoreFileName = "remote-identity-pins.dat";
        private static readonly TimeSpan ExclusiveStoreLockTimeout = TimeSpan.FromSeconds(5);
        private static readonly object IdentityGate = new();
        private static readonly HashSet<string> UnavailablePinStores = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> UnavailableLocalIdentityStores = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ResetLocalIdentityStores = new(StringComparer.OrdinalIgnoreCase);

        public static string GetOrCreatePeerId()
        {
            lock (IdentityGate)
            {
                string directory = AppStoragePathService.ResolveAppDataDirectory();
                Directory.CreateDirectory(directory);
                using FileStream storeLock = AcquireExclusiveStoreLock(
                    Path.Combine(directory, LocalIdentityLockFileName));
                return GetOrCreatePeerIdFromDirectory(directory);
            }
        }

        public static Guid CreateDiscoverySessionId() => Guid.NewGuid();

        public static string CreateShortSessionId(Guid discoverySessionId) =>
            discoverySessionId.ToString("N")[..4].ToUpperInvariant();

        public static byte[] GetOrCreateChatIdentityPrivateKey(Func<byte[]> keyFactory)
        {
            ArgumentNullException.ThrowIfNull(keyFactory);
            lock (IdentityGate)
            {
                string directory = AppStoragePathService.ResolveAppDataDirectory();
                Directory.CreateDirectory(directory);
                using FileStream storeLock = AcquireExclusiveStoreLock(
                    Path.Combine(directory, LocalIdentityLockFileName));
                _ = GetOrCreatePeerIdFromDirectory(directory);
                string keyPath = Path.Combine(directory, ChatIdentityKeyFileName);
                string initializedMarkerPath = GetLocalIdentityInitializedMarkerPath(directory);
                EnsureLocalIdentityAvailable(directory);

                if (!TryReadPrivateKey(keyPath, out byte[] key))
                {
                    if (PathEntryExists(keyPath))
                    {
                        ThrowLocalIdentityUnavailable(
                            directory,
                            "The persistent chat signing identity is corrupt or cannot be read.");
                    }

                    if (PathEntryExists(initializedMarkerPath))
                    {
                        ThrowLocalIdentityUnavailable(
                            directory,
                            "The persistent chat signing identity is missing after initialization.");
                    }

                    // A marker-less, valid peer identity with no signing key is the
                    // one supported upgrade/first-run state. Once the key is written,
                    // the initialized marker makes any future disappearance fail closed.
                    key = GetOrCreateChatIdentityPrivateKeyFromPath(keyPath, keyFactory);
                }

                try
                {
                    EnsureLocalIdentityInitializedMarker(initializedMarkerPath);
                    return key;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw;
                }
            }
        }

        /// <summary>
        /// Explicitly acknowledges loss of the persistent local peer/signing identity.
        /// Both identity files are quarantined while a durable fail-closed marker is
        /// present. A process restart is required before a replacement identity may be
        /// created, preventing the current process from accidentally mixing identities.
        /// </summary>
        public static void ResetLocalIdentityAfterCorruption()
        {
            string directory = AppStoragePathService.ResolveAppDataDirectory();
            ResetLocalIdentityAfterCorruptionInDirectory(directory);
        }

        private static string GetOrCreatePeerIdFromDirectory(string directory)
        {
            string normalizedDirectory = Path.GetFullPath(directory);
            EnsureLocalIdentityAvailable(normalizedDirectory);
            string identityPath = Path.Combine(normalizedDirectory, PeerIdentityFileName);

            if (TryReadPeerId(identityPath, out string peerId))
            {
                return peerId;
            }

            if (PathEntryExists(identityPath))
            {
                ThrowLocalIdentityUnavailable(
                    normalizedDirectory,
                    "The persistent local peer identity is corrupt or cannot be read.");
            }

            string keyPath = Path.Combine(normalizedDirectory, ChatIdentityKeyFileName);
            if (PathEntryExists(keyPath) ||
                PathEntryExists(GetLocalIdentityInitializedMarkerPath(normalizedDirectory)))
            {
                ThrowLocalIdentityUnavailable(
                    normalizedDirectory,
                    "The persistent local peer identity is missing while its signing identity still exists.");
            }

            peerId = PeerIdPrefix + Guid.NewGuid().ToString("N");
            try
            {
                WriteProtectedAtomic(
                    identityPath,
                    UserDataProtection.ProtectString(peerId),
                    replaceExisting: false);
            }
            catch (IOException) when (TryReadPeerId(identityPath, out string existingPeerId))
            {
                return existingPeerId;
            }

            return peerId;
        }

        private static byte[] GetOrCreateChatIdentityPrivateKeyFromPath(
            string keyPath,
            Func<byte[]> keyFactory)
        {
            if (TryReadPrivateKey(keyPath, out byte[] existing))
            {
                return existing;
            }

            if (PathEntryExists(keyPath))
            {
                ThrowLocalIdentityUnavailable(
                    Path.GetDirectoryName(Path.GetFullPath(keyPath))!,
                    "The persistent chat signing identity is corrupt or cannot be read.");
            }

            byte[] created = keyFactory();
            if (created == null || created.Length == 0 || created.Length > MaxPrivateKeyBytes)
            {
                if (created != null)
                {
                    CryptographicOperations.ZeroMemory(created);
                }
                throw new InvalidDataException("The chat identity key factory returned invalid key material.");
            }
            if (!IsValidChatIdentityPrivateKey(created))
            {
                CryptographicOperations.ZeroMemory(created);
                throw new InvalidDataException(
                    "The chat identity key factory did not return one complete P-256 PKCS#8 private key.");
            }

            try
            {
                WriteProtectedAtomic(
                    keyPath,
                    UserDataProtection.ProtectBytesToString(created),
                    replaceExisting: false);
                return created;
            }
            catch (IOException) when (TryReadPrivateKey(keyPath, out byte[] racedExisting))
            {
                CryptographicOperations.ZeroMemory(created);
                return racedExisting;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(created);
                throw;
            }
        }

        public static string? GetPinnedRemoteIdentityFingerprint(string peerId)
        {
            string normalizedPeerId = NormalizePeerId(peerId);
            lock (IdentityGate)
            {
                string path = GetRemoteIdentityPinStorePath();
                using FileStream storeLock = AcquireExclusiveStoreLock(path + ".lock");
                Dictionary<string, string> pins = ReadPinsFromPath(path);
                return pins.TryGetValue(normalizedPeerId, out string? fingerprint)
                    ? fingerprint
                    : null;
            }
        }

        /// <summary>
        /// Stores a first-use pin. An existing, different fingerprint is never replaced.
        /// </summary>
        public static void SaveRemoteIdentityFingerprint(string peerId, string fingerprint)
        {
            string normalizedPeerId = NormalizePeerId(peerId);
            string normalizedFingerprint = NormalizeFingerprint(fingerprint);
            lock (IdentityGate)
            {
                string path = GetRemoteIdentityPinStorePath();
                using FileStream storeLock = AcquireExclusiveStoreLock(path + ".lock");
                Dictionary<string, string> pins = ReadPinsFromPath(path);
                if (pins.TryGetValue(normalizedPeerId, out string? existing))
                {
                    if (!FingerprintsEqual(existing, normalizedFingerprint))
                    {
                        throw new CryptographicException("The remote identity fingerprint does not match its existing TOFU pin.");
                    }
                    return;
                }

                if (pins.Count >= MaxPinnedIdentities)
                {
                    throw new InvalidOperationException("The remote identity pin store has reached its safety limit.");
                }

                pins.Add(normalizedPeerId, normalizedFingerprint);
                WritePins(path, pins);
            }
        }

        /// <summary>
        /// Pins a previously unseen peer and returns true. For an existing peer it
        /// returns true only when the fingerprint matches the stored pin.
        /// </summary>
        public static bool VerifyOrPinRemoteIdentityFingerprint(string peerId, string fingerprint)
        {
            string normalizedPeerId = NormalizePeerId(peerId);
            string normalizedFingerprint = NormalizeFingerprint(fingerprint);
            lock (IdentityGate)
            {
                string path = GetRemoteIdentityPinStorePath();
                using FileStream storeLock = AcquireExclusiveStoreLock(path + ".lock");
                Dictionary<string, string> pins = ReadPinsFromPath(path);
                if (pins.TryGetValue(normalizedPeerId, out string? existing))
                {
                    return FingerprintsEqual(existing, normalizedFingerprint);
                }

                if (pins.Count >= MaxPinnedIdentities)
                {
                    return false;
                }

                pins.Add(normalizedPeerId, normalizedFingerprint);
                WritePins(path, pins);
                return true;
            }
        }

        /// <summary>
        /// Explicitly acknowledges loss of a corrupt TOFU store. Until this is
        /// called, corruption remains fail-closed across application restarts.
        /// </summary>
        public static void ResetRemoteIdentityPinStoreAfterCorruption()
        {
            string path = GetRemoteIdentityPinStorePath();
            ResetRemoteIdentityPinStoreAfterCorruption(path);
        }

        private static void ResetLocalIdentityAfterCorruptionInDirectory(string directory)
        {
            string normalizedDirectory = Path.GetFullPath(directory);
            string recoveryMarkerPath = GetLocalIdentityRecoveryMarkerPath(normalizedDirectory);
            lock (IdentityGate)
            {
                Directory.CreateDirectory(normalizedDirectory);
                using FileStream storeLock = AcquireExclusiveStoreLock(
                    Path.Combine(normalizedDirectory, LocalIdentityLockFileName));
                if (ResetLocalIdentityStores.Contains(normalizedDirectory))
                {
                    return;
                }

                if (!UnavailableLocalIdentityStores.Contains(normalizedDirectory) &&
                    !PathEntryExists(recoveryMarkerPath))
                {
                    throw new InvalidOperationException(
                        "The local identity store is not awaiting corruption recovery.");
                }

                try
                {
                    EnsureRecoveryMarker(recoveryMarkerPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidOperationException)
                {
                    UnavailableLocalIdentityStores.Add(normalizedDirectory);
                    throw new LocalIdentityStoreUnavailableException(
                        "A durable local identity recovery marker could not be created; recovery remains blocked.",
                        ex);
                }

                string[] paths =
                {
                    Path.Combine(normalizedDirectory, PeerIdentityFileName),
                    Path.Combine(normalizedDirectory, ChatIdentityKeyFileName),
                    GetLocalIdentityInitializedMarkerPath(normalizedDirectory)
                };
                string[] labels =
                {
                    "local-peer-identity-recovered",
                    "chat-identity-recovered",
                    "local-identity-state-recovered"
                };
                for (int index = 0; index < paths.Length; index++)
                {
                    if (PathEntryExists(paths[index]) &&
                        (!TryQuarantine(paths[index], labels[index]) || PathEntryExists(paths[index])))
                    {
                        UnavailableLocalIdentityStores.Add(normalizedDirectory);
                        throw new LocalIdentityStoreUnavailableException(
                            "The local peer and signing identities could not both be quarantined; recovery remains blocked.");
                    }
                }

                try
                {
                    if (PathEntryExists(recoveryMarkerPath))
                    {
                        File.Delete(recoveryMarkerPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    UnavailableLocalIdentityStores.Add(normalizedDirectory);
                    throw new LocalIdentityStoreUnavailableException(
                        "The local identity recovery marker could not be cleared; recovery remains blocked.",
                        ex);
                }

                UnavailableLocalIdentityStores.Remove(normalizedDirectory);
                ResetLocalIdentityStores.Add(normalizedDirectory);
            }
        }

        private static void ResetRemoteIdentityPinStoreAfterCorruption(string path)
        {
            string normalizedPath = Path.GetFullPath(path);
            string markerPath = GetPinRecoveryMarkerPath(path);
            lock (IdentityGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath)!);
                using FileStream storeLock = AcquireExclusiveStoreLock(normalizedPath + ".lock");
                // Never clear the durable fail-closed marker while the corrupt
                // store could become visible again. Quarantine must succeed first.
                if (PathEntryExists(path) &&
                    (!TryQuarantine(path, "remote-identity-pins-recovered") || PathEntryExists(path)))
                {
                    UnavailablePinStores.Add(normalizedPath);
                    throw new RemoteIdentityPinStoreUnavailableException(
                        "The corrupt remote identity pin store could not be quarantined; recovery remains blocked.");
                }

                try
                {
                    if (PathEntryExists(markerPath))
                    {
                        File.Delete(markerPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    UnavailablePinStores.Add(normalizedPath);
                    throw new RemoteIdentityPinStoreUnavailableException(
                        "The remote identity recovery marker could not be cleared; recovery remains blocked.",
                        ex);
                }

                UnavailablePinStores.Remove(normalizedPath);
            }
        }

        private static bool TryReadPeerId(string path, out string peerId)
        {
            peerId = "";
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }
                if (IsReparsePoint(path))
                {
                    return false;
                }

                string protectedValue = ReadUtf8TextBounded(path, MaxIdentityFileBytes);
                if (!UserDataProtection.IsProtected(protectedValue))
                {
                    return false;
                }

                string candidate = UserDataProtection.UnprotectString(protectedValue);
                if (!candidate.StartsWith(PeerIdPrefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(candidate[PeerIdPrefix.Length..], "N", out _))
                {
                    return false;
                }

                peerId = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPrivateKey(string path, out byte[] key)
        {
            key = Array.Empty<byte>();
            byte[]? candidate = null;
            bool accepted = false;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }
                if (IsReparsePoint(path))
                {
                    return false;
                }

                string protectedValue = ReadUtf8TextBounded(path, MaxPrivateKeyFileBytes);
                if (!UserDataProtection.IsProtected(protectedValue))
                {
                    return false;
                }

                candidate = UserDataProtection.UnprotectBytesFromString(protectedValue);
                if (candidate.Length == 0 || candidate.Length > MaxPrivateKeyBytes)
                {
                    return false;
                }

                if (!IsValidChatIdentityPrivateKey(candidate))
                {
                    return false;
                }

                key = candidate;
                candidate = null;
                accepted = true;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (!accepted && candidate != null)
                {
                    CryptographicOperations.ZeroMemory(candidate);
                }
            }
        }

        private static bool IsValidChatIdentityPrivateKey(ReadOnlySpan<byte> candidate)
        {
            ECParameters parameters = default;
            try
            {
                using ECDsa verifier = ECDsa.Create();
                verifier.ImportPkcs8PrivateKey(candidate, out int bytesRead);
                if (bytesRead != candidate.Length)
                {
                    return false;
                }

                parameters = verifier.ExportParameters(includePrivateParameters: true);
                return string.Equals(
                           parameters.Curve.Oid.Value,
                           ECCurve.NamedCurves.nistP256.Oid.Value,
                           StringComparison.Ordinal) &&
                       parameters.D?.Length == 32 &&
                       parameters.Q.X?.Length == 32 &&
                       parameters.Q.Y?.Length == 32;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return false;
            }
            finally
            {
                if (parameters.D != null)
                {
                    CryptographicOperations.ZeroMemory(parameters.D);
                }
                if (parameters.Q.X != null)
                {
                    CryptographicOperations.ZeroMemory(parameters.Q.X);
                }
                if (parameters.Q.Y != null)
                {
                    CryptographicOperations.ZeroMemory(parameters.Q.Y);
                }
            }
        }

        private static void EnsureLocalIdentityAvailable(string directory)
        {
            string normalizedDirectory = Path.GetFullPath(directory);
            string recoveryMarkerPath = GetLocalIdentityRecoveryMarkerPath(normalizedDirectory);
            if (ResetLocalIdentityStores.Contains(normalizedDirectory))
            {
                throw new LocalIdentityStoreUnavailableException(
                    "The local identity was reset. Restart the application before creating a replacement identity.");
            }

            if (UnavailableLocalIdentityStores.Contains(normalizedDirectory) ||
                PathEntryExists(recoveryMarkerPath))
            {
                UnavailableLocalIdentityStores.Add(normalizedDirectory);
                throw new LocalIdentityStoreUnavailableException(
                    $"The local peer/signing identity is unavailable. Explicit recovery is required ({recoveryMarkerPath}).");
            }
        }

        private static void ThrowLocalIdentityUnavailable(
            string directory,
            string message,
            Exception? innerException = null)
        {
            string normalizedDirectory = Path.GetFullPath(directory);
            string recoveryMarkerPath = GetLocalIdentityRecoveryMarkerPath(normalizedDirectory);
            UnavailableLocalIdentityStores.Add(normalizedDirectory);
            Exception? markerFailure = null;
            try
            {
                EnsureRecoveryMarker(recoveryMarkerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                markerFailure = ex;
            }

            throw new LocalIdentityStoreUnavailableException(
                markerFailure == null
                    ? $"{message} Explicit recovery is required ({recoveryMarkerPath})."
                    : $"{message} The durable recovery marker could not be written; this process remains fail-closed.",
                markerFailure ?? innerException);
        }

        private static string GetLocalIdentityInitializedMarkerPath(string directory) =>
            Path.Combine(directory, LocalIdentityInitializedMarkerFileName);

        private static string GetLocalIdentityRecoveryMarkerPath(string directory) =>
            Path.Combine(directory, LocalIdentityRecoveryMarkerFileName);

        private static void EnsureLocalIdentityInitializedMarker(string markerPath)
        {
            if (File.Exists(markerPath))
            {
                return;
            }
            if (Directory.Exists(markerPath))
            {
                throw new IOException("The local identity initialized marker path is occupied by a directory.");
            }

            WriteMarkerAtomic(
                markerPath,
                $"Persistent local peer and signing identities initialized: {DateTime.UtcNow:O}");
        }

        private static void EnsureRecoveryMarker(string markerPath)
        {
            if (File.Exists(markerPath))
            {
                return;
            }
            if (Directory.Exists(markerPath))
            {
                throw new IOException("The local identity recovery marker path is occupied by a directory.");
            }

            WriteMarkerAtomic(
                markerPath,
                $"Local peer/signing identity recovery required: {DateTime.UtcNow:O}");
        }

        private static void WriteMarkerAtomic(string markerPath, string value)
        {
            string directory = Path.GetDirectoryName(markerPath)
                ?? throw new InvalidOperationException("The identity marker directory is invalid.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $".identity-marker-{Guid.NewGuid():N}.tmp");
            try
            {
                byte[] encoded = new UTF8Encoding(false, true).GetBytes(value);
                try
                {
                    using var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4 * 1024,
                        FileOptions.WriteThrough);
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }

                try
                {
                    File.Move(temporaryPath, markerPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(markerPath))
                {
                    // Another process established the same durable marker first.
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static Dictionary<string, string> ReadPinsFromPath(string path)
        {
            string normalizedPath = Path.GetFullPath(path);
            string recoveryMarkerPath = GetPinRecoveryMarkerPath(path);
            lock (IdentityGate)
            {
                if (UnavailablePinStores.Contains(normalizedPath) || PathEntryExists(recoveryMarkerPath))
                {
                    UnavailablePinStores.Add(normalizedPath);
                    throw new RemoteIdentityPinStoreUnavailableException(
                        $"Remote identity pins are unavailable. Explicit recovery is required ({recoveryMarkerPath}).");
                }
            }

            try
            {
                if (!File.Exists(path))
                {
                    if (Directory.Exists(path))
                    {
                        throw new InvalidDataException(
                            "The remote identity pin-store path is occupied by a directory.");
                    }
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }
                if (IsReparsePoint(path))
                {
                    throw new InvalidDataException(
                        "The remote identity pin store cannot be a reparse point.");
                }

                string protectedValue = ReadUtf8TextBounded(path, MaxPinStoreFileBytes);
                if (!UserDataProtection.IsProtected(protectedValue))
                {
                    throw new InvalidDataException("The pin store is not DPAPI protected.");
                }

                string json = UserDataProtection.UnprotectString(protectedValue);
                Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    json,
                    new JsonSerializerOptions { MaxDepth = 4 });
                var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
                if (values == null)
                {
                    return normalized;
                }

                if (values.Count > MaxPinnedIdentities)
                {
                    throw new InvalidDataException("The pin store exceeds its safety limit.");
                }

                foreach ((string peerId, string fingerprint) in values)
                {
                    string normalizedPeerId = NormalizePeerId(peerId);
                    string normalizedFingerprint = NormalizeFingerprint(fingerprint);
                    if (normalized.TryGetValue(normalizedPeerId, out string? existing) &&
                        !FingerprintsEqual(existing, normalizedFingerprint))
                    {
                        throw new InvalidDataException("The pin store contains conflicting peer identities.");
                    }

                    normalized[normalizedPeerId] = normalizedFingerprint;
                }

                return normalized;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or CryptographicException or JsonException or
                ArgumentException or DecoderFallbackException)
            {
                lock (IdentityGate)
                {
                    UnavailablePinStores.Add(normalizedPath);
                }
                if (TryCreateRecoveryMarker(recoveryMarkerPath))
                {
                    TryQuarantine(path, "remote-identity-pins");
                }
                throw new RemoteIdentityPinStoreUnavailableException(
                    $"Remote identity pins are corrupt. Explicit recovery is required ({recoveryMarkerPath}).",
                    ex);
            }
        }

        private static void WritePins(string path, Dictionary<string, string> pins)
        {
            if (pins.Count > MaxPinnedIdentities)
            {
                throw new InvalidOperationException("The remote identity pin store has reached its safety limit.");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("The remote identity pin-store directory is invalid.");
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(pins);
            WriteProtectedAtomic(path, UserDataProtection.ProtectString(json), replaceExisting: true);
        }

        private static void WriteProtectedAtomic(
            string destinationPath,
            string protectedValue,
            bool replaceExisting)
        {
            string directory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("The identity storage directory is invalid.");
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $".identity-{Guid.NewGuid():N}.tmp");
            try
            {
                byte[] encoded = new UTF8Encoding(false, true).GetBytes(protectedValue);
                try
                {
                    using var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 16 * 1024,
                        FileOptions.WriteThrough);
                    stream.Write(encoded);
                    stream.Flush(flushToDisk: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }

                if (replaceExisting && File.Exists(destinationPath))
                {
                    try
                    {
                        File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                    }
                    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
                    {
                        File.Move(temporaryPath, destinationPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static string ReadUtf8TextBounded(string path, int maximumBytes)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
            {
                throw new InvalidDataException("The identity data file exceeds its safety limit.");
            }

            byte[] bytes = new byte[maximumBytes + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maximumBytes || stream.ReadByte() != -1)
            {
                throw new InvalidDataException("The identity data file exceeds its safety limit.");
            }

            return new UTF8Encoding(false, true).GetString(bytes, 0, total);
        }

        private static bool TryQuarantine(string path, string label)
        {
            bool isFile = File.Exists(path);
            bool isDirectory = Directory.Exists(path);
            if (!isFile && !isDirectory)
            {
                return true;
            }

            string directory = Path.GetDirectoryName(path) ?? "";
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string corruptPath = Path.Combine(
                    directory,
                    $"{label}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.dat");
                try
                {
                    if (isDirectory)
                    {
                        Directory.Move(path, corruptPath);
                    }
                    else
                    {
                        File.Move(path, corruptPath, overwrite: false);
                    }
                    return true;
                }
                catch (IOException) when (PathEntryExists(path) && PathEntryExists(corruptPath))
                {
                    // Extremely unlikely name collision; generate another name.
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool PathEntryExists(string path) =>
            File.Exists(path) || Directory.Exists(path);

        private static bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static string GetRemoteIdentityPinStorePath() =>
            Path.Combine(
                AppStoragePathService.ResolveAppDataDirectory(),
                RemoteIdentityPinStoreFileName);

        private static FileStream AcquireExclusiveStoreLock(string lockPath)
        {
            string normalizedPath = Path.GetFullPath(lockPath);
            Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath)!);
            long deadline = Environment.TickCount64 + (long)ExclusiveStoreLockTimeout.TotalMilliseconds;
            IOException? lastFailure = null;
            while (true)
            {
                try
                {
                    return new FileStream(
                        normalizedPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                }
                catch (IOException ex) when (Environment.TickCount64 < deadline)
                {
                    lastFailure = ex;
                    Thread.Sleep(25);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"The identity store lock could not be acquired within {ExclusiveStoreLockTimeout.TotalSeconds:F0} seconds.",
                        ex);
                }

                if (Environment.TickCount64 >= deadline)
                {
                    throw new IOException(
                        $"The identity store lock could not be acquired within {ExclusiveStoreLockTimeout.TotalSeconds:F0} seconds.",
                        lastFailure);
                }
            }
        }

        private static string GetPinRecoveryMarkerPath(string pinStorePath) =>
            pinStorePath + ".recovery-required";

        private static bool TryCreateRecoveryMarker(string markerPath)
        {
            try
            {
                WriteMarkerAtomic(
                    markerPath,
                    $"Remote identity pin recovery required: {DateTime.UtcNow:O}");
                return true;
            }
            catch
            {
                // Keep the corrupt store in place so a later process cannot mistake
                // its absence for a first-use empty store.
                return false;
            }
        }

        private static string NormalizePeerId(string peerId)
        {
            string value = peerId?.Trim() ?? "";
            if (value.Length == 0 || value.Length > 512 || value.Any(char.IsControl))
            {
                throw new ArgumentException("A valid stable peer identity is required.", nameof(peerId));
            }

            return value.ToUpperInvariant();
        }

        private static string NormalizeFingerprint(string fingerprint)
        {
            string value = (fingerprint ?? "")
                .Replace(":", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Trim()
                .ToUpperInvariant();
            if (value.Length != Sha256FingerprintCharacters ||
                value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException("A SHA-256 identity fingerprint is required.", nameof(fingerprint));
            }

            return value;
        }

        private static bool FingerprintsEqual(string first, string second)
        {
            byte[] firstBytes = Convert.FromHexString(first);
            byte[] secondBytes = Convert.FromHexString(second);
            return CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // A stale temp file is harmless and will be retried on the next launch.
            }
        }
    }
}
