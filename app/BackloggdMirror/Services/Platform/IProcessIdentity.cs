using System.Diagnostics;

namespace BackloggdMirror.Services.Platform;

/// <summary>
/// How tier-1 detection reads a running process, swapped per platform: Windows and Linux expose a
/// game's executable in unrelated places, and on Linux most games are Windows executables running
/// under Proton/Wine. Everything here may throw for a process that exits mid-scan; the caller skips it.
/// </summary>
internal interface IProcessIdentity
{
    /// <summary>Whether this platform runs the database's executables for <paramref name="os"/> ("win32", "linux"...).</summary>
    bool CanRun(string os);

    /// <summary>Called once before each scan; false skips the scan.</summary>
    bool BeginScan();

    /// <param name="fileName">As the database lists it, e.g. "eldenring.exe".</param>
    /// <param name="name">What the blacklist compares: the file name without its extension.</param>
    bool TryGetExecutable(Process process, out string fileName, out string name);

    /// <summary>Whether the process belongs to the user running the app, so it can be their game.</summary>
    bool IsOwnProcess(Process process);

    /// <summary>The executable's full path, for database entries that also name its parent folders.</summary>
    string? PathOf(Process process);

    /// <summary>Whether a detected game's process is still running, which is what keeps its session open.</summary>
    bool IsAlive(int processId);
}
