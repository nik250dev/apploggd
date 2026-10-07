using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.DuckStation;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// DuckStation on Linux (AppImage, Flatpak or a distro build). Its memory, where Windows reads the running
/// serial, is closed to other processes, and its Wayland window cannot be read either. What /proc does show:
/// DuckStation keeps the disc image open while a game runs, pause included, closes it when the game shuts
/// down and swaps it on a disc change. The serial is read from that image the way DuckStation reads it
/// (SYSTEM.CNF), mapped through its gamedb and disc sets, so the session key is the one Windows uses; an
/// image that cannot be read (CHD, ECM) binds the session to its path.
/// </summary>
internal sealed class LinuxDuckStationDetector : IEmulatorDetector
{
    // The Qt frontend and the one without it, cut to 15 characters by the kernel.
    internal static readonly string[] ProcessNames = { "duckstation-qt", "duckstation-nog" };

    // Images DuckStation keeps open while a game runs; a .cue or .m3u only points at them.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".img", ".iso", ".mdf", ".chd", ".ecm", ".pbp"
    };

    public string Name => "DuckStation";

    /// <param name="Key">As on Windows: the first serial of the disc set, the serial, or the image path when no serial can be read.</param>
    private sealed record LinuxDuckStationGame(string Key, string? Serial, string? Name, string? SetName, string ImagePath);

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();

    // The disc is read once per image a process opens, not on every pass.
    private readonly Dictionary<int, LinuxDuckStationGame> _games = new();

    public LinuxDuckStationDetector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
    {
        _processes = processes;
        _resolver = resolver;
        _logger = logger;
        _blacklist = blacklist;
    }

    public DetectedGame? Detect()
    {
        var pids = _processes.Find(ProcessNames);
        _debounce.KeepOnly(pids);
        foreach (int gone in _games.Keys.Where(pid => !pids.Contains(pid)).ToList())
            _games.Remove(gone);

        foreach (int pid in pids)
        {
            if (_blacklist?.IsApplicationBlocked(pid, LinuxEmulatorProcesses.ExecutableName(pid)) == true)
                continue;

            var game = ReadGame(pid);

            if (!_debounce.Observe(pid, game?.Key) || game == null)
                continue;

            if (_blacklist?.IsContentBlocked(Name, game.Key) == true)
                continue;

            return Identify(pid, game);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        int pid = (int)game.ProcessId;
        if (!LinuxEmulatorProcesses.IsStillRunning(pid, ProcessNames))
            return Closed(pid);

        var running = ReadGame(pid);
        if (_debounce.StillHolds(pid, running != null && running.Key == game.ContentKey))
            return true;

        _logger?.Info($"[LinuxDuckStationDetector] Game '{game.ContentKey}' is no longer running in DuckStation (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        _games.Remove(pid);
        return false;
    }

    /// <summary>Null while DuckStation sits in its game list or runs the BIOS alone: no disc image open.</summary>
    private LinuxDuckStationGame? ReadGame(int pid)
    {
        var images = LinuxProcFs.ReadOpenFiles(pid)
            .Where(f => f.Target.StartsWith('/') && !f.Target.EndsWith(" (deleted)", StringComparison.Ordinal)
                        && ImageExtensions.Contains(Path.GetExtension(f.Target)))
            .ToList();
        if (images.Count == 0)
            return null;

        if (_games.TryGetValue(pid, out var cached) && images.Any(f => f.Target == cached.ImagePath))
            return cached;

        // The first image that reads as a disc: audio tracks and DuckStation's own pipeline cache do not.
        foreach (var (fd, target) in images)
        {
            // Through the descriptor, so a Flatpak's paths need no translating.
            var disc = PsxDiscReader.Read($"/proc/{pid}/fd/{fd}");
            if (disc == null)
                continue;

            var game = Resolve(pid, disc.GameId, target);
            _games[pid] = game;
            return game;
        }

        return null;
    }

    private LinuxDuckStationGame Resolve(int pid, string? gameId, string imagePath)
    {
        if (gameId == null)
        {
            _logger?.Info($"[LinuxDuckStationDetector] Could not read the serial from '{imagePath}' (PID {pid}); the session is bound to its path. CHD and ECM images are not read.");
            return new LinuxDuckStationGame(imagePath, null, null, null, imagePath);
        }

        // Inside an AppImage's mount or a Flatpak, so through the process's root.
        string? directory = LinuxEmulatorProcesses.Readable(pid, LinuxEmulatorProcesses.ExecutableDir(pid));
        var entry = DuckStationGameDatabase.FindEntry(directory, gameId);
        string serial = entry?.Serial ?? gameId;
        var set = DuckStationGameDatabase.FindDiscSet(directory, serial);

        return new LinuxDuckStationGame(set?.Key ?? serial, serial, entry?.Name, set?.Name, imagePath);
    }

    private DetectedGame Identify(int pid, LinuxDuckStationGame game)
    {
        var names = RomNameCleaner.Clean(game.ImagePath, null, game.SetName, game.Name);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(game.Key, null);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("psx") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"serial: {game.Serial ?? "unread"}, names: {string.Join(" | ", names.Names)}, file: {game.ImagePath}";
        Console.WriteLine($"[LinuxDuckStationDetector] '{game.Key}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxDuckStationDetector] DuckStation (PID {pid}) is running '{name}' with key '{game.Key}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, game.Key, platforms.FirstOrDefault()?.Key, Name);
    }
}
