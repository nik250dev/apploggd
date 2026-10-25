using System;
using System.Collections.Generic;

namespace BackloggdMirror.Models;

public enum NotePrivacy
{
    Default,
    Public,
    Friends,
    Private
}

/// <summary>The note, its privacy and the tags Backloggd keeps on each day of a log.</summary>
public sealed record PlaySessionExtras(string Note, NotePrivacy Privacy, IReadOnlyList<string> Tags)
{
    public static PlaySessionExtras Empty { get; } = new(string.Empty, NotePrivacy.Default, Array.Empty<string>());

    public bool IsEmpty => string.IsNullOrWhiteSpace(Note) && Tags.Count == 0;
}
