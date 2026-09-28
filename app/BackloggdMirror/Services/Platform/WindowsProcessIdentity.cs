using System;
using System.Diagnostics;

namespace BackloggdMirror.Services.Platform;

/// <summary>
/// Tier-1 process reading on Windows: the process name is the executable's file name without ".exe",
/// and the path comes from the main module.
/// </summary>
internal sealed class WindowsProcessIdentity : IProcessIdentity
{
    private int _sessionId;

    public bool CanRun(string os) => string.Equals(os, "win32", StringComparison.OrdinalIgnoreCase);

    public bool BeginScan()
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            _sessionId = currentProcess.SessionId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetExecutable(Process process, out string fileName, out string name)
    {
        name = process.ProcessName;
        fileName = name + ".exe";
        return true;
    }

    // Only this desktop session: services and other users' processes cannot be the game this user
    // is playing, and reading them tends to be denied anyway.
    public bool IsOwnProcess(Process process) => process.SessionId == _sessionId;

    // Denied for elevated or protected processes; the caller then leaves the candidate unconfirmed.
    public string? PathOf(Process process) => process.MainModule?.FileName;

    public bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            // GetProcessById throws once the PID is gone, which is the normal way a session ends.
            return false;
        }
    }
}
