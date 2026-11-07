namespace BackloggdMirror.Models;

/// <summary>Which detection tier produced a hit, since the session lifecycle differs per tier.</summary>
public enum DetectionSource
{
    Executable,
    Emulator,
    Window,
    SteamApp
}

public static class DetectionSourceExtensions
{
    /// <summary>The tier as numbered in <see cref="Services.GameDetectionService"/>, for the log.</summary>
    public static string TierLabel(this DetectionSource source) => source switch
    {
        DetectionSource.Executable => "tier 1 (executable name)",
        DetectionSource.SteamApp => "tier 1.2 (Steam app id)",
        DetectionSource.Emulator => "tier 1.5 (emulator)",
        DetectionSource.Window => "tier 2 (window heuristic)",
        _ => source.ToString()
    };
}

/// <summary>
/// A game detected as running. A null <see cref="IdIgdb"/> means detected but not identified.
/// <see cref="ExecutablePath"/> is read while the process is alive: once the session ends, its PID
/// may already belong to something else.
/// </summary>
public sealed record DetectedGame(
    string Name,
    uint ProcessId,
    string? IdIgdb,
    DetectionSource Source,
    string? ContentKey = null,
    string? PlatformKey = null,
    string? EmulatorName = null,
    string? ExecutablePath = null);
