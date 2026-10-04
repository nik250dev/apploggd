using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.DuckStation;

/// <summary>
/// DuckStation's detector. A session is bound to the pair (PID, serial), the serial being the one
/// DuckStation itself settled on for the disc and reads from its memory; the discs of one game share
/// the serial of the first, so a disc change keeps the session. When the memory cannot be read
/// (DuckStation elevated, or a build that keeps its state some other way) the window title stands in.
/// Opening and closing need the same reading twice, as with RetroArch.
/// </summary>
internal sealed class DuckStationDetector : IEmulatorDetector
{
    // duckstation-qt-x64-ReleaseLTCG, duckstation-qt-ARM64-ReleaseLTCG...
    internal const string ProcessNamePrefix = "duckstation";
    private const int RequiredReadings = 2;

    /// <param name="Key">The session key: the first serial of the disc set, the serial, the path for discs without one, or the window title.</param>
    private sealed record DuckStationGame(string Key, string? Serial, string? Path, string Title);

    private sealed class PidState
    {
        public DuckStationMemoryLocation Memory = new();
        public bool DirectoryRead;
        public string? Directory;
        public string? LastKey;
        public int SameCount;
        public int MissCount;
        public bool WindowWarningLogged;
        public bool FoundLogged;
    }

    public string Name => "DuckStation";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;

    public DuckStationDetector(EmulatedGameResolver resolver, IAppLogger? logger = null, IDetectionBlacklist? blacklist = null)
    {
        _resolver = resolver;
        _logger = logger;
        _blacklist = blacklist;
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
                if (_blacklist?.IsApplicationBlocked(process.Id, process.ProcessName) == true)
                    continue;

                var game = ReadGame(process);

                if (!Observe(process.Id, game?.Key) || game == null)
                    continue;

                if (_blacklist?.IsContentBlocked(Name, game.Key) == true)
                    continue;

                return Identify(process, game);
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
        Process process;
        try
        {
            process = Process.GetProcessById((int)game.ProcessId);
            if (process.HasExited || !IsDuckStation(process))
            {
                process.Dispose();
                return Closed(game);
            }
        }
        catch
        {
            return Closed(game);
        }

        DuckStationGame? running;
        using (process)
        {
            running = ReadGame(process);
        }

        var state = StateFor((int)game.ProcessId);

        if (running != null && string.Equals(running.Key, game.ContentKey, StringComparison.Ordinal))
        {
            state.MissCount = 0;
            return true;
        }

        state.MissCount++;
        if (state.MissCount < RequiredReadings)
            return true;

        _logger?.Info($"[DuckStationDetector] Game '{game.ContentKey}' is no longer running in DuckStation (PID {game.ProcessId}). Ending the session.");
        return Closed(game);
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsDuckStation(Process process)
    {
        try
        {
            return process.ProcessName.StartsWith(ProcessNamePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Keeps where the memory and the folder are: they last as long as the process, which may run another game.</summary>
    private bool Closed(DetectedGame game)
    {
        if (_states.TryGetValue((int)game.ProcessId, out var state))
        {
            state.LastKey = null;
            state.SameCount = 0;
            state.MissCount = 0;
        }

        return false;
    }

    /// <summary>Null while DuckStation sits in its game list or runs the BIOS alone.</summary>
    private DuckStationGame? ReadGame(Process process)
    {
        var state = StateFor(process.Id);

        try
        {
            bool scanned = state.Memory.Address == 0;
            var stopwatch = Stopwatch.StartNew();
            var running = DuckStationMemory.Read(process, state.Memory);

            if (running != null)
            {
                if (scanned && !state.FoundLogged)
                {
                    state.FoundLogged = true;
                    _logger?.Info($"[DuckStationDetector] Found the running game in the memory of DuckStation (PID {process.Id}) in {stopwatch.ElapsedMilliseconds} ms.");
                }

                return FromMemory(state, process.Id, running);
            }

            // Found once and empty now: no game. Never found, while the window shows one: a build this does not know.
            if (state.Memory.Address != 0)
                return null;

            return FromWindow(state, process.Id,
                "The window shows a game, but its serial was not found in the memory of DuckStation");
        }
        catch (DuckStationAccessDeniedException ex)
        {
            return FromWindow(state, process.Id, ex.Message.TrimEnd('.'));
        }
    }

    private DuckStationGame FromMemory(PidState state, int processId, DuckStationRunningGame running)
    {
        string key = running.Path;
        if (running.Serial.Length > 0)
            key = DuckStationGameDatabase.FindDiscSet(DirectoryOf(state, processId), running.Serial)?.Key ?? running.Serial;

        return new DuckStationGame(key, running.Serial.Length > 0 ? running.Serial : null, running.Path, running.Title);
    }

    private DuckStationGame? FromWindow(PidState state, int processId, string reason)
    {
        string? title = DuckStationWindows.FindGameTitle(processId);
        if (title == null)
            return null;

        if (!state.WindowWarningLogged)
        {
            state.WindowWarningLogged = true;
            _logger?.Warning($"[DuckStationDetector] {reason}. Relying on the window title of DuckStation (PID {processId}), " +
                             "which loses the game when it renders to a separate window that is not fullscreen, and splits sessions on disc changes.");
        }

        return new DuckStationGame(title, null, null, title);
    }

    private static string? DirectoryOf(PidState state, int processId)
    {
        if (!state.DirectoryRead)
        {
            state.DirectoryRead = true;
            string? path = ProcessImagePath.TryGet(processId);
            state.Directory = path != null ? Path.GetDirectoryName(path) : null;
        }

        return state.Directory;
    }

    private DetectedGame Identify(Process process, DuckStationGame game)
    {
        var state = StateFor(process.Id);
        string? setName = null;
        string? databaseName = null;

        if (game.Serial != null)
        {
            string? directory = DirectoryOf(state, process.Id);
            setName = DuckStationGameDatabase.FindDiscSet(directory, game.Serial)?.Name;
            databaseName = DuckStationGameDatabase.FindEntry(directory, game.Serial)?.Name;
        }

        // The title may be the Japanese one when DuckStation shows localized titles; the database name is always in Latin script.
        string? title = RomNameCleaner.IsLatinScript(game.Title) || (setName == null && databaseName == null) ? game.Title : null;

        var names = RomNameCleaner.Clean(game.Path ?? string.Empty, null, setName, databaseName, title);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(game.Key, null);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("psx") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"serial: {game.Serial ?? "none"}, names: {string.Join(" | ", names.Names)}, file: {game.Path ?? "none"}";
        Console.WriteLine($"[DuckStationDetector] Game '{game.Key}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[DuckStationDetector] DuckStation (PID {process.Id}) is running '{name}' with key '{game.Key}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)process.Id, idIgdb, DetectionSource.Emulator, game.Key, platforms.FirstOrDefault()?.Key, Name);
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

    private bool Observe(int processId, string? key)
    {
        var state = StateFor(processId);

        if (string.Equals(state.LastKey, key, StringComparison.Ordinal))
        {
            state.SameCount++;
        }
        else
        {
            state.LastKey = key;
            state.SameCount = 1;
        }

        return key != null && state.SameCount >= RequiredReadings;
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

            var mine = new List<Process>();

            foreach (var process in Process.GetProcesses())
            {
                bool keep;
                try { keep = IsDuckStation(process) && process.SessionId == sessionId; }
                catch { keep = false; }

                if (keep)
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
}
