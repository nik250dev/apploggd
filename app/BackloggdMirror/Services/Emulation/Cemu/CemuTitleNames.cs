using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace BackloggdMirror.Services.Emulation.Cemu;

/// <summary>
/// Names for a title ID from Cemu's own files. The long name in title_list_cache.xml beats the short
/// one in the window ("Family Party" for "Family Party: 30 Great Games Obstacle Arcade"), but both
/// follow the console language. The comment atop the bundled game profiles is always English, yet
/// only a few hundred games have one.
/// </summary>
internal static class CemuTitleNames
{
    private const int EnglishConsoleLanguage = 1;

    /// <summary>Most reliable first: English names ahead of localized ones.</summary>
    public static IReadOnlyList<string> Find(string? cemuDir, string titleId, string? windowName)
    {
        string? userDir = UserDataDir(cemuDir);
        string? cacheName = userDir != null ? FromTitleCache(userDir, titleId) : null;
        string? profileName = cemuDir != null ? FromGameProfile(cemuDir, titleId) : null;

        var ordered = IsEnglishConsole(userDir)
            ? new[] { cacheName, windowName, profileName }
            : new[] { profileName, cacheName, windowName };

        return ordered.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Same order Cemu follows: a "portable" folder, then a settings.xml beside the exe (pre-2.0 installs), then %APPDATA%.</summary>
    internal static string? UserDataDir(string? cemuDir)
    {
        if (!string.IsNullOrEmpty(cemuDir))
        {
            string portable = Path.Combine(cemuDir, "portable");
            if (Directory.Exists(portable))
                return portable;

            if (File.Exists(Path.Combine(cemuDir, "settings.xml")))
                return cemuDir;
        }

        string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cemu");
        return Directory.Exists(roaming) ? roaming : null;
    }

    private static string? FromTitleCache(string userDir, string titleId)
    {
        try
        {
            string path = Path.Combine(userDir, "title_list_cache.xml");
            if (!File.Exists(path))
                return null;

            var title = XDocument.Load(path).Root?.Elements("title")
                .FirstOrDefault(t => string.Equals((string?)t.Attribute("titleId"), titleId, StringComparison.OrdinalIgnoreCase));

            string? name = title?.Element("name")?.Value.Trim();
            return string.IsNullOrEmpty(name) || name == "Unknown Title" ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The first line of gameProfiles\default\&lt;titleId&gt;.ini, like "# Mario Kart 8 (JPN)".</summary>
    private static string? FromGameProfile(string cemuDir, string titleId)
    {
        try
        {
            string path = Path.Combine(cemuDir, "gameProfiles", "default", titleId + ".ini");
            if (!File.Exists(path))
                return null;

            string? first = File.ReadLines(path).FirstOrDefault();
            if (first == null || !first.StartsWith('#'))
                return null;

            string name = first.TrimStart('#').Trim();
            return name.Length > 0 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>English unless settings.xml says otherwise; English is Cemu's default.</summary>
    private static bool IsEnglishConsole(string? userDir)
    {
        if (userDir == null)
            return true;

        try
        {
            string path = Path.Combine(userDir, "settings.xml");
            if (!File.Exists(path))
                return true;

            string? value = XDocument.Load(path).Root?.Element("console_language")?.Value;
            return !int.TryParse(value, out int language) || language == EnglishConsoleLanguage;
        }
        catch
        {
            return true;
        }
    }
}
