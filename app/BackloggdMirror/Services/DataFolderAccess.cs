using System;
using System.IO;
using System.Linq;

namespace BackloggdMirror.Services;

/// <summary>
/// Checks the data folder can be read and written: every service there swallows its own write
/// errors, so otherwise settings and the remembered login would vanish without a word.
/// </summary>
public static class DataFolderAccess
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd");

    /// <summary>False when the folder or a file in it cannot be read or written.</summary>
    public static bool Check(string folder, IAppLogger? logger)
    {
        try
        {
            Directory.CreateDirectory(folder);

            // A real write and read rather than reading ACLs, so read-only drives and shares are caught too.
            var probe = Path.Combine(folder, $".access-test-{Guid.NewGuid():N}");
            byte[] expected = [1, 2, 3, 4];
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                stream.Write(expected);
                stream.Position = 0;
                var actual = new byte[expected.Length];
                stream.ReadExactly(actual);
                if (!actual.SequenceEqual(expected)) throw new IOException("The probe file read back different bytes.");
            }

            // Logs is skipped: the logger holds today's file open and reports its own failures.
            var logs = Path.Combine(folder, "Logs") + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (file.StartsWith(logs, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger?.Warning($"[DataFolderAccess] '{file}' cannot be read or written: {ex.Message}");
                    return false;
                }
                catch (IOException ex)
                {
                    // Another process holding it for a moment says nothing about permissions.
                    logger?.Info($"[DataFolderAccess] Skipped '{file}' while checking access: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Warning($"[DataFolderAccess] The data folder '{folder}' cannot be read or written: {ex.Message}");
            return false;
        }

        return true;
    }
}
