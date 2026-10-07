using System;

namespace BackloggdMirror.Models;

/// <summary>A finished session the user left to review later. Serialized as-is to pending_sessions.json.</summary>
public sealed class PendingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The Backloggd account it was played under; other accounts never see it.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The title it will be registered as, or the detected name while unidentified.</summary>
    public string GameName { get; set; } = string.Empty;

    public string? IdIgdb { get; set; }

    /// <summary>Backloggd slug or link, as the confirmation modal would have passed it to RegisterGame.</summary>
    public string? GameUrl { get; set; }

    public string? CoverUrl { get; set; }

    public bool IsIdentified { get; set; }

    /// <summary>True when it got here after Backloggd refused to save it.</summary>
    public bool SaveFailed { get; set; }

    /// <summary>Backloggd's "Started" and "Finished" marks for the play date.</summary>
    public bool MarkedStarted { get; set; }
    public bool MarkedFinished { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public TimeSpan Duration { get; set; }
}
