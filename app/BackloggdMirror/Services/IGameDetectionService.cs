using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

public interface IGameDetectionService
{
    /// <summary>
    /// Polled every three seconds while no session is open. Returns null when nothing is running.
    /// </summary>
    DetectedGame? Detect();

    /// <summary>
    /// Polled every three seconds while a session is open. For an emulated game this is more than a
    /// liveness check: the emulator stays open when the content is closed or swapped.
    /// </summary>
    bool IsStillRunning(DetectedGame game);

    /// <summary>Rebuilds the in-memory indexes after the games database is updated on disk.</summary>
    void ReloadDatabase();
}
