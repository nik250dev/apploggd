namespace BackloggdMirror.Models;

/// <summary>Which detection tier produced a hit, since the session lifecycle differs per tier.</summary>
public enum DetectionSource
{
    Executable,
    Emulator,
    Window
}

/// <summary>A game detected as running. A null <see cref="IdIgdb"/> means detected but not identified.</summary>
public sealed record DetectedGame(
    string Name,
    uint ProcessId,
    string? IdIgdb,
    DetectionSource Source,
    string? ContentKey = null,
    string? PlatformKey = null,
    string? EmulatorName = null);
