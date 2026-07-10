using System;
using System.IO;
using Windows.Storage;

namespace direct_module.Services
{
    public static class AppStoragePathService
    {
        public static string ResolveAppDataDirectory()
        {
            string localCache = ResolveApplicationLocalCachePath();
            if (!string.IsNullOrWhiteSpace(localCache))
            {
                return Path.Combine(localCache, "Local", "direct_module");
            }

            string localAppData = ResolveUserLocalAppDataPath();
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = AppContext.BaseDirectory;
            }

            return Path.Combine(localAppData, "direct_module");
        }

        private static string ResolveApplicationLocalCachePath()
        {
            try
            {
                return ApplicationData.Current.LocalCacheFolder.Path;
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveUserLocalAppDataPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = string.IsNullOrWhiteSpace(userProfile)
                ? ""
                : Path.Combine(userProfile, "AppData", "Local");

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            }

            return localAppData;
        }
    }
}
