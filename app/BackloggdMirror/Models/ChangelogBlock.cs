namespace BackloggdMirror.Models;

/// <summary>
/// A renderable block of changelog content. Each subtype has its own DataTemplate in
/// MainWindow.axaml, so the ItemsControl picks the look from the item's type and no converter
/// is needed.
/// </summary>
public abstract class ChangelogBlock
{
}

/// <summary>Version heading ("## 1.0.0 — 30/07/2026").</summary>
public sealed class ChangelogVersionBlock : ChangelogBlock
{
    public string Version { get; init; } = string.Empty;

    /// <summary>Text following the heading separator, empty when there is none.</summary>
    public string Date { get; init; } = string.Empty;

    public bool HasDate => !string.IsNullOrWhiteSpace(Date);
}

/// <summary>Subheading within a version ("### What's new").</summary>
public sealed class ChangelogSectionBlock : ChangelogBlock
{
    public string Title { get; init; } = string.Empty;
}

/// <summary>List item ("- ...").</summary>
public sealed class ChangelogItemBlock : ChangelogBlock
{
    public string Text { get; init; } = string.Empty;
}

/// <summary>Loose paragraph.</summary>
public sealed class ChangelogParagraphBlock : ChangelogBlock
{
    public string Text { get; init; } = string.Empty;
}
