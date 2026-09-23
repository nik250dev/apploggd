using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.Cemu;

/// <summary>
/// Cemu's detector. A session is bound to the pair (PID, title ID). The window title names the game
/// but outlives it, and the emulated memory proves a game is loaded but not which, so each reading
/// needs both. Opening and closing need the same reading twice, as with RetroArch.
/// </summary>
internal sealed class CemuDetector : IEmulatorDetector
{
    private const string ProcessName = "cemu";
    private const int RequiredReadings = 2;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wud", ".wux", ".wua", ".wuhb", ".rpx", ".elf"
    };

    private sealed class PidState
    {
        public long MemoryBase;
        public string? LastTitleId;
        public int SameCount;
        public int MissCount;
        public bool DegradedWarningLogged;
        public bool CostLogged;
    }

    public string Name => "Cemu";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;

    public CemuDetector(EmulatedGameResolver resolver, IAppLogger? logger = null)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public DetectedGame? Detect()
    {
        var processes = GetProcesses();
        if (processes.Length == 0)
        {
            _states.Clear();
            return null;
        }

        try
        {
            PruneStates(processes);

            foreach (var process in processes)
            {
                var title = ReadTitle(process.Id);

                if (!Observe(process.Id, title?.TitleId) || title == null)
                    continue;

                return Identify(process, title);
            }
        }
        finally
        {
            Release(processes);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        try
        {
            using var process = Process.GetProcessById((int)game.ProcessId);
            if (process.HasExited || !IsCemu(process))
                return Closed(game);
        }
        catch
        {
            return Closed(game);
        }

        var title = ReadTitle((int)game.ProcessId);
        var state = StateFor((int)game.ProcessId);

        if (title != null && string.Equals(title.TitleId, game.ContentKey, StringComparison.Ordinal))
        {
            state.MissCount = 0;
            return true;
        }

        state.MissCount++;
        if (state.MissCount < RequiredReadings)
            return true;

        _logger?.Info($"[CemuDetector] Title '{game.ContentKey}' is no longer running in Cemu (PID {game.ProcessId}). Ending the session.");
        return Closed(game);
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsCemu(Process process)
    {
        try
        {
            return string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool Closed(DetectedGame game)
    {
        _states.Remove((int)game.ProcessId);
        return false;
    }

    /// <summary>Null when Cemu sits in its game list, is loading, or has stopped the game.</summary>
    private CemuTitle? ReadTitle(int processId)
    {
        var state = StateFor(processId);

        bool walked = state.MemoryBase == 0;
        var stopwatch = Stopwatch.StartNew();
        bool? loaded = CemuMemory.IsGameLoaded(processId, ref state.MemoryBase);

        if (loaded != null && walked && !state.CostLogged)
        {
            state.CostLogged = true;
            _logger?.Info($"[CemuDetector] Found the emulated memory of Cemu (PID {processId}) in {stopwatch.ElapsedMilliseconds} ms.");
        }

        if (loaded == false)
            return null;

        var title = CemuWindowTitles.Find(processId);

        if (title != null && loaded == null && !state.DegradedWarningLogged)
        {
            state.DegradedWarningLogged = true;
            _logger?.Warning($"[CemuDetector] Could not find the emulated memory of Cemu (PID {processId}). Relying on the window title alone, which Cemu does not reset on \"Stop emulation\": the session only ends when Cemu closes or loads another game.");
        }

        return title;
    }

    private DetectedGame Identify(Process process, CemuTitle title)
    {
        string? cemuDir = Path.GetDirectoryName(QueryImagePath(process.Id) ?? string.Empty);
        var labels = CemuTitleNames.Find(cemuDir, title.TitleId, title.Name);
        string? imagePath = ProcessOpenFiles.FindFirst(process.Id, ImageExtensions);

        var names = RomNameCleaner.Clean(imagePath ?? string.Empty, null, labels.ToArray());
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(title.TitleId, null);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("wiiu") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"names: {string.Join(" | ", names.Names)}, file: {imagePath ?? "none"}";
        Console.WriteLine($"[CemuDetector] Title '{title.TitleId}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[CemuDetector] Cemu (PID {process.Id}) is running '{name}' with title ID '{title.TitleId}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)process.Id, idIgdb, DetectionSource.Emulator, title.TitleId, platforms.FirstOrDefault()?.Key, Name);
    }

    private PidState StateFor(int processId)
    {
        if (!_states.TryGetValue(processId, out var state))
        {
            state = new PidState();
            _states[processId] = state;
        }

        return state;
    }

    private bool Observe(int processId, string? titleId)
    {
        var state = StateFor(processId);

        if (string.Equals(state.LastTitleId, titleId, StringComparison.Ordinal))
        {
            state.SameCount++;
        }
        else
        {
            state.LastTitleId = titleId;
            state.SameCount = 1;
        }

        return titleId != null && state.SameCount >= RequiredReadings;
    }

    private void PruneStates(Process[] processes)
    {
        var alive = new HashSet<int>(processes.Select(p => p.Id));

        foreach (int processId in _states.Keys.Where(id => !alive.Contains(id)).ToList())
            _states.Remove(processId);
    }

    private static Process[] GetProcesses()
    {
        try
        {
            int sessionId;
            using (var current = Process.GetCurrentProcess())
            {
                sessionId = current.SessionId;
            }

            var found = Process.GetProcessesByName(ProcessName);
            var mine = new List<Process>();

            foreach (var process in found)
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

    private static void Release(Process[] processes)
    {
        foreach (var process in processes)
        {
            try { process.Dispose(); } catch { }
        }
    }

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION crosses integrity levels, so this works for an elevated Cemu too.</summary>
    private static string? QueryImagePath(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder buffer, ref uint size);
}
