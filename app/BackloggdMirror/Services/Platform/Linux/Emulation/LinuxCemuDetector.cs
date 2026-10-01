using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.Cemu;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// Cemu on Linux. The Windows detector names the game from the window title, which Cemu draws natively
/// under Wayland where no other app can read it. What names it here is the shader cache Cemu keeps open
/// for the running title, named after its title ID (".../shaderCache/transferable/0005000010144d00_shaders.bin"),
/// with the "TitleId:" line of its log as a fallback. Whether a game is still loaded is the Windows signal
/// read from /proc/&lt;pid&gt;/maps: MEM2 (1 GB at +0x10000000 of the 4 GB Wii U space) is read-write while a
/// title runs and inaccessible otherwise. The base of that space is in the log. A session is bound to (PID, title ID).
/// </summary>
internal sealed class LinuxCemuDetector : IEmulatorDetector
{
    private const string ProcessName = "cemu";

    private const ulong Mem2LastPage = 0x10000000 + 0x40000000 - 0x1000;

    // Mapped by Cemu before any title boots, so they confirm a base read from the log.
    private const ulong CemuAreaOffset = 0x0E000000;
    private const ulong SharedAreaOffset = 0xF8000000;

    private static readonly Regex ShaderCacheFile = new(@"/shaderCache/(?:transferable|precompiled)/([0-9a-f]{16})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemoryBaseLine = new(@"Init Wii U memory space \(base: 0x([0-9a-f]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TitleIdLine = new(@"TitleId: ([0-9a-f]{8})-([0-9a-f]{8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "Cemu";

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();

    // 0 means not found yet; the Wii U space never moves while the process lives.
    private readonly Dictionary<int, ulong> _bases = new();
    private readonly HashSet<int> _degradedLogged = new();

    public LinuxCemuDetector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
    {
        _processes = processes;
        _resolver = resolver;
        _logger = logger;
        _blacklist = blacklist;
    }

    public DetectedGame? Detect()
    {
        var pids = _processes.Find(ProcessName);
        _debounce.KeepOnly(pids);
        foreach (int gone in _bases.Keys.Where(pid => !pids.Contains(pid)).ToList())
            _bases.Remove(gone);
        _degradedLogged.IntersectWith(pids);

        foreach (int pid in pids)
        {
            if (_blacklist?.IsApplicationBlocked(pid, LinuxEmulatorProcesses.ExecutableName(pid)) == true)
                continue;

            var files = LinuxProcFs.ReadOpenFiles(pid);
            string? titleId = ReadTitleId(pid, files);

            if (!_debounce.Observe(pid, titleId) || titleId == null)
                continue;

            if (_blacklist?.IsContentBlocked(Name, titleId) == true)
                continue;

            return Identify(pid, titleId, files);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        int pid = (int)game.ProcessId;
        if (!LinuxEmulatorProcesses.IsStillRunning(pid, ProcessName))
            return Closed(pid);

        string? titleId = ReadTitleId(pid, LinuxProcFs.ReadOpenFiles(pid));
        if (_debounce.StillHolds(pid, titleId == game.ContentKey))
            return true;

        _logger?.Info($"[LinuxCemuDetector] Title '{game.ContentKey}' is no longer running in Cemu (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        return false;
    }

    /// <summary>Null when Cemu sits in its game list or has stopped the game.</summary>
    private string? ReadTitleId(int pid, List<(int Fd, string Target)> files)
    {
        string? logPath = files.Select(f => f.Target).FirstOrDefault(t => t.EndsWith("/log.txt", StringComparison.Ordinal));
        bool? loaded = IsGameLoaded(pid, logPath);
        if (loaded == false)
            return null;

        string? fromCache = files.Select(f => ShaderCacheFile.Match(f.Target)).FirstOrDefault(m => m.Success)?.Groups[1].Value.ToLowerInvariant();

        if (loaded == null)
        {
            if (fromCache != null && _degradedLogged.Add(pid))
                _logger?.Warning($"[LinuxCemuDetector] Could not find the Wii U memory of Cemu (PID {pid}) through its log. Relying on its open shader cache alone.");

            return fromCache;
        }

        // The log is not reset on Stop, so it only names the title while the memory says one runs.
        return fromCache ?? TitleIdFromLog(pid, logPath);
    }

    /// <summary>Null when unknown: no log open yet, or a base that does not look like Cemu's space.</summary>
    private bool? IsGameLoaded(int pid, string? logPath)
    {
        if (!_bases.TryGetValue(pid, out ulong memoryBase) || memoryBase == 0)
        {
            memoryBase = BaseFromLog(pid, logPath);
            if (memoryBase == 0)
                return null;

            var areas = Permissions(pid, memoryBase + CemuAreaOffset, memoryBase + SharedAreaOffset);
            if (areas[0]?.StartsWith("rw", StringComparison.Ordinal) != true || areas[1]?.StartsWith("rw", StringComparison.Ordinal) != true)
                return null;

            _bases[pid] = memoryBase;
        }

        string? mem2 = Permissions(pid, memoryBase + Mem2LastPage)[0];
        return mem2 == null ? null : mem2.StartsWith("rw", StringComparison.Ordinal);
    }

    private static ulong BaseFromLog(int pid, string? logPath)
    {
        string? readable = LinuxEmulatorProcesses.Readable(pid, logPath);
        if (readable == null)
            return 0;

        try
        {
            foreach (string line in File.ReadLines(readable).Take(50))
            {
                var match = MemoryBaseLine.Match(line);
                if (match.Success)
                    return ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
            }
        }
        catch
        {
            // Rewritten or gone.
        }

        return 0;
    }

    private static string? TitleIdFromLog(int pid, string? logPath)
    {
        string? readable = LinuxEmulatorProcesses.Readable(pid, logPath);
        if (readable == null)
            return null;

        try
        {
            string? last = null;
            foreach (string line in File.ReadLines(readable))
            {
                var match = TitleIdLine.Match(line);
                if (match.Success)
                    last = (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant();
            }

            return last;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The permissions ("rw-p", "---p") of the mappings holding each address, in ascending order; null where none does.</summary>
    private static string?[] Permissions(int pid, params ulong[] addresses)
    {
        var result = new string?[addresses.Length];
        int next = 0;

        try
        {
            // Sorted by address, so the read stops at the last one asked for.
            foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            {
                int dash = line.IndexOf('-');
                int space = line.IndexOf(' ');
                ulong start = ulong.Parse(line.AsSpan(0, dash), NumberStyles.HexNumber);
                ulong end = ulong.Parse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber);

                while (next < addresses.Length && addresses[next] < start)
                    next++;

                while (next < addresses.Length && addresses[next] < end)
                    result[next++] = line.Substring(space + 1, 4);

                if (next == addresses.Length)
                    break;
            }
        }
        catch
        {
            // Gone mid-read.
        }

        return result;
    }

    private DetectedGame Identify(int pid, string titleId, List<(int Fd, string Target)> files)
    {
        var labels = TitleNames(pid, titleId, files);
        string? imagePath = LinuxEmulatorProcesses.FindOpenFile(files, CemuDetector.ImageExtensions);

        var names = RomNameCleaner.Clean(imagePath ?? string.Empty, null, labels.ToArray());
        if (names.Names.Count == 0)
            names = RomNameCleaner.Clean(titleId, null);

        var platforms = new[] { EmulatedPlatformResolver.ByKey("wiiu") }.OfType<EmulatedPlatform>().ToList();
        string? idIgdb = _resolver.Resolve(platforms, names);
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string sources = $"names: {string.Join(" | ", names.Names)}, file: {imagePath ?? "none"}";
        Console.WriteLine($"[LinuxCemuDetector] Title '{titleId}' → '{name}' ({sources}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxCemuDetector] Cemu (PID {pid}) is running '{name}' with title ID '{titleId}' ({sources}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, titleId, platforms.FirstOrDefault()?.Key, Name);
    }

    /// <summary>
    /// The names Windows takes from Cemu's files, found where Cemu keeps them on Linux: the title cache beside
    /// log.txt (~/.local/share/Cemu, or "portable" beside the executable), settings.xml in ~/.config/Cemu, and
    /// the bundled game profiles beside the executable or in share/Cemu. There is no window name to add.
    /// </summary>
    private static IReadOnlyList<string> TitleNames(int pid, string titleId, List<(int Fd, string Target)> files)
    {
        string? exeDir = LinuxEmulatorProcesses.ExecutableDir(pid);
        string? home = LinuxEmulatorProcesses.EnvironmentVariable(pid, "HOME");
        string? logPath = files.Select(f => f.Target).FirstOrDefault(t => t.EndsWith("/log.txt", StringComparison.Ordinal));

        string? portable = exeDir != null ? Path.Combine(exeDir, "portable") : null;
        bool isPortable = Directory.Exists(LinuxEmulatorProcesses.Readable(pid, portable) ?? string.Empty);

        string? userDir = logPath != null ? Path.GetDirectoryName(logPath)
            : isPortable ? portable
            : XdgDir(pid, "XDG_DATA_HOME", home, ".local/share");
        string? configDir = isPortable ? portable : XdgDir(pid, "XDG_CONFIG_HOME", home, ".config");

        string? readableUserDir = LinuxEmulatorProcesses.Readable(pid, userDir);
        string? cacheName = readableUserDir != null ? CemuTitleNames.FromTitleCache(readableUserDir, titleId) : null;

        var profileDirs = new[] { exeDir, exeDir != null ? Path.GetFullPath(Path.Combine(exeDir, "..", "share", "Cemu")) : null };
        string? profileName = profileDirs.Select(dir => LinuxEmulatorProcesses.Readable(pid, dir)).OfType<string>()
            .Select(dir => CemuTitleNames.FromGameProfile(dir, titleId)).FirstOrDefault(n => n != null);

        var ordered = CemuTitleNames.IsEnglishConsole(LinuxEmulatorProcesses.Readable(pid, configDir))
            ? new[] { cacheName, profileName }
            : new[] { profileName, cacheName };

        return ordered.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? XdgDir(int pid, string variable, string? home, string fallback)
    {
        string? root = LinuxEmulatorProcesses.EnvironmentVariable(pid, variable) ?? (home != null ? Path.Combine(home, fallback) : null);
        return root != null ? Path.Combine(root, "Cemu") : null;
    }
}
