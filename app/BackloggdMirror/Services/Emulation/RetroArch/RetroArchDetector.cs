using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>
/// RetroArch's detector. A session is bound to the pair (PID, content path), which is what lets
/// "Close Content" and swapping ROM end it where a PID check alone cannot. Opening and closing both
/// need the same reading twice, so one unlucky read cannot start or end a session on its own.
/// </summary>
internal sealed class RetroArchDetector : IEmulatorDetector
{
    private const string ProcessName = "retroarch";
    private const string CoreModuleSuffix = "_libretro.dll";
    private const int RequiredReadings = 2;

    private sealed class PidState
    {
        public string? LastPath;
        public int SameCount;
        public int MissCount;
        public bool DegradedWarningLogged;
        public bool CostLogged;
        public bool CommandLineOnlyLogged;
    }

    public string Name => "RetroArch";

    private readonly Dictionary<int, PidState> _states = new();
    private readonly EmulatedGameResolver _resolver;
    private readonly IAppLogger? _logger;

    public RetroArchDetector(EmulatedGameResolver resolver, IAppLogger? logger = null)
    {
        _resolver = resolver;
        _logger = logger;
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
                var content = ReadCurrentContent(process, null, out var processInfo, out var coreInfo);

                if (!Observe(process.Id, content?.FullPath))
                    continue;

                if (content == null || processInfo == null)
                    continue;

                return Identify(process, processInfo, coreInfo, content);
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
        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)game.ProcessId);
            if (process.HasExited || !IsRetroArch(process))
            {
                process.Dispose();
                return Closed(game);
            }
        }
        catch
        {
            try { process?.Dispose(); } catch { }
            return Closed(game);
        }

        try
        {
            var content = ReadCurrentContent(process, game.ContentKey, out _, out _);
            var state = StateFor(process.Id);

            if (content != null && string.Equals(content.FullPath, game.ContentKey, StringComparison.OrdinalIgnoreCase))
            {
                state.MissCount = 0;
                return true;
            }

            state.MissCount++;
            if (state.MissCount < RequiredReadings)
                return true;

            _logger?.Info($"[RetroArchDetector] Content '{game.ContentKey}' is no longer loaded in RetroArch (PID {game.ProcessId}). Ending the session.");
            return Closed(game);
        }
        finally
        {
            try { process?.Dispose(); } catch { }
        }
    }

    /// <summary>Windows reuses PIDs, so a live PID is not on its own proof that it is still the emulator.</summary>
    private static bool IsRetroArch(Process process)
    {
        try
        {
            return string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool Closed(DetectedGame game)
    {
        _states.Remove((int)game.ProcessId);
        RetroArchProcessInfo.Forget((int)game.ProcessId);
        return false;
    }

    /// <summary>Null when RetroArch sits in the menu. Memory is only read once a core is loaded, the cheap half of the signal.</summary>
    private RetroArchContentPath? ReadCurrentContent(Process process, string? preferredPath,
        out RetroArchProcessInfo? processInfo, out LibretroCoreInfo? coreInfo)
    {
        processInfo = null;
        coreInfo = null;

        string? coreModule = FindCoreModule(process, out bool accessDenied);
        if (coreModule == null && !accessDenied)
            return null;

        processInfo = RetroArchProcessInfo.For(process);
        var state = StateFor(process.Id);

        // With the module list denied the core is unknown, but the memory is still worth trying:
        // only an elevated RetroArch denies both.
        if (coreModule != null)
        {
            coreInfo = LibretroCoreInfo.Load(coreModule, processInfo);

            if (coreInfo.IsNonGame)
                return null;
        }

        try
        {
            var scan = RetroArchContentReader.ReadContentPaths(process);

            if (!state.CostLogged)
            {
                state.CostLogged = true;
                _logger?.Info($"[RetroArchDetector] Scanned {scan.BytesRead / 1024} KB of the RetroArch data sections (PID {process.Id}) in {scan.ElapsedMilliseconds} ms, {scan.Paths.Count} content path(s) found, {scan.Paths.Count(p => p.Embedded)} of them only inside the command line.");
            }

            if (scan.Paths.Count == 0)
                return null;

            // The command line names the ROM for as long as the process lives, so on its own it says
            // what was launched, never whether it is still loaded.
            var loaded = scan.Paths.Where(p => !p.Embedded).ToList();
            if (loaded.Count == 0)
            {
                if (!state.CommandLineOnlyLogged)
                {
                    state.CommandLineOnlyLogged = true;
                    _logger?.Info($"[RetroArchDetector] RetroArch (PID {process.Id}) only holds the ROM path from its own command line, which means no content is loaded.");
                }

                return null;
            }

            state.CommandLineOnlyLogged = false;
            return Choose(loaded, processInfo, coreInfo, preferredPath);
        }
        catch (RetroArchAccessDeniedException ex)
        {
            WarnDegraded(state, accessDenied
                ? $"[RetroArchDetector] RetroArch (PID {process.Id}) runs elevated: neither its module list nor its memory can be read."
                : $"[RetroArchDetector] {ex.Message}");

            return ReadFromHistory(process, processInfo, out coreInfo);
        }
    }

    private void WarnDegraded(PidState state, string reason)
    {
        if (state.DegradedWarningLogged)
            return;

        state.DegradedWarningLogged = true;
        _logger?.Warning($"{reason} Falling back to the content history: there will be a session, but closing the content will not end it — only closing RetroArch or loading another ROM will.");
    }

    /// <summary>
    /// Degraded mode: the history is only trusted when it was rewritten after this process started,
    /// since it otherwise describes a previous run. Its core_path stands in for the module list.
    /// </summary>
    private static RetroArchContentPath? ReadFromHistory(Process process, RetroArchProcessInfo processInfo, out LibretroCoreInfo? coreInfo)
    {
        coreInfo = null;

        var written = RetroArchHistoryReader.LastWriteTimeUtc(processInfo);
        if (written == null)
            return null;

        try
        {
            if (written <= process.StartTime.ToUniversalTime())
                return null;
        }
        catch
        {
            return null;
        }

        var entry = RetroArchHistoryReader.First(processInfo);
        if (entry == null)
            return null;

        if (!string.IsNullOrEmpty(entry.CorePath))
        {
            coreInfo = LibretroCoreInfo.Load(System.IO.Path.GetFileName(entry.CorePath), processInfo);
            if (coreInfo.IsNonGame)
                return null;
        }

        return ToContentPath(entry.Path);
    }

    private static RetroArchContentPath? ToContentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string full = RetroArchPath.Normalize(path);
        string archivePath = full;
        string? innerName = null;

        int hash = full.IndexOf('#');
        if (hash > 0)
        {
            archivePath = full[..hash];
            innerName = full[(hash + 1)..];
        }

        string extension = System.IO.Path.GetExtension(innerName ?? archivePath);
        extension = extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;

        return new RetroArchContentPath(full, archivePath, innerName, extension);
    }

    /// <summary>RetroArch keeps other paths around, so several candidates are ranked by how much they can be trusted.</summary>
    private static RetroArchContentPath Choose(IReadOnlyList<RetroArchContentPath> paths,
        RetroArchProcessInfo processInfo, LibretroCoreInfo? coreInfo, string? preferredPath)
    {
        if (paths.Count == 1)
            return paths[0];

        var mostRecent = RetroArchHistoryReader.First(processInfo);
        if (mostRecent != null)
        {
            var fromHistory = paths.FirstOrDefault(p => p.FullPath.Equals(mostRecent.Path, StringComparison.OrdinalIgnoreCase));
            if (fromHistory != null)
                return fromHistory;
        }

        var supported = coreInfo != null ? paths.FirstOrDefault(p => coreInfo.Supports(p.Extension)) : null;
        if (supported != null)
            return supported;

        if (preferredPath != null)
        {
            var current = paths.FirstOrDefault(p => p.FullPath.Equals(preferredPath, StringComparison.OrdinalIgnoreCase));
            if (current != null)
                return current;
        }

        return paths[0];
    }

    private DetectedGame Identify(Process process, RetroArchProcessInfo processInfo, LibretroCoreInfo? coreInfo, RetroArchContentPath content)
    {
        var historyEntry = RetroArchHistoryReader.Lookup(processInfo, content.FullPath);
        var platforms = EmulatedPlatformResolver.Resolve(content.Extension, coreInfo, historyEntry?.DatabaseName);
        var names = RomNameCleaner.Clean(content.ArchivePath, content.InnerName, historyEntry?.Label);

        string coreName = coreInfo?.ShortName ?? "unknown";

        string? idIgdb = null;
        if (coreInfo != null && coreInfo.IsArcade)
        {
            _logger?.Info($"[RetroArchDetector] Core '{coreName}' is an arcade core, so '{names.Primary}' is a romset code and cannot be identified. The session will need the manual picker.");
        }
        else
        {
            idIgdb = _resolver.Resolve(platforms, names);
        }

        string? platformKey = platforms.Count > 0 ? platforms[0].Key : null;
        string name = EmulatedGamesDatabase.Instance.FindByIgdbId(idIgdb)?.Name ?? names.Primary;

        string platformLabel = platforms.Count > 0 ? string.Join(", ", platforms.Select(p => p.Key)) : "unknown";
        Console.WriteLine($"[RetroArchDetector] Content '{content.FullPath}' → '{name}' (platform: {platformLabel}, core: {coreName}, IGDB: {idIgdb ?? "null"})");
        _logger?.Info($"[RetroArchDetector] RetroArch (PID {process.Id}) is running '{name}' from '{content.FullPath}' (platform: {platformLabel}, core: {coreName}, IGDB: {idIgdb ?? "null"}).");

        return new DetectedGame(name, (uint)process.Id, idIgdb, DetectionSource.Emulator, content.FullPath, platformKey, Name);
    }

    /// <summary>
    /// A null result with <paramref name="accessDenied"/> false means no core is loaded, which is
    /// RetroArch sitting in the menu. The two must not be confused: denied means "unknown".
    /// </summary>
    private static string? FindCoreModule(Process process, out bool accessDenied)
    {
        accessDenied = false;

        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName != null && module.ModuleName.EndsWith(CoreModuleSuffix, StringComparison.OrdinalIgnoreCase))
                    return module.ModuleName;
            }
        }
        catch (Win32Exception)
        {
            accessDenied = true;
        }
        catch
        {
            // The process exited mid-enumeration.
        }

        return null;
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

    private bool Observe(int processId, string? path)
    {
        var state = StateFor(processId);

        if (string.Equals(state.LastPath, path, StringComparison.OrdinalIgnoreCase))
        {
            state.SameCount++;
        }
        else
        {
            state.LastPath = path;
            state.SameCount = 1;
        }

        return path != null && state.SameCount >= RequiredReadings;
    }

    private void PruneStates(Process[] processes)
    {
        var alive = new HashSet<int>(processes.Select(p => p.Id));

        foreach (int processId in _states.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _states.Remove(processId);
            RetroArchProcessInfo.Forget(processId);
        }
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

            var found = Process.GetProcessesByName(ProcessName);
            var mine = new List<Process>();

            foreach (var process in found)
            {
                bool sameSession;
                try { sameSession = process.SessionId == sessionId; }
                catch { sameSession = false; }

                if (sameSession)
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
