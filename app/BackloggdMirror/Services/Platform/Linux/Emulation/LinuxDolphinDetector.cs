using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.Dolphin;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// Dolphin on Linux, native or Flatpak. The emulated RAM is a shared memory file (/dev/shm/dolphin-emu.&lt;pid&gt;,
/// unlinked at once) that Dolphin keeps open from boot to Stop, with MEM1 at offset 0. Reopening it through
/// /proc/&lt;pid&gt;/fd only needs the same user, unlike reading the process memory, and gives the same disc
/// header the Windows detector reads: the game ID and the console. A session is bound to (PID, game ID).
/// </summary>
internal sealed class LinuxDolphinDetector : IEmulatorDetector
{
    // The kernel cuts "dolphin-emu-nogui" to 15 characters.
    private static readonly string[] ProcessNames = { "dolphin-emu", "dolphin-emu-nog" };

    // The smallest MEM1 there is (24 MB). Not an exact size: the RAM override changes it, and the file holds every region.
    private const long MinMem1Size = 0x1800000;
    private const int HeaderSize = 0x20;
    private const uint WiiMagic = 0x5D1C9EA3;
    private const uint GameCubeMagic = 0xC2339F3D;

    public string Name => "Dolphin";

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();
    private readonly Dictionary<int, int> _ramFds = new();

    public LinuxDolphinDetector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
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
        foreach (int gone in _ramFds.Keys.Where(pid => !pids.Contains(pid)).ToList())
            _ramFds.Remove(gone);

        foreach (int pid in pids)
        {
            if (_blacklist?.IsApplicationBlocked(pid, LinuxEmulatorProcesses.ExecutableName(pid)) == true)
                continue;

            var disc = ReadDisc(pid);

            if (!_debounce.Observe(pid, disc?.GameId) || disc == null)
                continue;

            if (_blacklist?.IsContentBlocked(Name, disc.GameId) == true)
                continue;

            return Identify(pid, disc);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        int pid = (int)game.ProcessId;
        if (!LinuxEmulatorProcesses.IsStillRunning(pid, ProcessNames))
            return Closed(pid);

        var disc = ReadDisc(pid);
        if (_debounce.StillHolds(pid, disc != null && disc.GameId == game.ContentKey))
            return true;

        _logger?.Info($"[LinuxDolphinDetector] Game '{game.ContentKey}' is no longer running in Dolphin (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        _ramFds.Remove(pid);
        return false;
    }

    /// <summary>Null when Dolphin sits in its game list: no emulated RAM, or no header in it yet.</summary>
    private DolphinDisc? ReadDisc(int pid)
    {
        // The descriptor is looked up once per boot; a number reused after Stop no longer names the RAM.
        if (_ramFds.TryGetValue(pid, out int cached))
        {
            if (IsEmulatedRam(Target(pid, cached)))
                return ReadHeader(pid, cached);

            _ramFds.Remove(pid);
        }

        foreach (var (fd, target) in LinuxProcFs.ReadOpenFiles(pid))
        {
            if (!IsEmulatedRam(target))
                continue;

            _ramFds[pid] = fd;
            return ReadHeader(pid, fd);
        }

        return null;
    }

    // "/dev/shm/dolphin-emu.33 (deleted)" today; a memfd would read "/memfd:dolphin-emu (deleted)".
    private static bool IsEmulatedRam(string? target) =>
        target != null && target.Contains("dolphin-emu", StringComparison.Ordinal)
                       && (target.StartsWith("/dev/shm/", StringComparison.Ordinal) || target.StartsWith("/memfd:", StringComparison.Ordinal));

    private static string? Target(int pid, int fd)
    {
        try
        {
            return new FileInfo($"/proc/{pid}/fd/{fd}").LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static DolphinDisc? ReadHeader(int pid, int fd)
    {
        try
        {
            using var handle = File.OpenHandle($"/proc/{pid}/fd/{fd}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (RandomAccess.GetLength(handle) < MinMem1Size)
                return null;

            var header = new byte[HeaderSize];
            if (RandomAccess.Read(handle, header, 0) < HeaderSize)
                return null;

            for (int i = 0; i < 6; i++)
            {
                byte c = header[i];
                if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'0' and <= (byte)'9'))
                    return null;
            }

            bool? isWii = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18)) == WiiMagic ? true
                : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x1C)) == GameCubeMagic ? false
                : null;

            return isWii == null ? null : new DolphinDisc(System.Text.Encoding.ASCII.GetString(header, 0, 6), isWii);
        }
        catch
        {
            // Stopped mid-read.
            return null;
        }
    }

    private DetectedGame Identify(int pid, DolphinDisc disc)
    {
        string? titleName = SysDirs(pid).Select(dir => DolphinTitleDatabase.FindNameInSys(dir, disc.GameId)).FirstOrDefault(name => name != null);
        string? discPath = titleName == null ? LinuxEmulatorProcesses.FindOpenFile(LinuxProcFs.ReadOpenFiles(pid), DolphinDetector.DiscExtensions) : null;

        var names = RomNameCleaner.Clean(discPath ?? string.Empty, null, titleName);
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(disc.GameId, null);

        var platforms = DolphinDetector.PlatformsFor(disc);
        string? idIgdb = _resolver.Resolve(platforms, names);

        string? platformKey = platforms.Count > 0 ? platforms[0].Key : null;
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string platformLabel = string.Join(", ", platforms.Select(p => p.Key));
        string source = titleName != null ? "title database" : discPath != null ? $"file '{discPath}'" : "game ID only";
        Console.WriteLine($"[LinuxDolphinDetector] Game '{disc.GameId}' → '{name}' (platform: {platformLabel}, name from: {source}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxDolphinDetector] Dolphin (PID {pid}) is running '{name}' with game ID '{disc.GameId}' (platform: {platformLabel}, name from: {source}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, disc.GameId, platformKey, Name);
    }

    /// <summary>
    /// Where the GameTDB files may be, readable from here: installs put Sys in share/dolphin-emu ("sys" from
    /// CMake, "Sys" in the Flatpak), and portable builds beside the executable.
    /// </summary>
    private static IEnumerable<string> SysDirs(int pid)
    {
        string? exeDir = LinuxEmulatorProcesses.ExecutableDir(pid);
        var dirs = new List<string>();

        if (exeDir != null)
        {
            string share = Path.GetFullPath(Path.Combine(exeDir, "..", "share", "dolphin-emu"));
            dirs.Add(Path.Combine(share, "sys"));
            dirs.Add(Path.Combine(share, "Sys"));
            dirs.Add(Path.Combine(exeDir, "Sys"));
        }

        dirs.Add("/usr/share/dolphin-emu/sys");
        dirs.Add("/usr/local/share/dolphin-emu/sys");

        return dirs.Distinct().Select(dir => LinuxEmulatorProcesses.Readable(pid, dir)).OfType<string>().Where(Directory.Exists);
    }
}
