using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation;

/// <summary>One emulator's detector; an emulator stays open between games, so liveness is about its content.</summary>
internal interface IEmulatorDetector
{
    /// <summary>Matches <see cref="DetectedGame.EmulatorName"/>, so a session is checked by its own detector.</summary>
    string Name { get; }

    DetectedGame? Detect();

    bool IsStillRunning(DetectedGame game);
}
