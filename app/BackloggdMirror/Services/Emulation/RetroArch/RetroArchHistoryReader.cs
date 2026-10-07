using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>One entry of content_history.lpl. Everything but <c>path</c> can be empty.</summary>
internal sealed class RetroArchHistoryEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("core_path")]
    public string? CorePath { get; set; }

    [JsonPropertyName("core_name")]
    public string? CoreName { get; set; }

    [JsonPropertyName("crc32")]
    public string? Crc32 { get; set; }

    [JsonPropertyName("db_name")]
    public string? DatabaseName { get; set; }
}

internal sealed class RetroArchHistoryFile
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("items")]
    public List<RetroArchHistoryEntry> Items { get; set; } = new();
}

/// <summary>
/// RetroArch's "recently played" playlist: a complement, never a source of truth, because it says
/// what was loaded and not whether it still is. What it adds is <c>label</c> and <c>db_name</c>.
/// </summary>
internal static class RetroArchHistoryReader
{
    private static readonly object _cacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static long _cachedLength;
    private static List<RetroArchHistoryEntry> _cachedItems = new();

    public static IReadOnlyList<RetroArchHistoryEntry> Read(RetroArchProcessInfo processInfo)
    {
        string? path = processInfo.HistoryPath;
        if (string.IsNullOrEmpty(path))
            return Array.Empty<RetroArchHistoryEntry>();

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return Array.Empty<RetroArchHistoryEntry>();

            lock (_cacheLock)
            {
                if (_cachedPath == path && _cachedWriteTimeUtc == file.LastWriteTimeUtc && _cachedLength == file.Length)
                    return _cachedItems;

                var parsed = JsonSerializer.Deserialize<RetroArchHistoryFile>(File.ReadAllText(path));
                var items = parsed?.Items ?? new List<RetroArchHistoryEntry>();

                // Normalized once here, so every comparison downstream is against the same shape as
                // the paths read out of memory.
                foreach (var entry in items)
                {
                    if (!string.IsNullOrEmpty(entry.Path))
                        entry.Path = RetroArchPath.Normalize(entry.Path);
                }

                _cachedPath = path;
                _cachedWriteTimeUtc = file.LastWriteTimeUtc;
                _cachedLength = file.Length;
                _cachedItems = items;

                return _cachedItems;
            }
        }
        catch
        {
            // Very old RetroArch versions used a plain-text format, and the file is rewritten while
            // it is being read. Either way this is an optional source.
            return Array.Empty<RetroArchHistoryEntry>();
        }
    }

    public static RetroArchHistoryEntry? First(RetroArchProcessInfo processInfo)
    {
        var items = Read(processInfo);
        return items.Count > 0 ? items[0] : null;
    }

    public static RetroArchHistoryEntry? Lookup(RetroArchProcessInfo processInfo, string contentPath)
    {
        if (string.IsNullOrEmpty(contentPath))
            return null;

        string archivePath = contentPath;
        int hash = contentPath.IndexOf('#');
        if (hash > 0)
            archivePath = contentPath[..hash];

        foreach (var entry in Read(processInfo))
        {
            if (string.IsNullOrEmpty(entry.Path))
                continue;

            if (entry.Path.Equals(contentPath, StringComparison.OrdinalIgnoreCase)
                || entry.Path.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    public static DateTime? LastWriteTimeUtc(RetroArchProcessInfo processInfo)
    {
        string? path = processInfo.HistoryPath;
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.LastWriteTimeUtc : null;
        }
        catch
        {
            return null;
        }
    }
}
