using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>The Linux counterpart of the Windows Run key: a desktop entry in the XDG autostart folder, launched at login.</summary>
internal static class LinuxAutostartEntry
{
    private const string FileName = "apploggd.desktop";

    private static string FilePath
    {
        get
        {
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome))
            {
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(configHome, "autostart", FileName);
        }
    }

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
            string? current = ReadKey(lines, "Exec");
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
        string? path = ResolveLauncherPath();
        if (string.IsNullOrEmpty(path))
        {
            logger.Error("[AutostartService] Environment.ProcessPath is empty, so there is no path to register.");
            return null;
        }

        // Under "dotnet Apploggd.dll" the process is the shared host, which would open nothing at login.
        if (Path.GetFileNameWithoutExtension(path) == "dotnet")
        {
            logger.Error($"[AutostartService] Running through the dotnet host ({path}), not the Apploggd executable; there is nothing to register.");
            return null;
        }

        if (path.Any(char.IsControl))
        {
            logger.Error($"[AutostartService] The executable path contains control characters and cannot go in a desktop entry: {path}");
            return null;
        }

        return $"{QuoteExecArgument(path)} {AutostartService.StartupArgument}";
    }

    /// <summary>Inside an AppImage the process runs from a mount that disappears on exit; the image itself is in APPIMAGE.</summary>
    private static string? ResolveLauncherPath()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage)) return appImage;
        return Environment.ProcessPath;
    }

    /// <summary>Desktop Entry spec quoting: Exec-level escapes, then string-level ones (a backslash becomes four), and % doubled.</summary>
    private static string QuoteExecArgument(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    private static string? ReadKey(string[] lines, string key)
    {
        bool inMainGroup = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inMainGroup = line == "[Desktop Entry]";
                continue;
            }
            if (!inMainGroup) continue;

            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].TrimEnd() == key) return line[(eq + 1)..].TrimStart();
        }
        return null;
    }

    /// <summary>GNOME Tweaks writes X-GNOME-Autostart-enabled=false; the spec's Hidden=true means "treat as deleted".</summary>
    private static bool IsDisabledByDesktop(string[] lines)
    {
        return string.Equals(ReadKey(lines, "X-GNOME-Autostart-enabled"), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadKey(lines, "Hidden"), "true", StringComparison.OrdinalIgnoreCase);
    }
}
