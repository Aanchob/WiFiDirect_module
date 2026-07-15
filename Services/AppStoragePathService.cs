using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;

namespace direct_module.Services
{
    /// <summary>
    /// Resolves one durable per-user data location for packaged and unpackaged runs.
    /// </summary>
    public static class AppStoragePathService
    {
        private const string PublisherDirectoryName = "Aanchob";
        private const string ApplicationDirectoryName = "WiFiDirect_module";

        public static string ResolveAppDataDirectory()
        {
            string localAppData = ResolveUserLocalAppDataPath();
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.GetFullPath(Path.Combine(
                    localAppData,
                    PublisherDirectoryName,
                    ApplicationDirectoryName));
            }

            string localFolder = ResolveApplicationLocalFolderPath();
            if (!string.IsNullOrWhiteSpace(localFolder))
            {
                return Path.GetFullPath(Path.Combine(localFolder, ApplicationDirectoryName));
            }

            throw new DirectoryNotFoundException("A writable per-user application data directory could not be resolved.");
        }

        /// <summary>
        /// Returns locations used by older packaged and unpackaged builds. Callers can
        /// migrate data from these directories into <see cref="ResolveAppDataDirectory"/>.
        /// </summary>
        public static IReadOnlyList<string> ResolveLegacyAppDataDirectories()
        {
            var candidates = new List<string>();
            string current = ResolveAppDataDirectory();

            string localAppData = ResolveUserLocalAppDataPath();
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                AddCandidate(Path.Combine(localAppData, "direct_module"));
            }

            string localCache = ResolveApplicationLocalCachePath();
            if (!string.IsNullOrWhiteSpace(localCache))
            {
                AddCandidate(Path.Combine(localCache, "Local", "direct_module"));
            }

            string localFolder = ResolveApplicationLocalFolderPath();
            if (!string.IsNullOrWhiteSpace(localFolder))
            {
                AddCandidate(Path.Combine(localFolder, "direct_module"));
            }

            return candidates;

            void AddCandidate(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase) ||
                    candidates.Exists(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                candidates.Add(candidate);
            }
        }

        public static string ResolveOutgoingCacheDirectory()
        {
            string localCache = ResolveApplicationLocalCachePath();
            return string.IsNullOrWhiteSpace(localCache)
                ? Path.Combine(ResolveAppDataDirectory(), "outgoing")
                : Path.Combine(localCache, "outgoing");
        }

        private static string ResolveApplicationLocalCachePath()
        {
            try
            {
                return NormalizeAbsolutePath(ApplicationData.Current.LocalCacheFolder.Path);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveApplicationLocalFolderPath()
        {
            try
            {
                return NormalizeAbsolutePath(ApplicationData.Current.LocalFolder.Path);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveUserLocalAppDataPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            }

            return NormalizeAbsolutePath(localAppData);
        }

        private static string NormalizeAbsolutePath(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                    ? Path.GetFullPath(path)
                    : "";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "";
            }
        }
    }
}
