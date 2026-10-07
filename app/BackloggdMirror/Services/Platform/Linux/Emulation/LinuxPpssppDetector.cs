using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.Ppsspp;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// PPSSPP on Linux, native or Flatpak. There is no window message to ask, and Wayland hides the window
/// title, so it reads what /proc shows: PPSSPP keeps the emulated RAM in a shared memory file
/// (/dev/shm/ppsspp_&lt;n&gt;.ram, unlinked at once) and the game image open while a game runs, pause
/// included, and closes both when it shuts down. The PARAM.SFO read from that image gives the same disc ID
/// the Windows detector gets; a session is bound to (PID, disc ID), or to the image path when the format
/// cannot be read (CHD).
/// </summary>
internal sealed class LinuxPpssppDetector : IEmulatorDetector
{
    // The SDL build (Flatpak, most distros) and the Qt one.
    internal static readonly string[] ProcessNames = { "PPSSPPSDL", "PPSSPPQt" };

    public string Name => "PPSSPP";

    private sealed record LinuxPpssppGame(string Key, string? DiscId, string? Title, string ImagePath);

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();

    // The PARAM.SFO is read once per image a process opens, not on every pass.
    private readonly Dictionary<int, LinuxPpssppGame> _games = new();

    public LinuxPpssppDetector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
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

        _logger?.Info($"[LinuxPpssppDetector] Game '{game.ContentKey}' is no longer running in PPSSPP (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        _games.Remove(pid);
        return false;
    }

    /// <summary>Null while PPSSPP sits in its menu: no emulated RAM open.</summary>
    private LinuxPpssppGame? ReadGame(int pid)
    {
        var files = LinuxProcFs.ReadOpenFiles(pid);
        if (!files.Any(f => IsEmulatedRam(f.Target)))
            return null;

        var image = files.FirstOrDefault(f => f.Target.StartsWith('/') && !f.Target.EndsWith(" (deleted)", StringComparison.Ordinal)
                                              && PspImageReader.Extensions.Contains(Path.GetExtension(f.Target)));
        if (image.Target == null)
            return null;

        if (_games.TryGetValue(pid, out var cached) && cached.ImagePath == image.Target)
            return cached;

        // Through the descriptor, so a Flatpak's paths need no translating.
        var sfo = PspImageReader.Read($"/proc/{pid}/fd/{image.Fd}");
        var game = new LinuxPpssppGame(sfo?.DiscId ?? image.Target, sfo?.DiscId, sfo?.Title, image.Target);
        _games[pid] = game;

        if (sfo?.DiscId == null)
            _logger?.Info($"[LinuxPpssppDetector] Could not read the disc ID from '{image.Target}' (PID {pid}); the session is bound to its path. CHD images are not read.");

        return game;
    }

    // "/dev/shm/ppsspp_0.ram (deleted)"; without shm_open PPSSPP falls back to a gc_mem.tmp in /dev/shm or /tmp.
    private static bool IsEmulatedRam(string target) =>
        (target.StartsWith("/dev/shm/ppsspp_", StringComparison.Ordinal) && target.Contains(".ram", StringComparison.Ordinal))
        || target.StartsWith("/dev/shm/gc_mem.tmp", StringComparison.Ordinal)
        || target.StartsWith("/tmp/gc_mem.tmp", StringComparison.Ordinal);

    private DetectedGame Identify(int pid, LinuxPpssppGame game)
    {
        var names = PpssppNames.For(game.Title, game.ImagePath, game.DiscId ?? game.ImagePath);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("psp") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"disc ID: {game.DiscId ?? "unread"}, names: {string.Join(" | ", names.Names)}, file: {game.ImagePath}";
        Console.WriteLine($"[LinuxPpssppDetector] '{game.Key}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxPpssppDetector] PPSSPP (PID {pid}) is running '{name}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, game.Key, platforms.FirstOrDefault()?.Key, Name);
    }
}
