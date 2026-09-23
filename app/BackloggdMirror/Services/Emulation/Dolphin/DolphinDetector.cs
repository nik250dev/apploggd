using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.Dolphin;

/// <summary>
/// Dolphin's detector. A session is bound to the pair (PID, game ID): the file name says little,
/// while the ID in the emulated RAM names the game and its console, and changes disc 2 of the same
/// game into nothing. Opening and closing need the same reading twice, as with RetroArch.
/// </summary>
internal sealed class DolphinDetector : IEmulatorDetector
{
    private const string ProcessName = "dolphin";
    private const int RequiredReadings = 2;

    // GameTDB prefixes of GameCube discs (retail, demo, promotional, Game Boy Player).
    private const string GameCubeIdPrefixes = "GDPU";

    private sealed class PidState
    {
        public long MemoryBase;
        public string? LastGameId;
        public int SameCount;
        public int MissCount;
        public bool DegradedWarningLogged;
        public bool CostLogged;
    }

    public string Name => "Dolphin";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;

    public DolphinDetector(EmulatedGameResolver resolver, IAppLogger? logger = null)
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
                var disc = ReadDisc(process.Id);

                if (!Observe(process.Id, disc?.GameId) || disc == null)
                    continue;

                return Identify(process, disc);
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
            if (process.HasExited || !IsDolphin(process))
                return Closed(game);
        }
        catch
        {
            return Closed(game);
        }

        var disc = ReadDisc((int)game.ProcessId);
        var state = StateFor((int)game.ProcessId);

        if (disc != null && string.Equals(disc.GameId, game.ContentKey, StringComparison.Ordinal))
        {
            state.MissCount = 0;
            return true;
        }

        state.MissCount++;
        if (state.MissCount < RequiredReadings)
            return true;

        _logger?.Info($"[DolphinDetector] Game '{game.ContentKey}' is no longer running in Dolphin (PID {game.ProcessId}). Ending the session.");
        return Closed(game);
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsDolphin(Process process)
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

    /// <summary>Null when Dolphin sits in its game list.</summary>
    private DolphinDisc? ReadDisc(int processId)
    {
        var state = StateFor(processId);

        try
        {
            bool walked = state.MemoryBase == 0;
            var stopwatch = Stopwatch.StartNew();
            var disc = DolphinMemoryReader.Read(processId, ref state.MemoryBase);

            if (disc != null && walked && !state.CostLogged)
            {
                state.CostLogged = true;
                _logger?.Info($"[DolphinDetector] Found the emulated RAM of Dolphin (PID {processId}) in {stopwatch.ElapsedMilliseconds} ms.");
            }

            return disc;
        }
        catch (DolphinAccessDeniedException ex)
        {
            if (!state.DegradedWarningLogged)
            {
                state.DegradedWarningLogged = true;
                _logger?.Warning($"[DolphinDetector] {ex.Message} Falling back to the window title, which only names the game while Dolphin's \"Show Active Title in Window Title\" setting is on.");
            }

            return DolphinWindowTitles.FindDisc(processId);
        }
    }

    private DetectedGame Identify(Process process, DolphinDisc disc)
    {
        string? dolphinDir = Path.GetDirectoryName(QueryImagePath(process.Id) ?? string.Empty);
        string? titleName = DolphinTitleDatabase.FindName(dolphinDir, disc.GameId);
        string? discPath = titleName == null ? DolphinOpenFiles.FindDiscImage(process.Id) : null;

        var names = RomNameCleaner.Clean(discPath ?? string.Empty, null, titleName);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(disc.GameId, null);

        var platforms = PlatformsFor(disc);
        string? idIgdb = _resolver.Resolve(platforms, names);

        string? platformKey = platforms.Count > 0 ? platforms[0].Key : null;
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string platformLabel = string.Join(", ", platforms.Select(p => p.Key));
        string source = titleName != null ? "title database" : discPath != null ? $"file '{discPath}'" : "game ID only";
        Console.WriteLine($"[DolphinDetector] Game '{disc.GameId}' → '{name}' (platform: {platformLabel}, name from: {source}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[DolphinDetector] Dolphin (PID {process.Id}) is running '{name}' with game ID '{disc.GameId}' (platform: {platformLabel}, name from: {source}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)process.Id, idIgdb, DetectionSource.Emulator, disc.GameId, platformKey, Name);
    }

    /// <summary>From the header magic when the RAM was read; guessed from the ID prefix otherwise, keeping both.</summary>
    private static IReadOnlyList<EmulatedPlatform> PlatformsFor(DolphinDisc disc)
    {
        var gameCube = EmulatedPlatformResolver.ByKey("ngc");
        var wii = EmulatedPlatformResolver.ByKey("wii");

        var ordered = disc.IsWii switch
        {
            true => new[] { wii },
            false => new[] { gameCube },
            null => GameCubeIdPrefixes.Contains(disc.GameId[0]) ? new[] { gameCube, wii } : new[] { wii, gameCube }
        };

        return ordered.OfType<EmulatedPlatform>().ToList();
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

    private bool Observe(int processId, string? gameId)
    {
        var state = StateFor(processId);

        if (string.Equals(state.LastGameId, gameId, StringComparison.Ordinal))
        {
            state.SameCount++;
        }
        else
        {
            state.LastGameId = gameId;
            state.SameCount = 1;
        }

        return gameId != null && state.SameCount >= RequiredReadings;
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

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION crosses integrity levels, so this works for an elevated Dolphin too.</summary>
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
