using System;
using System.IO;
using System.Text;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>The Linux counterpart of the Windows Run key: a desktop entry in the XDG autostart folder, launched at login.</summary>
internal static class LinuxAutostartEntry
{
    private const string FileName = "apploggd.desktop";

    private static string FilePath => Path.Combine(LinuxDesktopFile.ConfigHome, "autostart", FileName);

    public static bool SetEnabled(bool enable, IAppLogger logger)
    {
        string path = FilePath;
        try
        {
            if (!enable)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger.Info($"[AutostartService] Autostart disabled: {path} removed.");
                }
                return true;
            }

            string? exec = BuildExec(logger);
            if (exec == null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Rewritten whole on every enable, which also drops a "disabled" flag left by the desktop's startup settings.
            File.WriteAllText(path, BuildEntry(exec));
            logger.Info($"[AutostartService] Autostart enabled: {path} -> {exec}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"[AutostartService] Failed to {(enable ? "enable" : "disable")} autostart ({path}).", ex);
            return false;
        }
    }

    /// <summary>Same job as the Windows reconcile: the app may have been moved or extracted elsewhere since the entry was written.</summary>
    public static void Reconcile(bool shouldBeEnabled, IAppLogger logger)
    {
        string path = FilePath;
        try
        {
            bool exists = File.Exists(path);

            if (!shouldBeEnabled)
            {
                if (exists)
                {
                    logger.Info("[AutostartService] Setting is off but the autostart entry was still present; removing it.");
                    SetEnabled(false, logger);
                }
                return;
            }

            string? expected = BuildExec(logger);
            if (expected == null) return;

            if (!exists)
            {
                logger.Warning("[AutostartService] Setting is on but the autostart entry was missing; recreating it.");
                SetEnabled(true, logger);
                return;
            }

            string[] lines = File.ReadAllLines(path);
            string? current = LinuxDesktopFile.ReadKey(lines, "Exec");
            if (current != expected)
            {
                logger.Warning($"[AutostartService] Autostart entry is stale. Was: {current} — now: {expected}. Rewriting it.");
                SetEnabled(true, logger);
            }
            else if (IsDisabledByDesktop(lines))
            {
                // Like Task Manager on Windows: the user's choice, not overruled at startup.
                logger.Warning($"[AutostartService] The autostart entry is correct but the desktop has it disabled ({path}). Re-enable it in the desktop's startup settings, or toggle the setting off and on again.");
            }
        }
        catch (Exception ex)
        {
            logger.Error("[AutostartService] Failed to reconcile the autostart entry.", ex);
        }
    }

    private static string BuildEntry(string exec)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Apploggd\n");
        sb.Append("Comment=Logs the games you play to Backloggd\n");
        sb.Append($"Exec={exec}\n");
        sb.Append("Terminal=false\n");
        sb.Append("X-GNOME-Autostart-enabled=true\n");
        return sb.ToString();
    }

    private static string? BuildExec(IAppLogger logger)
    {
        string? path = LinuxDesktopFile.ResolveExecutable(logger, "[AutostartService]");
        return path == null ? null : $"{LinuxDesktopFile.QuoteExecArgument(path)} {AutostartService.StartupArgument}";
    }

    /// <summary>GNOME Tweaks writes X-GNOME-Autostart-enabled=false; the spec's Hidden=true means "treat as deleted".</summary>
    private static bool IsDisabledByDesktop(string[] lines)
    {
        return string.Equals(LinuxDesktopFile.ReadKey(lines, "X-GNOME-Autostart-enabled"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(LinuxDesktopFile.ReadKey(lines, "Hidden"), "true", StringComparison.OrdinalIgnoreCase);
    }
}
