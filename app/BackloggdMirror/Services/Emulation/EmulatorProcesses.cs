using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BackloggdMirror.Services.Emulation;

/// <summary>
/// The user's processes, listed once per pass for every emulator detector. Listing them costs ~7 ms, and
/// Process.GetProcessesByName lists them all too, so each detector listing its own paid that five times.
/// The pass owns what it listed: a detector must not dispose it or keep it past the pass.
/// </summary>
internal sealed class EmulatorProcesses
{
    private Process[]? _listed;

    public Process[] Find(Func<string, bool> isEmulator)
    {
        _listed ??= ListOwn();

        var found = new List<Process>();
        foreach (var process in _listed)
        {
            try
            {
                if (isEmulator(process.ProcessName))
                    found.Add(process);
            }
            catch
            {
                // The process exited mid-iteration.
            }
        }

        return found.ToArray();
    }

    /// <summary>Called by <see cref="EmulatorDetector"/> once every detector is done with the pass.</summary>
    public void EndPass()
    {
        if (_listed == null)
            return;

        foreach (var process in _listed)
        {
            try { process.Dispose(); } catch { }
        }

        _listed = null;
    }

    private static Process[] ListOwn()
    {
        try
        {
            int sessionId;
            using (var current = Process.GetCurrentProcess())
            {
                sessionId = current.SessionId;
            }

            var mine = new List<Process>();

            foreach (var process in Process.GetProcesses())
            {
                bool sameSession;
                try { sameSession = process.SessionId == sessionId; }
                catch { sameSession = false; }

                if (sameSession)
                    mine.Add(process);
                else
                    try { process.Dispose(); } catch { }
            }

            return mine.ToArray();
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }
}
