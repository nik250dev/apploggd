using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace BackloggdMirror.Services.Emulation.DuckStation;

/// <summary>A gamedb entry: its own serial, the one DuckStation runs the disc under, and its English name.</summary>
internal sealed record DuckStationGameEntry(string Serial, string? Name);

/// <summary>A multi-disc game; <paramref name="Key"/> is the serial of its first disc.</summary>
internal sealed record DuckStationDiscSet(string Key, string Name);

/// <summary>
/// Serial to name and disc set, from the YAML files every DuckStation build ships in resources.
/// gamedb.yaml names each disc in English ("Resident Evil 2 - Dual Shock Ver. (Disc 1)"), also for
/// Japanese games, and discsets.yaml groups the discs of one game, so a disc change does not split
/// the session.
/// </summary>
internal static class DuckStationGameDatabase
{
    private const string ResourcesFolder = "resources";
    private const string GameDbFile = "gamedb.yaml";
    private const string DiscSetsFile = "discsets.yaml";
    private const string NameKey = "name: ";
    private const string ListItem = "- ";
    private const string EntryIndent = "  ";

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, DuckStationDiscSet>> DiscSetsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    public static DuckStationDiscSet? FindDiscSet(string? duckStationDir, string serial)
    {
        if (string.IsNullOrEmpty(duckStationDir) || serial.Length == 0)
            return null;

        string path = Path.Combine(duckStationDir, ResourcesFolder, DiscSetsFile);
        var sets = DiscSetsByFile.GetOrAdd(path, LoadDiscSets);
        return sets.TryGetValue(serial, out var set) ? set : null;
    }

    /// <summary>
    /// The entry for a serial or for a disc ID read from the disc, which an entry may list among its
    /// "codes" (alternative releases). Scanned on each call: it is ~5 MB and only needed once per disc.
    /// </summary>
    public static DuckStationGameEntry? FindEntry(string? duckStationDir, string gameId)
    {
        if (string.IsNullOrEmpty(duckStationDir) || gameId.Length == 0)
            return null;

        string path = Path.Combine(duckStationDir, ResourcesFolder, GameDbFile);
        if (!File.Exists(path))
            return null;

        string? serial = null;
        string? name = null;
        bool inCodes = false;
        bool matched = false;

        foreach (string line in ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            // A top-level key starts the next entry.
            if (!char.IsWhiteSpace(line[0]))
            {
                if (matched)
                    break;

                serial = line.TrimEnd().TrimEnd(':');
                name = null;
                inCodes = false;
                matched = serial.Equals(gameId, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            string trimmed = line.Trim();
            bool entryKey = line.Length > EntryIndent.Length && line.StartsWith(EntryIndent, StringComparison.Ordinal)
                            && !char.IsWhiteSpace(line[EntryIndent.Length]);
            if (entryKey)
            {
                inCodes = trimmed == "codes:";
                if (name == null && trimmed.StartsWith(NameKey, StringComparison.Ordinal))
                    name = Unquote(trimmed[NameKey.Length..]);
            }
            else if (inCodes && trimmed.StartsWith(ListItem, StringComparison.Ordinal)
                     && trimmed[ListItem.Length..].Trim().Equals(gameId, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
            }
        }

        return matched && serial != null ? new DuckStationGameEntry(serial, name) : null;
    }

    private static IReadOnlyDictionary<string, DuckStationDiscSet> LoadDiscSets(string path)
    {
        var sets = new Dictionary<string, DuckStationDiscSet>(StringComparer.OrdinalIgnoreCase);

        string? name = null;
        string? key = null;
        bool inSerials = false;

        foreach (string line in ReadLines(path))
        {
            if (line.StartsWith(ListItem + NameKey, StringComparison.Ordinal))
            {
                name = Unquote(line[(ListItem.Length + NameKey.Length)..]);
                key = null;
                inSerials = false;
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed == "serials:")
            {
                inSerials = true;
                continue;
            }

            if (inSerials && trimmed.StartsWith(ListItem, StringComparison.Ordinal) && name != null)
            {
                string serial = trimmed[ListItem.Length..].Trim();
                key ??= serial;
                sets.TryAdd(serial, new DuckStationDiscSet(key, name));
                continue;
            }

            if (trimmed.Length > 0)
                inSerials = false;
        }

        return sets;
    }

    /// <summary>An unreadable file reads as empty, or as far as it could be read.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        IEnumerator<string> lines;
        try
        {
            lines = File.ReadLines(path).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (lines)
        {
            while (true)
            {
                try
                {
                    if (!lines.MoveNext())
                        yield break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return lines.Current;
            }
        }
    }

    private static string? Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

        return value.Length > 0 ? value : null;
    }
}
