using System;
using System.IO;

namespace BackloggdMirror.Services;

/// <summary>
/// Deletes everything Apploggd keeps on disk (credentials, encryption keys, settings, the
/// downloaded games database...) while preserving the logs folder, which is still what makes
/// errors diagnosable after a wipe.
/// </summary>
public class AppDataResetService
{
    /// <summary>Entries in the data folder that are never deleted.</summary>
    private static readonly string[] PreservedEntries = { "Logs" };

    private readonly IAppLogger _logger;
    private readonly string _dataFolder;

    public AppDataResetService(IAppLogger logger, string? customDataFolder = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataFolder = customDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd");
    }

    /// <summary>
    /// Removes the whole contents of the data folder except <see cref="PreservedEntries"/>.
    /// Best effort: a failing entry is logged and the rest still get deleted, since a file locked
    /// by another process should not leave the remaining data behind.
    /// </summary>
    /// <returns>True if everything that had to be deleted was deleted.</returns>
    public bool ClearAll()
    {
        if (!Directory.Exists(_dataFolder))
        {
            _logger.Info($"[AppDataResetService] Nothing to delete, '{_dataFolder}' does not exist.");
            return true;
        }

        _logger.Info($"[AppDataResetService] Deleting application data in '{_dataFolder}' (logs are preserved).");

        bool success = true;

        foreach (var directory in Directory.GetDirectories(_dataFolder))
        {
            if (IsPreserved(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, true);
                _logger.Info($"[AppDataResetService] Deleted directory '{directory}'.");
            }
            catch (Exception ex)
            {
                success = false;
                _logger.Error($"[AppDataResetService] Could not delete directory '{directory}'.", ex);
            }
        }

        foreach (var file in Directory.GetFiles(_dataFolder))
        {
            if (IsPreserved(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                _logger.Info($"[AppDataResetService] Deleted file '{file}'.");
            }
            catch (Exception ex)
            {
                success = false;
                _logger.Error($"[AppDataResetService] Could not delete file '{file}'.", ex);
            }
        }

        return success;
    }

    private static bool IsPreserved(string path)
    {
        var name = Path.GetFileName(path);

        foreach (var preserved in PreservedEntries)
        {
            if (string.Equals(name, preserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
