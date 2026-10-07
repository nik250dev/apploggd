using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BackloggdMirror.Services.Emulation.RetroArch;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>
/// Where a running RetroArch keeps its cfg, core info and history on Linux, and what its command line
/// loaded. Every path is as RetroArch sees it; the Readable ones go through its root, for the Flatpak
/// (its info files live in /app). Cached per PID and start time: none of it changes while it runs.
/// </summary>
internal sealed class LinuxRetroArchProcessInfo
{
    // getopt options of RetroArch that take a value, so the value is not taken for the content.
    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.Ordinal)
    {
        "-s", "-S", "-A", "-U", "-N", "-d", "-e", "-C", "-F", "-L", "-r", "-c", "-P", "-R", "-M",
        "--config", "--appendconfig", "--libretro", "--subsystem", "--save", "--savestate", "--record",
        "--recordconfig", "--size", "--bps", "--ips", "--xdelta", "--ups", "--connect", "--port", "--nick",
        "--entryslot", "--log-file", "--max-frames", "--max-frames-ss-path", "--sram-mode", "--play-replay", "--record-replay"
    };

    private static readonly Dictionary<int, LinuxRetroArchProcessInfo> _cache = new();
    private static readonly object _cacheLock = new();

    private readonly int _pid;
    private readonly List<string> _infoDirs;
    private readonly Dictionary<string, string?> _infoDirByCore = new(StringComparer.Ordinal);

    public DateTime StartTimeUtc { get; }
    public string? ReadableHistoryPath { get; }
    public bool HistoryEnabled { get; }

    /// <summary>The content named on the command line, as frontends launch it: named, not necessarily still loaded.</summary>
    public string? CommandLineContent { get; }

    /// <summary>The file name of the -L core, if any.</summary>
    public string? CommandLineCore { get; }

    private LinuxRetroArchProcessInfo(int pid, DateTime startTimeUtc, List<string> infoDirs, string? readableHistoryPath,
        bool historyEnabled, string? commandLineContent, string? commandLineCore)
    {
        _pid = pid;
        StartTimeUtc = startTimeUtc;
        _infoDirs = infoDirs;
        ReadableHistoryPath = readableHistoryPath;
        HistoryEnabled = historyEnabled;
        CommandLineContent = commandLineContent;
        CommandLineCore = commandLineCore;
    }

    public static LinuxRetroArchProcessInfo? For(int pid)
    {
        DateTime startTimeUtc;
        try
        {
            using var process = Process.GetProcessById(pid);
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(pid, out var cached) && cached.StartTimeUtc == startTimeUtc)
                return cached;

            var built = Build(pid, startTimeUtc);
            _cache[pid] = built;
            return built;
        }
    }

    public static void KeepOnly(ICollection<int> alive)
    {
        lock (_cacheLock)
        {
            foreach (int pid in _cache.Keys.Where(pid => !alive.Contains(pid)).ToList())
                _cache.Remove(pid);
        }
    }

    /// <summary>The readable folder holding the .info of the core at <paramref name="corePath"/>, or the likeliest one.</summary>
    public string? ReadableInfoDirFor(string corePath)
    {
        lock (_infoDirByCore)
        {
            if (_infoDirByCore.TryGetValue(corePath, out string? known))
                return known;

            string infoName = Path.GetFileNameWithoutExtension(corePath) + ".info";
            var candidates = _infoDirs.Append(Path.GetDirectoryName(corePath)).OfType<string>()
                .Select(dir => LinuxEmulatorProcesses.Readable(_pid, dir)).OfType<string>().ToList();

            string? found = candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, infoName))) ?? candidates.FirstOrDefault();
            _infoDirByCore[corePath] = found;
            return found;
        }
    }

    public bool HistoryWrittenAfterStart()
    {
        try
        {
            return ReadableHistoryPath != null && File.GetLastWriteTimeUtc(ReadableHistoryPath) > StartTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static LinuxRetroArchProcessInfo Build(int pid, DateTime startTimeUtc)
    {
        string? home = LinuxEmulatorProcesses.EnvironmentVariable(pid, "HOME");
        string? appDir = LinuxEmulatorProcesses.ExecutableDir(pid);
        string? workingDir = LinuxEmulatorProcesses.WorkingDir(pid);
        string[] arguments = LinuxProcFs.ReadArgs(pid) ?? Array.Empty<string>();

        // RetroArch's own order on Unix; the Flatpak moves XDG_CONFIG_HOME to ~/.var/app/<id>/config.
        string? xdgConfig = LinuxEmulatorProcesses.EnvironmentVariable(pid, "XDG_CONFIG_HOME")
                            ?? (home != null ? Path.Combine(home, ".config") : null);
        string? configDir = xdgConfig != null ? Path.Combine(xdgConfig, "retroarch") : null;

        string? configPath = new[]
            {
                RetroArchProcessInfo.OptionValues(arguments, 'c', "config").Select(v => Absolute(v, workingDir, home, appDir)).LastOrDefault(),
                configDir != null ? Path.Combine(configDir, "retroarch.cfg") : null,
                home != null ? Path.Combine(home, ".retroarch.cfg") : null
            }
            .FirstOrDefault(path => Exists(pid, path));

        var config = configPath != null
            ? RetroArchConfig.Parse(LinuxEmulatorProcesses.Readable(pid, configPath)!)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // --appendconfig takes "a.cfg|b.cfg", each overriding the keys of the ones before.
        foreach (string value in RetroArchProcessInfo.OptionValues(arguments, null, "appendconfig"))
        {
            foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? appendPath = Absolute(part, workingDir, home, appDir);
                if (!Exists(pid, appendPath))
                    continue;

                foreach (var (key, appended) in RetroArchConfig.Parse(LinuxEmulatorProcesses.Readable(pid, appendPath)!))
                    config[key] = appended;
            }
        }

        string? baseDir = configPath != null ? Path.GetDirectoryName(configPath) : configDir;

        // Without libretro_info_path, a Unix build looks beside the cores; distro packages install them in share.
        var infoDirs = new[]
            {
                Setting(config, "libretro_info_path", home, appDir),
                baseDir != null ? Path.Combine(baseDir, "cores") : null,
                baseDir != null ? Path.Combine(baseDir, "info") : null,
                "/usr/share/libretro/info",
                "/usr/local/share/libretro/info"
            }
            .OfType<string>().Distinct().ToList();

        string? historyDir = Setting(config, "content_history_directory", home, appDir);
        string? playlistDir = Setting(config, "playlist_directory", home, appDir)
                              ?? (baseDir != null ? Path.Combine(baseDir, "playlists") : null);

        var historyCandidates = new[]
        {
            Setting(config, "content_history_path", home, appDir),
            historyDir != null ? Path.Combine(historyDir, "content_history.lpl") : null,
            playlistDir != null ? Path.Combine(playlistDir, "builtin", "content_history.lpl") : null,
            baseDir != null ? Path.Combine(baseDir, "content_history.lpl") : null
        };
        string? historyPath = historyCandidates.FirstOrDefault(path => Exists(pid, path)) ?? historyCandidates.OfType<string>().FirstOrDefault();

        bool historyEnabled = !config.TryGetValue("history_list_enable", out string? enabled)
                              || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);

        string? core = RetroArchProcessInfo.OptionValues(arguments, 'L', "libretro").LastOrDefault();

        return new LinuxRetroArchProcessInfo(pid, startTimeUtc, infoDirs, LinuxEmulatorProcesses.Readable(pid, historyPath),
            historyEnabled, CommandLineContentOf(pid, arguments, workingDir), core != null ? Path.GetFileName(core) : null);
    }

    /// <summary>The last argument that is not an option or its value and names an existing file other than a core or a cfg.</summary>
    private static string? CommandLineContentOf(int pid, string[] arguments, string? workingDir)
    {
        string? content = null;
        bool optionsEnded = false;

        for (int i = 1; i < arguments.Length; i++)
        {
            string argument = arguments[i];

            if (!optionsEnded)
            {
                if (argument == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                if (argument.StartsWith('-'))
                {
                    if (OptionsWithValue.Contains(argument))
                        i++;
                    continue;
                }
            }

            string? path = argument.StartsWith('/') ? argument : workingDir != null ? Path.Combine(workingDir, argument) : null;
            if (path == null || path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                continue;

            // "archive.zip#inner.gb" names a file inside the archive.
            string file = path;
            int zipped = ArchiveSeparator(path);
            if (zipped > 0)
                file = path[..zipped];

            if (Exists(pid, file))
                content = Path.GetFullPath(path);
        }

        return content;
    }

    /// <summary>Where "archive#inner" splits, or -1. Linux names may hold '#', so only after an archive extension.</summary>
    internal static int ArchiveSeparator(string path)
    {
        foreach (string extension in new[] { ".zip#", ".7z#" })
        {
            int index = path.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
                return index + extension.Length - 1;
        }

        return -1;
    }

    /// <summary>A path setting, where "default" or empty means RetroArch's own default.</summary>
    private static string? Setting(Dictionary<string, string> config, string key, string? home, string? appDir)
    {
        if (!config.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) || value == "default")
            return null;

        return Absolute(value, null, home, appDir);
    }

    /// <summary>Expands RetroArch's prefixes: "~/" is the home folder and ":/" the folder of the executable.</summary>
    private static string? Absolute(string value, string? workingDir, string? home, string? appDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var (prefix, dir) in new[] { ('~', home), (':', appDir) })
        {
            if (value.Length >= 2 && value[0] == prefix && value[1] == '/')
                return dir != null ? Path.Combine(dir, value[2..]) : null;

            if (value.Length == 1 && value[0] == prefix)
                return dir;
        }

        if (value.StartsWith('/'))
            return value;

        return workingDir != null ? Path.Combine(workingDir, value) : null;
    }

    private static bool Exists(int pid, string? path)
    {
        string? readable = LinuxEmulatorProcesses.Readable(pid, path);
        return readable != null && File.Exists(readable);
    }
}

/// <summary>
/// RetroArch's content_history.lpl, read as it is on Linux: the Windows reader rewrites separators to '\'.
/// It says what was loaded last, never whether it still is, and RetroArch does not rewrite it when the content
/// is already its first entry.
/// </summary>
internal static class LinuxRetroArchHistory
{
    private static readonly object _cacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static long _cachedLength;
    private static List<RetroArchHistoryEntry> _cachedItems = new();

    public static IReadOnlyList<RetroArchHistoryEntry> Read(string? readablePath)
    {
        if (string.IsNullOrEmpty(readablePath))
            return Array.Empty<RetroArchHistoryEntry>();

        try
        {
            var file = new FileInfo(readablePath);
            if (!file.Exists)
                return Array.Empty<RetroArchHistoryEntry>();

            lock (_cacheLock)
            {
                if (_cachedPath == readablePath && _cachedWriteTimeUtc == file.LastWriteTimeUtc && _cachedLength == file.Length)
                    return _cachedItems;

                var parsed = JsonSerializer.Deserialize<RetroArchHistoryFile>(File.ReadAllText(readablePath));

                _cachedPath = readablePath;
                _cachedWriteTimeUtc = file.LastWriteTimeUtc;
                _cachedLength = file.Length;
                _cachedItems = parsed?.Items ?? new List<RetroArchHistoryEntry>();

                return _cachedItems;
            }
        }
        catch
        {
            // Old plain-text playlists, or a file being rewritten: the history is only ever a complement.
            return Array.Empty<RetroArchHistoryEntry>();
        }
    }

    public static RetroArchHistoryEntry? First(string? readablePath)
    {
        var items = Read(readablePath);
        return items.Count > 0 ? items[0] : null;
    }

    public static RetroArchHistoryEntry? Lookup(string? readablePath, string contentPath, string archivePath)
    {
        return Read(readablePath).FirstOrDefault(entry =>
            entry.Path.Length > 0 && (entry.Path == contentPath || entry.Path == archivePath));
    }
}
