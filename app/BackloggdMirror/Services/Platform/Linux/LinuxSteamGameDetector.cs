using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// Detects games started by Steam through their app id, which the database lists for three games in
/// four. It is what finds native Linux games: the database comes from Discord's Windows executables,
/// so "Megabonk.x86_64" is not in it, but app 3405340 is.
///
/// Steam starts every game, native or Proton, under "reaper SteamLaunch AppId=&lt;id&gt;", which lives as
/// long as the game and adopts its orphans. The reaper names the game; the session follows the game's
/// own process, found among the reaper's descendants as the first one run from the game's install folder
/// (the others are Steam's runtime container, Proton and Wine's system processes).
/// </summary>
internal sealed class LinuxSteamGameDetector
{
    private static readonly Regex InstallDirPattern = new("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);

    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private Dictionary<string, DetectableGame> _gamesByAppId = new();

    // Keyed by "<library>|<app id>"; a game's folder does not change while the app runs.
    private readonly Dictionary<string, string?> _installDirs = new();

    public LinuxSteamGameDetector(IAppLogger? logger, IDetectionBlacklist? blacklist)
    {
        _logger = logger;
        _blacklist = blacklist;
    }

    public void Load(IEnumerable<DetectableGame> games)
    {
        var index = new Dictionary<string, DetectableGame>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            foreach (var sku in game.ThirdPartySkus ?? new List<DetectableSku>())
            {
                if (sku.Distributor == "steam" && !string.IsNullOrEmpty(sku.Id))
                    index.TryAdd(sku.Id, game);
            }
        }

        _gamesByAppId = index;
        _installDirs.Clear();
        Console.WriteLine($"[LinuxSteamGameDetector] Indexed {index.Count} Steam app ids.");
    }

    public DetectedGame? Detect()
    {
        if (_gamesByAppId.Count == 0)
            return null;

        string? uid = LinuxProcFs.ReadUid(Environment.ProcessId);
        List<int> pids = LinuxProcFs.ListProcessIds();
        Dictionary<int, List<int>>? children = null;

        foreach (int pid in pids)
        {
            if (LinuxProcFs.ReadComm(pid) != "reaper")
                continue;

            string? appId = AppIdOf(pid);
            if (appId == null || !_gamesByAppId.TryGetValue(appId, out var game))
                continue;

            if (uid == null || LinuxProcFs.ReadUid(pid) != uid)
                continue;

            children ??= ChildrenOf(pids);
            int? gamePid = FindGameProcess(pid, appId, children, out string? path);

            // Still inside Steam's runtime setup, which takes a few seconds before the game starts.
            if (gamePid == null || path == null)
                continue;

            string processName = Path.GetFileNameWithoutExtension(LinuxProcFs.FileNameOf(path));
            if (_blacklist?.IsApplicationBlocked(gamePid.Value, processName) == true)
                continue;

            Console.WriteLine($"[LinuxSteamGameDetector] Steam app {appId} at '{path}' → game '{game.Name}' (IGDB: {game.IdIgdb ?? "null"})");
            _logger?.Info($"[LinuxSteamGameDetector] Steam app {appId} at '{path}' → game '{game.Name}' (IGDB: {game.IdIgdb ?? "null"}).");
            return new DetectedGame(game.Name, (uint)gamePid.Value, game.IdIgdb, DetectionSource.SteamApp);
        }

        return null;
    }

    /// <summary>The N of "reaper SteamLaunch AppId=N -- ...", or null for a reaper started some other way.</summary>
    private static string? AppIdOf(int reaperPid)
    {
        string[]? args = LinuxProcFs.ReadArgs(reaperPid);
        if (args == null || Array.IndexOf(args, "SteamLaunch") < 0)
            return null;

        foreach (string arg in args)
        {
            if (arg == "--")
                break;
            if (arg.StartsWith("AppId=", StringComparison.Ordinal))
                return arg["AppId=".Length..];
        }

        return null;
    }

    private static Dictionary<int, List<int>> ChildrenOf(List<int> pids)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (int pid in pids)
        {
            if (LinuxProcFs.ReadParentId(pid) is not int parent)
                continue;

            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<int>();
            list.Add(pid);
        }

        return children;
    }

    /// <summary>
    /// Breadth first, so a game's own helpers (a crash handler it starts) lose to the game itself.
    /// </summary>
    private int? FindGameProcess(int reaperPid, string appId, Dictionary<int, List<int>> children, out string? path)
    {
        var queue = new Queue<int>();
        queue.Enqueue(reaperPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            if (pid != reaperPid)
            {
                path = LinuxProcFs.TryGetImagePath(pid);
                if (path != null && IsInInstallDir(path, appId))
                    return pid;
            }

            if (children.TryGetValue(pid, out var list))
            {
                foreach (int child in list)
                    queue.Enqueue(child);
            }
        }

        path = null;
        return null;
    }

    /// <summary>
    /// Whether the path is inside the app's folder, as the library's appmanifest names it: the runtime and
    /// Proton also live in steamapps/common, so being there is not enough.
    /// </summary>
    private bool IsInInstallDir(string path, string appId)
    {
        const string common = "/steamapps/common/";
        int at = path.IndexOf(common, StringComparison.Ordinal);
        if (at < 0)
            return false;

        string rest = path[(at + common.Length)..];
        int slash = rest.IndexOf('/');
        if (slash <= 0)
            return false;

        string library = path[..at];
        string key = library + "|" + appId;
        if (!_installDirs.TryGetValue(key, out string? installDir))
        {
            installDir = ReadInstallDir(Path.Combine(library + "/steamapps", $"appmanifest_{appId}.acf"));
            _installDirs[key] = installDir;
        }

        // Wine keeps the case of the Windows path it was given, which need not be the folder's.
        return installDir != null && string.Equals(rest[..slash], installDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadInstallDir(string manifestPath)
    {
        try
        {
            var match = InstallDirPattern.Match(File.ReadAllText(manifestPath));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
