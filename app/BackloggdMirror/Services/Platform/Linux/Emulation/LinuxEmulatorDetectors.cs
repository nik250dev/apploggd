using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Services.Emulation;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// The emulator tier on Linux. The Windows detectors read the emulators' memory, which most distros forbid
/// (Yama's ptrace_scope 1: only an ancestor may read a process), so these rely on what /proc shows any
/// process of the same user: the libraries mapped and the state of each mapping, the files open, and
/// shared memory reopened through /proc/&lt;pid&gt;/fd. Window titles are no help either: under Wayland
/// other apps' windows cannot be read.
/// </summary>
internal static class LinuxEmulatorDetectors
{
    public static IEmulatorDetector[] Create(EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
    {
        var processes = new LinuxEmulatorProcesses();
        return new IEmulatorDetector[]
        {
            new LinuxRetroArchDetector(processes, resolver, logger, blacklist),
            new LinuxDolphinDetector(processes, resolver, logger, blacklist),
            new LinuxCemuDetector(processes, resolver, logger, blacklist),
            new LinuxPpssppDetector(processes, resolver, logger, blacklist),
            new LinuxDuckStationDetector(processes, resolver, logger, blacklist),
            new LinuxPcsx2Detector(processes, resolver, logger, blacklist)
        };
    }
}

/// <summary>The user's emulator processes, found by their kernel name. One /proc scan serves every detector of a pass.</summary>
internal sealed class LinuxEmulatorProcesses
{
    // Passes are seconds apart, so a scan is only ever shared within one.
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromSeconds(1);

    // The kernel cuts names to 15 characters: "dolphin-emu-nogui" is "dolphin-emu-nog".
    private static readonly HashSet<string> EmulatorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "retroarch", "dolphin-emu", "dolphin-emu-nog", "cemu", "PPSSPPSDL", "PPSSPPQt", "duckstation-qt", "duckstation-nog", "pcsx2-qt"
    };

    // An AppImage runs its binary through a link named AppRun, which becomes the kernel name.
    private const string AppImageEntryName = "AppRun";

    private readonly object _lock = new();
    private List<(int Pid, string Name)> _found = new();
    private DateTime _scannedAtUtc = DateTime.MinValue;

    public List<int> Find(params string[] names)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _scannedAtUtc > ScanLifetime)
            {
                _found = Scan();
                _scannedAtUtc = now;
            }

            return _found.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Pid).ToList();
        }
    }

    private static List<(int, string)> Scan()
    {
        var found = new List<(int, string)>();
        string? uid = LinuxProcFs.ReadUid(Environment.ProcessId);
        if (uid == null)
            return found;

        foreach (int pid in LinuxProcFs.ListProcessIds())
        {
            string? name = NameOf(pid);
            if (name == null || !EmulatorNames.Contains(name))
                continue;

            // After the name, so only the emulators pay for it.
            if (LinuxProcFs.ReadUid(pid) == uid)
                found.Add((pid, name));
        }

        return found;
    }

    /// <summary>PIDs get reused, so a live PID is not on its own proof that it is still the emulator.</summary>
    public static bool IsStillRunning(int pid, params string[] names) =>
        LinuxProcFs.IsRunning(pid) && NameOf(pid) is { } name && names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the process is an emulator this tier handles, so the window tier leaves it alone.</summary>
    public static bool IsEmulator(int pid) => NameOf(pid) is { } name && EmulatorNames.Contains(name);

    /// <summary>The kernel name, or for an AppImage the name of the binary it runs, cut the same way.</summary>
    private static string? NameOf(int pid)
    {
        string? comm = LinuxProcFs.ReadComm(pid);
        if (comm != AppImageEntryName)
            return comm;

        try
        {
            string? exe = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (exe == null)
                return comm;

            string name = LinuxProcFs.FileNameOf(exe.Replace(" (deleted)", ""));
            return name.Length > 15 ? name[..15] : name;
        }
        catch
        {
            return comm;
        }
    }

    /// <summary>Without extension, as the blacklist compares it: "retroarch", "Cemu".</summary>
    public static string ExecutableName(int pid)
    {
        string? path = LinuxProcFs.TryGetImagePath(pid);
        return path == null ? string.Empty : Path.GetFileNameWithoutExtension(LinuxProcFs.FileNameOf(path));
    }

    /// <summary>The folder of the executable, as the process sees it.</summary>
    public static string? ExecutableDir(int pid)
    {
        try
        {
            string? exe = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            return exe == null ? null : Path.GetDirectoryName(exe.Replace(" (deleted)", ""));
        }
        catch
        {
            return null;
        }
    }

    public static string? WorkingDir(int pid)
    {
        try
        {
            return new FileInfo($"/proc/{pid}/cwd").LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    public static string? EnvironmentVariable(int pid, string name)
    {
        try
        {
            string? value = LinuxProcFs.ReadEnvironmentVariable(pid, name);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A path the process named, made readable from here. A Flatpak runs in its own mount namespace, where
    /// /app is its install and most of the host is missing, so its paths are read through its root.
    /// </summary>
    public static string? Readable(int pid, string? path) =>
        path != null && path.StartsWith('/') ? $"/proc/{pid}/root{path}" : null;

    /// <summary>The first open file with one of <paramref name="extensions"/>, a game image in practice.</summary>
    public static string? FindOpenFile(IEnumerable<(int Fd, string Target)> files, IReadOnlySet<string> extensions) =>
        files.Select(f => f.Target)
            .FirstOrDefault(target => target.StartsWith('/') && !target.EndsWith(" (deleted)", StringComparison.Ordinal)
                                      && extensions.Contains(Path.GetExtension(target)));
}

/// <summary>
/// The rule the Windows detectors follow: a session opens once the same content is read twice in a row
/// and closes once it is missed twice, so one unlucky reading can do neither.
/// </summary>
internal sealed class ReadingDebounce
{
    private const int RequiredReadings = 2;

    private sealed class State
    {
        public string? Last;
        public int Same;
        public int Misses;
    }

    private readonly Dictionary<int, State> _states = new();

    /// <summary>True once <paramref name="key"/> has been read twice in a row.</summary>
    public bool Observe(int pid, string? key)
    {
        var state = StateFor(pid);

        if (string.Equals(state.Last, key, StringComparison.Ordinal))
        {
            state.Same++;
        }
        else
        {
            state.Last = key;
            state.Same = 1;
        }

        return key != null && state.Same >= RequiredReadings;
    }

    /// <summary>Whether a running session holds after this reading.</summary>
    public bool StillHolds(int pid, bool matched)
    {
        var state = StateFor(pid);

        if (matched)
        {
            state.Misses = 0;
            return true;
        }

        state.Misses++;
        return state.Misses < RequiredReadings;
    }

    public void Forget(int pid) => _states.Remove(pid);

    public void KeepOnly(ICollection<int> alive)
    {
        foreach (int pid in _states.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _states.Remove(pid);
    }

    private State StateFor(int pid)
    {
        if (!_states.TryGetValue(pid, out var state))
        {
            state = new State();
            _states[pid] = state;
        }

        return state;
    }
}
