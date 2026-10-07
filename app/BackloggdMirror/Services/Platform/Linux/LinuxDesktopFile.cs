using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>What the autostart entry and the applications-menu entry share: where they live, which executable they launch and how the Desktop Entry spec wants it written.</summary>
internal static class LinuxDesktopFile
{
    public static string ConfigHome => XdgDirectory("XDG_CONFIG_HOME", ".config");

    public static string DataHome => XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share"));

    private static string XdgDirectory(string variable, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value) || !Path.IsPathRooted(value))
        {
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
        }
        return value;
    }

    /// <summary>The executable a desktop entry should launch, or null (already logged) when there is none that would open the app.</summary>
    public static string? ResolveExecutable(IAppLogger logger, string logTag)
    {
        string? path = ResolveLauncherPath();
        if (string.IsNullOrEmpty(path))
        {
            logger.Error($"{logTag} Environment.ProcessPath is empty, so there is no path to register.");
            return null;
        }

        // Under "dotnet Apploggd.dll" the process is the shared host, which would open nothing.
        if (Path.GetFileNameWithoutExtension(path) == "dotnet")
        {
            logger.Error($"{logTag} Running through the dotnet host ({path}), not the Apploggd executable; there is nothing to register.");
            return null;
        }

        if (path.Any(char.IsControl))
        {
            logger.Error($"{logTag} The executable path contains control characters and cannot go in a desktop entry: {path}");
            return null;
        }

        return path;
    }

    /// <summary>Inside an AppImage the process runs from a mount that disappears on exit; the image itself is in APPIMAGE.</summary>
    private static string? ResolveLauncherPath()
    {
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage)) return appImage;
        return Environment.ProcessPath;
    }

    /// <summary>Desktop Entry spec quoting: Exec-level escapes, then string-level ones (a backslash becomes four), and % doubled.</summary>
    public static string QuoteExecArgument(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return EscapeString(sb.ToString()).Replace("%", "%%");
    }

    /// <summary>String-level escaping, for keys such as TryExec that are not Exec command lines.</summary>
    public static string EscapeString(string value) => value.Replace("\\", "\\\\");

    public static string? ReadKey(string[] lines, string key)
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
}
