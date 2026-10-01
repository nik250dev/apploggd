using System;
using System.Diagnostics;
using System.IO;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// Tier-1 process reading on Linux. Most games there are Windows executables under Proton/Wine,
/// which the process name cannot identify: the kernel truncates it to 15 characters and
/// /proc/&lt;pid&gt;/exe points at the Wine loader. Wine does set argv[0] to the Windows path of the
/// .exe, so that is what names every process here, native games included.
/// </summary>
internal sealed class LinuxProcessIdentity : IProcessIdentity
{
    private string? _uid;

    // Proton runs the Windows builds; the database also lists a few native Linux ones.
    public bool CanRun(string os) =>
        string.Equals(os, "win32", StringComparison.OrdinalIgnoreCase)
        || string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase);

    public bool BeginScan()
    {
        _uid = LinuxProcFs.ReadUid(Environment.ProcessId);
        return _uid != null;
    }

    public bool TryGetExecutable(Process process, out string fileName, out string name)
    {
        string? argv0 = LinuxProcFs.ReadArgv0(process.Id);
        fileName = argv0 == null ? string.Empty : LinuxProcFs.FileNameOf(argv0);
        name = Path.GetFileNameWithoutExtension(fileName);
        return fileName.Length > 0;
    }

    // The user rather than the session: a Linux session is per terminal, and Steam starts games in its own.
    public bool IsOwnProcess(Process process) => _uid != null && LinuxProcFs.ReadUid(process.Id) == _uid;

    public string? PathOf(Process process) => LinuxProcFs.TryGetImagePath(process.Id);

    // .NET counts a zombie as running, and a game stays one until its parent reaps it.
    public bool IsAlive(int processId) => LinuxProcFs.IsRunning(processId);
}
