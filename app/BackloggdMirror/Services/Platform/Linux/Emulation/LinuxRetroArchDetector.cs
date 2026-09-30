using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Emulation;
using BackloggdMirror.Services.Emulation.RetroArch;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <summary>A content path as RetroArch names it on Linux, with "archive.zip#inner.gb" split apart.</summary>
internal sealed record LinuxRetroArchContent(string FullPath, string ArchivePath, string? InnerName, string Extension)
{
    public static LinuxRetroArchContent From(string path)
    {
        string archivePath = path;
        string? innerName = null;

        int separator = LinuxRetroArchProcessInfo.ArchiveSeparator(path);
        if (separator > 0)
        {
            archivePath = path[..separator];
            innerName = path[(separator + 1)..];
        }

        string extension = Path.GetExtension(innerName ?? archivePath);
        extension = extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;

        return new LinuxRetroArchContent(path, archivePath, innerName, extension);
    }
}

/// <summary>
/// RetroArch on Linux, native or Flatpak. Whether content is loaded comes from the core: RetroArch maps
/// &lt;core&gt;_libretro.so while content runs and unmaps it on "Close Content" (seen in /proc/&lt;pid&gt;/maps).
/// Which content is read from its memory as on Windows (see <see cref="LinuxRetroArchMemory"/>) where the
/// kernel allows it, which is always for the Flatpak. Otherwise, as the ROM is not kept open, it comes from
/// the history playlist and the command line, checked against the loaded core. A session is bound to
/// (PID, content path), with the same two readings as the other detectors.
/// </summary>
internal sealed class LinuxRetroArchDetector : IEmulatorDetector
{
    private const string ProcessName = "retroarch";
    private const string CoreSuffix = "_libretro.so";

    public string Name => "RetroArch";

    private readonly LinuxEmulatorProcesses _processes;
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly ReadingDebounce _debounce = new();
    private readonly HashSet<int> _unknownContentLogged = new();
    private readonly HashSet<int> _scanLogged = new();

    public LinuxRetroArchDetector(LinuxEmulatorProcesses processes, EmulatedGameResolver resolver, IAppLogger? logger, IDetectionBlacklist? blacklist)
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
        LinuxRetroArchProcessInfo.KeepOnly(pids);
        _unknownContentLogged.IntersectWith(pids);
        _scanLogged.IntersectWith(pids);

        foreach (int pid in pids)
        {
            if (_blacklist?.IsApplicationBlocked(pid, LinuxEmulatorProcesses.ExecutableName(pid)) == true)
                continue;

            var content = ReadContent(pid, null, out var info, out var core);

            if (!_debounce.Observe(pid, content?.FullPath) || content == null || info == null)
                continue;

            // Before Identify, which may call the worker for a game that is going to be ignored.
            if (_blacklist?.IsContentBlocked(Name, content.FullPath) == true)
                continue;

            return Identify(pid, info, core, content);
        }

        return null;
    }

    public bool IsStillRunning(DetectedGame game)
    {
        int pid = (int)game.ProcessId;
        if (!LinuxEmulatorProcesses.IsStillRunning(pid, ProcessName))
            return Closed(pid);

        var content = ReadContent(pid, game.ContentKey, out _, out _);
        if (_debounce.StillHolds(pid, content != null && content.FullPath == game.ContentKey))
            return true;

        _logger?.Info($"[LinuxRetroArchDetector] Content '{game.ContentKey}' is no longer loaded in RetroArch (PID {pid}). Ending the session.");
        return Closed(pid);
    }

    private bool Closed(int pid)
    {
        _debounce.Forget(pid);
        return false;
    }

    /// <summary>Null when RetroArch sits in the menu, or when nothing says which content its core runs.</summary>
    private LinuxRetroArchContent? ReadContent(int pid, string? preferredPath, out LinuxRetroArchProcessInfo? info, out LibretroCoreInfo? core)
    {
        info = null;
        core = null;

        string? corePath = FindCore(pid);
        if (corePath == null)
            return null;

        info = LinuxRetroArchProcessInfo.For(pid);
        if (info == null)
            return null;

        string coreFile = Path.GetFileName(corePath);
        core = LibretroCoreInfo.Load(Path.GetFileNameWithoutExtension(coreFile), info.ReadableInfoDirFor(corePath));
        if (core.IsNonGame)
            return null;

        var scan = LinuxRetroArchMemory.Read(pid);
        LogScan(pid, scan);

        // An empty scan falls back too: a build that keeps the path elsewhere would otherwise never be detected.
        string? path = scan.Paths.Count > 0
            ? ChooseFromMemory(scan.Paths, info, core, preferredPath)
            : ChooseContent(info, coreFile);

        if (path == null)
        {
            if (_unknownContentLogged.Add(pid))
                _logger?.Warning($"[LinuxRetroArchDetector] RetroArch (PID {pid}) runs the core '{coreFile}', but neither its memory (readable: {scan.Readable}), its history (enabled: {info.HistoryEnabled}) nor its command line say which content. No session until they do.");
            return null;
        }

        _unknownContentLogged.Remove(pid);
        return LinuxRetroArchContent.From(path);
    }

    private void LogScan(int pid, LinuxRetroArchMemoryScan scan)
    {
        if (!_scanLogged.Add(pid))
            return;

        if (scan.Readable)
            _logger?.Info($"[LinuxRetroArchDetector] Scanned {scan.BytesRead / 1024} KB of the RetroArch data sections (PID {pid}) in {scan.ElapsedMilliseconds} ms, {scan.Paths.Count} content path(s) found.");
        else
            _logger?.Info($"[LinuxRetroArchDetector] The memory of RetroArch (PID {pid}) cannot be read (Yama ptrace_scope; a native build, not a Flatpak). Relying on its history and command line.");
    }

    /// <summary>The path is normally alone; several are ranked as Windows does.</summary>
    private static string ChooseFromMemory(IReadOnlyList<string> paths, LinuxRetroArchProcessInfo info, LibretroCoreInfo core, string? preferredPath)
    {
        if (paths.Count == 1)
            return paths[0];

        var first = LinuxRetroArchHistory.First(info.ReadableHistoryPath);
        if (first != null && paths.Contains(first.Path))
            return first.Path;

        string? supported = paths.FirstOrDefault(p => core.Supports(LinuxRetroArchContent.From(p).Extension));
        if (supported != null)
            return supported;

        return preferredPath != null && paths.Contains(preferredPath) ? preferredPath : paths[0];
    }

    /// <summary>
    /// The history is rewritten on every load, except when the content is already its first entry; the
    /// command line names what was launched but outlives "Close Content". So: a first entry written
    /// since RetroArch started, then the command line, then the first entry if its core is the loaded one.
    /// </summary>
    private static string? ChooseContent(LinuxRetroArchProcessInfo info, string coreFile)
    {
        var first = info.HistoryEnabled ? LinuxRetroArchHistory.First(info.ReadableHistoryPath) : null;
        bool firstMatches = first != null && first.Path.Length > 0 && CoreMatches(first.CorePath, coreFile);

        if (firstMatches && info.HistoryWrittenAfterStart())
            return first!.Path;

        if (info.CommandLineContent != null && (info.CommandLineCore == null || info.CommandLineCore == coreFile))
            return info.CommandLineContent;

        return firstMatches ? first!.Path : null;
    }

    private static bool CoreMatches(string? historyCorePath, string coreFile) =>
        string.IsNullOrEmpty(historyCorePath) || historyCorePath == "DETECT" || Path.GetFileName(historyCorePath) == coreFile;

    /// <summary>The loaded core, as RetroArch sees its path, or null in the menu.</summary>
    private static string? FindCore(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            {
                int start = line.IndexOf('/');
                if (start < 0)
                    continue;

                string path = line[start..];
                if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
                    path = path[..^" (deleted)".Length];

                if (path.EndsWith(CoreSuffix, StringComparison.Ordinal))
                    return path;
            }
        }
        catch
        {
            // Gone mid-read.
        }

        return null;
    }

    private DetectedGame Identify(int pid, LinuxRetroArchProcessInfo info, LibretroCoreInfo? core, LinuxRetroArchContent content)
    {
        var historyEntry = LinuxRetroArchHistory.Lookup(info.ReadableHistoryPath, content.FullPath, content.ArchivePath);
        var platforms = EmulatedPlatformResolver.Resolve(content.Extension, core, historyEntry?.DatabaseName);
        var names = RomNameCleaner.Clean(content.ArchivePath, content.InnerName, historyEntry?.Label);

        string coreName = core?.ShortName ?? "unknown";

        string? idIgdb = null;
        if (core != null && core.IsArcade)
        {
            _logger?.Info($"[LinuxRetroArchDetector] Core '{coreName}' is an arcade core, so '{names.Primary}' is a romset code and cannot be identified. The session will need the manual picker.");
        }
        else
        {
            idIgdb = _resolver.Resolve(platforms, names);
        }

        string? platformKey = platforms.Count > 0 ? platforms[0].Key : null;
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string platformLabel = platforms.Count > 0 ? string.Join(", ", platforms.Select(p => p.Key)) : "unknown";
        Console.WriteLine($"[LinuxRetroArchDetector] Content '{content.FullPath}' → '{name}' (platform: {platformLabel}, core: {coreName}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[LinuxRetroArchDetector] RetroArch (PID {pid}) is running '{name}' from '{content.FullPath}' (platform: {platformLabel}, core: {coreName}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)pid, idIgdb, DetectionSource.Emulator, content.FullPath, platformKey, Name);
    }
}
