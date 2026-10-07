using System;

namespace BackloggdMirror.Models;

public enum TrayNoticeKind { Intro, Detecting, Playing, Paused, PendingSession, Update }

/// <summary>Content of a notice shown next to the tray while the main window is hidden.</summary>
public sealed record TrayNotice(TrayNoticeKind Kind, string Title, string Body)
{
    public string Kicker { get; init; } = string.Empty;

    /// <summary>Re-read on every tick, for a kicker that changes while it is on screen (the play time).</summary>
    public Func<string>? LiveKicker { get; init; }

    public string? Version { get; init; }
    public string? ActionText { get; init; }
    public Action? Action { get; init; }

    /// <summary>Runs only when the user closes the notice with its X.</summary>
    public Action? Dismissed { get; init; }

    /// <summary>Null keeps the notice up until it is dismissed.</summary>
    public TimeSpan? Duration => Kind switch
    {
        TrayNoticeKind.Update => null,
        TrayNoticeKind.Intro or TrayNoticeKind.PendingSession => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(6)
    };
}
