using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Avalonia.Platform;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// The applications-menu entry and its icon. GNOME ignores the icon the window publishes over X11 and
/// draws a generic one unless a desktop entry claims the window through StartupWMClass.
/// </summary>
internal static class LinuxDesktopEntry
{
    private const string LogTag = "[DesktopEntry]";
    private const string FileName = "apploggd.desktop";
    private const string IconName = "apploggd";
    private const string IconSize = "128x128";

    // Avalonia's default WM_CLASS, and the host of the avares:// URIs.
    private static readonly string AssemblyName = Assembly.GetExecutingAssembly().GetName().Name!;

    /// <summary>Must run before the first window maps: GNOME matches a window to its entry only then.</summary>
    public static void Install(IAppLogger logger)
    {
        try
        {
            string? executable = LinuxDesktopFile.ResolveExecutable(logger, LogTag);
            if (executable == null) return;

            byte[] icon;
            using (var stream = AssetLoader.Open(new Uri($"avares://{AssemblyName}/Assets/app-logo.png")))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                icon = buffer.ToArray();
            }

            // Icon first, so the entry never shows up pointing at an icon that is not there yet.
            WriteIfChanged(Path.Combine(LinuxDesktopFile.DataHome, "icons", "hicolor", IconSize, "apps", IconName + ".png"), icon, logger);
            WriteIfChanged(Path.Combine(LinuxDesktopFile.DataHome, "applications", FileName), Encoding.UTF8.GetBytes(BuildEntry(executable)), logger);
        }
        catch (Exception ex)
        {
            logger.Error($"{LogTag} Failed to install the desktop entry and icon.", ex);
        }
    }

    private static string BuildEntry(string executable)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=Apploggd\n");
        sb.Append("Comment=Logs the games you play to Backloggd\n");
        sb.Append($"Exec={LinuxDesktopFile.QuoteExecArgument(executable)}\n");
        // Hides the entry if the app is moved or deleted, until the next start rewrites it.
        sb.Append($"TryExec={LinuxDesktopFile.EscapeString(executable)}\n");
        sb.Append($"Icon={IconName}\n");
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Game;\n");
        sb.Append($"StartupWMClass={AssemblyName}\n");
        return sb.ToString();
    }

    private static void WriteIfChanged(string path, byte[] content, IAppLogger logger)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(content)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        logger.Info($"{LogTag} Wrote {path}");
    }
}
