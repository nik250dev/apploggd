using System;
using System.IO;

namespace BackloggdMirror.Services.Emulation.Pcsx2;

/// <summary>A GameIndex entry: its name, in the language of the disc, and the English one Japanese discs add.</summary>
internal sealed record Pcsx2GameEntry(string? Name, string? EnglishName);

/// <summary>
/// Serial to name, from the GameIndex.yaml every PCSX2 build ships in resources: the database PCSX2 names
/// its games with. Windows reads the same names out of PCSX2's memory, so only Linux needs it.
/// </summary>
internal static class Pcsx2GameIndex
{
    private const string ResourcesFolder = "resources";
    private const string GameIndexFile = "GameIndex.yaml";
    private const string NameKey = "name: ";
    private const string EnglishNameKey = "name-en: ";
    private const string EntryIndent = "  ";

    /// <summary>Scanned on each call: it is ~2.6 MB and only needed once per disc.</summary>
    public static Pcsx2GameEntry? FindEntry(string? pcsx2Dir, string serial)
    {
        if (string.IsNullOrEmpty(pcsx2Dir) || serial.Length == 0)
            return null;

        string path = Path.Combine(pcsx2Dir, ResourcesFolder, GameIndexFile);
        string header = serial + ":";
        bool matched = false;
        string? name = null;
        string? englishName = null;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;

                // A top-level key starts the next entry.
                if (!char.IsWhiteSpace(line[0]))
                {
                    if (matched)
                        break;

                    matched = line.TrimEnd().Equals(header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!matched || !line.StartsWith(EntryIndent, StringComparison.Ordinal) || char.IsWhiteSpace(line[EntryIndent.Length]))
                    continue;

                string trimmed = line.Trim();
                if (trimmed.StartsWith(NameKey, StringComparison.Ordinal))
                    name = Unquote(trimmed[NameKey.Length..]);
                else if (trimmed.StartsWith(EnglishNameKey, StringComparison.Ordinal))
                    englishName = Unquote(trimmed[EnglishNameKey.Length..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return matched ? new Pcsx2GameEntry(name, englishName) : null;
    }

    private static string? Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

        return value.Length > 0 ? value : null;
    }
}
