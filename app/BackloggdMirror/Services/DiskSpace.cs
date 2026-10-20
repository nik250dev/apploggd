using System;
using System.IO;

namespace BackloggdMirror.Services
{
    public static class DiskSpace
    {
        private const long MinimumFreeBytes = 50L * 1024 * 1024;

        // The temp folder counts too: Chromium writes its throwaway profile there.
        public static bool HasEnoughFreeSpace(IAppLogger logger)
        {
            string[] folders =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetTempPath(),
            };

            foreach (var folder in folders)
            {
                try
                {
                    long free = new DriveInfo(folder).AvailableFreeSpace;
                    if (free < MinimumFreeBytes)
                    {
                        logger.Warning($"[DiskSpace] Only {free / (1024 * 1024)} MB free on the drive of '{folder}'; at least {MinimumFreeBytes / (1024 * 1024)} MB are required.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning($"[DiskSpace] Could not read the free space of '{folder}', so it is not blocking: {ex.Message}");
                }
            }

            return true;
        }
    }
}
