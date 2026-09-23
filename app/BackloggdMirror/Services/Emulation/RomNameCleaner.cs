using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation;

/// <summary>Search names for a ROM, most reliable first, plus the last-resort one.</summary>
internal sealed record RomNames(IReadOnlyList<string> Names, string? WithoutSubtitle)
{
    public string Primary => Names.Count > 0 ? Names[0] : string.Empty;
}

/// <summary>
/// A ROM file name turned into searchable titles. Stripping every parenthesis and bracket is safe
/// because No-Intro, GoodTools, Redump and TOSEC put all their metadata in them.
/// </summary>
internal static class RomNameCleaner
{
    private static readonly Regex ScenePrefix = new(@"^\d{3,4}\s*[-\.]\s*", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex TrailingArticle = new(
        @"^(.*?),\s+(The|A|An|El|La|Los|Las|Le|Les|Der|Die|Das|Il|Lo)(\s+-\s+|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RomNames Clean(string archivePath, string? innerName, string? label = null)
    {
        var names = new List<string>();

        string? fromLabel = CleanOne(label);
        if (fromLabel != null)
            AddVariants(names, fromLabel);

        string fileName = Path.GetFileNameWithoutExtension(innerName ?? archivePath) ?? string.Empty;
        string? fromFile = CleanOne(fileName);
        if (fromFile != null)
            AddVariants(names, fromFile);

        // A name made only of tags ("[BIOS].zip") cleans down to nothing, and a nameless session is dropped without ever reaching the modal.
        if (names.Count == 0)
            Add(names, fileName);
        if (names.Count == 0)
            Add(names, Path.GetFileName(innerName ?? archivePath) ?? string.Empty);

        string? withoutSubtitle = null;
        string? main = names.Count > 0 ? names[0] : null;
        if (main != null)
        {
            int separator = main.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                separator = main.IndexOf(": ", StringComparison.Ordinal);

            if (separator > 0)
            {
                string head = main[..separator].Trim();
                if (head.Length >= 3 && !names.Contains(head, StringComparer.OrdinalIgnoreCase))
                    withoutSubtitle = head;
            }
        }

        return new RomNames(names, withoutSubtitle);
    }

    private static void AddVariants(List<string> names, string cleaned)
    {
        Add(names, cleaned);

        // Dumps separate subtitles with " - " where IGDB uses a colon.
        if (cleaned.Contains(" - ", StringComparison.Ordinal))
            Add(names, cleaned.Replace(" - ", ": ", StringComparison.Ordinal));
    }

    private static void Add(List<string> names, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
            names.Add(value);
    }

    private static string? CleanOne(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return null;

        string value = rawName;

        value = ScenePrefix.Replace(value, string.Empty);
        value = Tags.Replace(value, " ");
        value = value.Replace('_', ' ');
        value = Whitespace.Replace(value, " ").Trim().TrimEnd('.', '-', ' ');

        var article = TrailingArticle.Match(value);
        if (article.Success)
        {
            value = $"{article.Groups[2].Value} {article.Groups[1].Value}{article.Groups[3].Value}{value[article.Length..]}";
            value = Whitespace.Replace(value, " ").Trim();
        }

        return value.Length > 0 ? value : null;
    }
}
