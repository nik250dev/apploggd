using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.Pcsx2;

/// <summary>
/// PCSX2's detector. A session is bound to the pair (PID, serial), the serial being the one PCSX2 itself
/// read from the disc and reads from its memory. When the memory cannot be read (PCSX2 elevated, or a
/// build that keeps its state some other way) the window title stands in. Opening and closing need the
/// same reading twice, as with RetroArch.
/// </summary>
internal sealed class Pcsx2Detector : IEmulatorDetector
{
    // pcsx2-qt, pcsx2-qtx64-avx2 (1.7 nightlies), pcsx2x64 (1.6)...
    internal const string ProcessNamePrefix = "pcsx2";
    private const string AppName = "PCSX2";
    private const int RequiredReadings = 2;

    private static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(
        new[] { ".iso", ".bin", ".img", ".mdf", ".nrg", ".chd", ".cso", ".zso", ".gz" }, StringComparer.OrdinalIgnoreCase);

    // "PS2 BIOS (Europe)", translated: "BIOS de PS2 (Europe)".
    private static readonly Regex BiosTitle = new(@"\bBIOS\b.*\(.+\)$", RegexOptions.Compiled);

    /// <param name="Key">The session key: the serial, the boot file for discs without one, or the window title.</param>
    private sealed record Pcsx2Game(string Key, string? Serial, string? BootElf, string Title, string? EnglishTitle);

    private sealed class PidState
    {
        public Pcsx2MemoryLocation Memory = new();
        public string? LastKey;
        public int SameCount;
        public int MissCount;
        public bool WindowWarningLogged;
        public bool FoundLogged;
    }

    public string Name => "PCSX2";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;

    public Pcsx2Detector(EmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger = null, IDetectionBlacklist? blacklist = null)
    {
        _processes = processes;
        _resolver = resolver;
        _logger = logger;
        _blacklist = blacklist;
    }

    public DetectedGame? Detect()
    {
        var processes = _processes.Find(IsPcsx2);
        if (processes.Length == 0)
        {
            _states.Clear();
            return null;
        }

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

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        Process process;
        try
        {
            process = Process.GetProcessById((int)game.ProcessId);
            if (process.HasExited || !IsPcsx2(process))
            {
                process.Dispose();
                return Closed(game);
            }
        }
        catch
        {
            return Closed(game);
        }

        Pcsx2Game? running;
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

        _logger?.Info($"[Pcsx2Detector] Game '{game.ContentKey}' is no longer running in PCSX2 (PID {game.ProcessId}). Ending the session.");
        return Closed(game);
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsPcsx2(Process process)
    {
        try
        {
            return IsPcsx2(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPcsx2(string processName) =>
        processName.StartsWith(ProcessNamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps where the memory is: it lasts as long as the process, which may run another game.</summary>
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

    /// <summary>Null while PCSX2 sits in its game list or runs the BIOS alone.</summary>
    private Pcsx2Game? ReadGame(Process process)
    {
        var state = StateFor(process.Id);

        try
        {
            bool scanned = state.Memory.Address == 0;
            // Its data sections are ~200 MB, so they are only scanned while some window shows more than "PCSX2 v2.9.96".
            bool mayScan = scanned && QtEmulatorWindows.ShowsMoreThanApp(process.Id, AppName);
            var stopwatch = Stopwatch.StartNew();
            var running = Pcsx2Memory.Read(process, state.Memory, mayScan);

            if (running != null)
            {
                if (scanned && !state.FoundLogged)
                {
                    state.FoundLogged = true;
                    _logger?.Info($"[Pcsx2Detector] Found the running game in the memory of PCSX2 (PID {process.Id}) in {stopwatch.ElapsedMilliseconds} ms.");
                }

                string key = running.Serial.Length > 0 ? running.Serial : running.BootElf;
                return new Pcsx2Game(key, running.Serial.Length > 0 ? running.Serial : null, running.BootElf, running.Title,
                    running.EnglishTitle.Length > 0 ? running.EnglishTitle : null);
            }

            // Found once and empty now: no game. Never found, while the window shows one: a build this does not know.
            if (state.Memory.Address != 0 || !mayScan)
                return null;

            return FromWindow(state, process.Id,
                "The window shows a game, but its serial was not found in the memory of PCSX2");
        }
        catch (Pcsx2AccessDeniedException ex)
        {
            return FromWindow(state, process.Id, ex.Message.TrimEnd('.'));
        }
    }

    private Pcsx2Game? FromWindow(PidState state, int processId, string reason)
    {
        string? title = QtEmulatorWindows.FindGameTitle(processId, AppName);
        if (title == null || BiosTitle.IsMatch(title))
            return null;

        if (!state.WindowWarningLogged)
        {
            state.WindowWarningLogged = true;
            _logger?.Warning($"[Pcsx2Detector] {reason}. Relying on the window title of PCSX2 (PID {processId}), " +
                             "which loses the game when it renders to a separate window that is not fullscreen.");
        }

        return new Pcsx2Game(title, null, null, title, null);
    }

    private DetectedGame Identify(Process process, Pcsx2Game game)
    {
        string? imagePath = ProcessOpenFiles.FindFirst(process.Id, ImageExtensions);

        // Titles missing from its database come as "SLUS-21678 [?]"; Japanese ones may be in Japanese unless it also has the English one.
        string? title = game.Title.EndsWith(" [?]", StringComparison.Ordinal) || !RomNameCleaner.IsLatinScript(game.Title) ? null : game.Title;

        var names = RomNameCleaner.Clean(imagePath ?? string.Empty, null, game.EnglishTitle, title);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(game.Key, null);

        // PCSX2 also boots PlayStation discs, whose boot file is on "cdrom:" instead of "cdrom0:".
        string platformKey = game.BootElf != null && game.BootElf.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase) ? "psx" : "ps2";
        var platforms = new[] { EmulatedPlatformResolver.ByKey(platformKey) }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"serial: {game.Serial ?? "none"}, names: {string.Join(" | ", names.Names)}, file: {imagePath ?? "none"}";
        Console.WriteLine($"[Pcsx2Detector] Game '{game.Key}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[Pcsx2Detector] PCSX2 (PID {process.Id}) is running '{name}' with key '{game.Key}' ({sources}, IGDB: {idIgdb ?? "null"}).");

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
}
