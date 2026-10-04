using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.Pcsx2;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// PCSX2 on Linux (AppImage, Flatpak or a distro build). Its memory, where Windows reads the running serial,
/// is closed to other processes, and its Wayland window cannot be read either. What /proc does show: PCSX2
/// keeps the disc image open while a game runs, pause included, and closes it when the game shuts down;
/// the BIOS alone opens none. The serial is read from that image the way PCSX2 reads it (SYSTEM.CNF), so
/// the session key is the one Windows uses; an image that cannot be read (CHD, CSO, ZSO, gzip) binds the
/// session to its path.
/// </summary>
internal sealed class LinuxPcsx2Detector : IEmulatorDetector
{
    internal static readonly string[] ProcessNames = { "pcsx2-qt" };

    // Images PCSX2 keeps open while a game runs; its shader cache (gl_programs.bin) is open too and reads as no disc.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".bin", ".img", ".mdf", ".nrg", ".chd", ".cso", ".zso", ".gz"
    };

    public string Name => "PCSX2";

    /// <param name="Key">As on Windows: the serial, the boot file for discs without one, or the image path when it cannot be read.</param>
    private sealed record LinuxPcsx2Game(string Key, Ps2Disc Disc, string ImagePath);

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();

    // The disc is read once per image a process opens, not on every pass.
    private readonly Dictionary<int, LinuxPcsx2Game> _games = new();

    public LinuxPcsx2Detector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
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

        _logger?.Info($"[LinuxPcsx2Detector] Game '{game.ContentKey}' is no longer running in PCSX2 (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        _games.Remove(pid);
        return false;
    }

    /// <summary>Null while PCSX2 sits in its game list or runs the BIOS alone: no disc image open.</summary>
    private LinuxPcsx2Game? ReadGame(int pid)
    {
        var images = LinuxProcFs.ReadOpenFiles(pid)
            .Where(f => f.Target.StartsWith('/') && !f.Target.EndsWith(" (deleted)", StringComparison.Ordinal)
                        && ImageExtensions.Contains(Path.GetExtension(f.Target)))
            .ToList();
        if (images.Count == 0)
            return null;

        if (_games.TryGetValue(pid, out var cached) && images.Any(f => f.Target == cached.ImagePath))
            return cached;

        foreach (var (fd, target) in images)
        {
            // Through the descriptor, so a Flatpak's paths need no translating.
            var disc = Ps2DiscReader.Read($"/proc/{pid}/fd/{fd}");
            if (disc == null)
                continue;

            string key = disc.Serial is { Length: > 0 } serial ? serial : disc.BootElf ?? target;
            if (disc.BootElf == null)
                _logger?.Info($"[LinuxPcsx2Detector] Could not read the serial from '{target}' (PID {pid}); the session is bound to its path. CHD, CSO, ZSO and gzip images are not read.");

            var game = new LinuxPcsx2Game(key, disc, target);
            _games[pid] = game;
            return game;
        }

        return null;
    }

    private DetectedGame Identify(int pid, LinuxPcsx2Game game)
    {
        // Inside an AppImage's mount or a Flatpak, so through the process's root.
        string? directory = LinuxEmulatorProcesses.Readable(pid, LinuxEmulatorProcesses.ExecutableDir(pid));
        var entry = game.Disc.Serial is { Length: > 0 } serial ? Pcsx2GameIndex.FindEntry(directory, serial) : null;

        // Japanese discs are named in Japanese, with the English name beside it.
        string? title = entry?.Name != null && RomNameCleaner.IsLatinScript(entry.Name) ? entry.Name : null;

        var names = RomNameCleaner.Clean(game.ImagePath, null, entry?.EnglishName, title);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(game.Key, null);

        var platforms = new[] { EmulatedPlatformResolver.ByKey(game.Disc.IsPlayStation ? "psx" : "ps2") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"serial: {(game.Disc.Serial is { Length: > 0 } s ? s : "unread")}, names: {string.Join(" | ", names.Names)}, file: {game.ImagePath}";
        Console.WriteLine($"[LinuxPcsx2Detector] '{game.Key}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxPcsx2Detector] PCSX2 (PID {pid}) is running '{name}' with key '{game.Key}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, game.Key, platforms.FirstOrDefault()?.Key, Name);
    }
}
