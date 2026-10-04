using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>
/// PPSSPP's detector. A session is bound to the pair (PID, disc ID). Since 1.20 PPSSPP answers which
/// game is booted through a window message, pause included; before that, the window title carries the
/// same ID and is reset when the game shuts down. Opening and closing need the same reading twice, as
/// with RetroArch.
/// </summary>
internal sealed class PpssppDetector : IEmulatorDetector
{
    // PPSSPPWindows64, PPSSPPWindows, PPSSPPWindowsARM64 and the debug builds.
    internal const string ProcessNamePrefix = "PPSSPP";
    private const int RequiredReadings = 2;
    private static readonly Version GameIdMessageVersion = new(1, 20);

    private sealed record PpssppGame(string DiscId, string? Name);

    private sealed class PidState
    {
        public bool VersionRead;
        public bool? AnswersGameId;
        public string? LastDiscId;
        public int SameCount;
        public int MissCount;
        public bool TitleOnlyWarningLogged;
    }

    public string Name => "PPSSPP";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;

    public PpssppDetector(EmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger = null, IDetectionBlacklist? blacklist = null)
    {
        _processes = processes;
        _resolver = resolver;
        _logger = logger;
        _blacklist = blacklist;
    }

    public DetectedGame? Detect()
    {
        var processes = _processes.Find(IsPpsspp);
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

            var game = ReadGame(process.Id);

            if (!Observe(process.Id, game?.DiscId) || game == null)
                continue;

            if (_blacklist?.IsContentBlocked(Name, game.DiscId) == true)
                continue;

            return Identify(process, game);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        try
        {
            using var process = Process.GetProcessById((int)game.ProcessId);
            if (process.HasExited || !IsPpsspp(process))
                return Closed(game);
        }
        catch
        {
            return Closed(game);
        }

        var running = ReadGame((int)game.ProcessId);
        var state = StateFor((int)game.ProcessId);

        if (running != null && string.Equals(running.DiscId, game.ContentKey, StringComparison.Ordinal))
        {
            state.MissCount = 0;
            return true;
        }

        state.MissCount++;
        if (state.MissCount < RequiredReadings)
            return true;

        _logger?.Info($"[PpssppDetector] Game '{game.ContentKey}' is no longer running in PPSSPP (PID {game.ProcessId}). Ending the session.");
        return Closed(game);
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsPpsspp(Process process)
    {
        try
        {
            return IsPpsspp(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPpsspp(string processName) =>
        processName.StartsWith(ProcessNamePrefix, StringComparison.OrdinalIgnoreCase);

    private bool Closed(DetectedGame game)
    {
        _states.Remove((int)game.ProcessId);
        return false;
    }

    /// <summary>Null while PPSSPP sits in its menu.</summary>
    private PpssppGame? ReadGame(int processId)
    {
        IntPtr window = PpssppWindow.Find(processId);
        if (window == IntPtr.Zero)
            return null;

        var state = StateFor(processId);
        if (!state.VersionRead)
        {
            state.VersionRead = true;
            state.AnswersGameId = AnswersGameId(processId);
        }

        var title = PpssppWindowTitles.Parse(PpssppWindow.Title(window));
        string? gameId = state.AnswersGameId != false ? PpssppWindow.GameId(window) : null;

        if (!string.IsNullOrEmpty(gameId))
        {
            string discId = DiscIdOf(gameId);
            bool sameGame = title != null && (title.DiscId == null || title.DiscId == discId);
            return new PpssppGame(discId, sameGame ? title!.Name : null);
        }

        // A version that has the message answers "no game" with it, while the title may still be catching up.
        if (gameId != null && state.AnswersGameId == true)
            return null;

        if (title?.DiscId == null)
            return null;

        if (!state.TitleOnlyWarningLogged)
        {
            state.TitleOnlyWarningLogged = true;
            string reason = state.AnswersGameId == false
                ? "This PPSSPP version predates the game ID message (1.20)."
                : "PPSSPP did not answer the game ID message, which happens when it runs elevated.";
            _logger?.Warning($"[PpssppDetector] {reason} Relying on the window title of PPSSPP (PID {processId}), which loses track of homebrew without a disc ID.");
        }

        return new PpssppGame(title.DiscId, title.Name);
    }

    /// <summary>Null when the executable carries no version, so the message is tried and the title backs it up.</summary>
    private static bool? AnswersGameId(int processId)
    {
        string? path = ProcessImagePath.TryGet(processId);
        if (path == null)
            return null;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0)
                return null;

            return new Version(info.FileMajorPart, info.FileMinorPart) >= GameIdMessageVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"ULES00026_1.00" → "ULES00026". The version is cut at the last underscore: made-up homebrew IDs may contain one.</summary>
    internal static string DiscIdOf(string gameId)
    {
        int separator = gameId.LastIndexOf('_');
        return separator > 0 ? gameId[..separator] : gameId;
    }

    private DetectedGame Identify(Process process, PpssppGame game)
    {
        string? imagePath = ProcessOpenFiles.FindFirst(process.Id, PspImageReader.Extensions);
        var names = PpssppNames.For(game.Name, imagePath, game.DiscId);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("psp") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"names: {string.Join(" | ", names.Names)}, file: {imagePath ?? "none"}";
        Console.WriteLine($"[PpssppDetector] Disc '{game.DiscId}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[PpssppDetector] PPSSPP (PID {process.Id}) is running '{name}' with disc ID '{game.DiscId}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)process.Id, idIgdb, DetectionSource.Emulator, game.DiscId, platforms.FirstOrDefault()?.Key, Name);
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

    private bool Observe(int processId, string? discId)
    {
        var state = StateFor(processId);

        if (string.Equals(state.LastDiscId, discId, StringComparison.Ordinal))
        {
            state.SameCount++;
        }
        else
        {
            state.LastDiscId = discId;
            state.SameCount = 1;
        }

        return discId != null && state.SameCount >= RequiredReadings;
    }

    private void PruneStates(Process[] processes)
    {
        var alive = new HashSet<int>(processes.Select(p => p.Id));

        foreach (int processId in _states.Keys.Where(id => !alive.Contains(id)).ToList())
            _states.Remove(processId);
    }
}
