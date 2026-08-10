namespace BackloggdMirror.Services;

public interface IGameDetectionService
{
    /// <summary>
    /// Polled once per second. <paramref name="idIgdb"/> can be null even on a hit, which means the
    /// game was detected but not identified — the UI then requires the user to pick it by hand
    /// before the session can be saved.
    /// </summary>
    bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb);

    /// <summary>Rebuilds the in-memory indexes after the games database is updated on disk.</summary>
    void ReloadDatabase();
}
