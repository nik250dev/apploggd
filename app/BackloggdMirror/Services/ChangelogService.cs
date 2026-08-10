using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia.Platform;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>
/// Reads Assets\changelog.md and turns it into renderable blocks.
///
/// The parser deliberately covers only the markdown subset the file actually uses:
/// "## version — date", "### section", "- item" and loose paragraphs. The "# Changelog" heading is
/// discarded because the modal already has its own title. Inline markdown (bold, links, ...) is
/// not processed.
/// </summary>
public class ChangelogService
{
    /// <summary>
    /// The host of an avares:// URI is the *assembly* name, which here does not match the project
    /// name (&lt;AssemblyName&gt;Apploggd&lt;/AssemblyName&gt;). Derived rather than hardcoded so a
    /// rename of the assembly cannot break it.
    /// </summary>
    private static string ChangelogUri =>
        $"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/changelog.md";

    /// <summary>Separators accepted between version and date in the "##" heading.</summary>
    private static readonly char[] HeaderSeparators = { '—', '–' };

    private readonly IAppLogger? _logger;

    public ChangelogService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Application version as shown in Settings. Comes from &lt;Version&gt; in the .csproj.
    /// </summary>
    public string GetAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Some build tooling appends metadata ("1.0.0+abc1234"); keep the semantic version only.
            var plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational.Substring(0, plusIndex) : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Returns the changelog blocks, or an empty list if the resource cannot be read.
    /// </summary>
    public IReadOnlyList<ChangelogBlock> LoadBlocks()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(ChangelogUri));
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            _logger?.Error("Could not load the changelog.", ex);
            return Array.Empty<ChangelogBlock>();
        }
    }

    internal static List<ChangelogBlock> Parse(string markdown)
    {
        var blocks = new List<ChangelogBlock>();

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                blocks.Add(new ChangelogSectionBlock { Title = line.Substring(4).Trim() });
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(ParseVersionHeader(line.Substring(3).Trim()));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                // File title: the modal already shows its own.
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                blocks.Add(new ChangelogItemBlock { Text = line.Substring(2).Trim() });
            }
            else
            {
                blocks.Add(new ChangelogParagraphBlock { Text = line });
            }
        }

        return blocks;
    }

    private static ChangelogVersionBlock ParseVersionHeader(string header)
    {
        var separatorIndex = header.IndexOfAny(HeaderSeparators);

        if (separatorIndex < 0)
        {
            return new ChangelogVersionBlock { Version = header };
        }

        return new ChangelogVersionBlock
        {
            Version = header.Substring(0, separatorIndex).Trim(),
            Date = header.Substring(separatorIndex + 1).Trim()
        };
    }
}
