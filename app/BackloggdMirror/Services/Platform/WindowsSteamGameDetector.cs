using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Platform;

/// <summary>
/// Detects Steam games by app id, read from the path: a process run from "&lt;library&gt;\steamapps\common\&lt;folder&gt;"
/// belongs to the app whose appmanifest installs it there. The session follows the game's topmost process.
/// </summary>
internal sealed class WindowsSteamGameDetector : ISteamGameDetector
{
    private const string Common = @"\steamapps\common\";

    private readonly IAppLogger? _logger;
    private readonly IDetectionBlacklist? _blacklist;
    private Dictionary<string, DetectableGame> _gamesByAppId = new();

    // Keyed by "<library>|<folder>", null for a folder no manifest names. A game is installed before it runs.
    private readonly Dictionary<string, string?> _appIds = new(StringComparer.OrdinalIgnoreCase);

    // Reading every process path on every pass would be waste; the exe name guards against a recycled PID.
    private Dictionary<uint, (string ExeName, string? Path)> _paths = new();

    public WindowsSteamGameDetector(IAppLogger? logger, IDetectionBlacklist? blacklist)
    {
        _logger = logger;
        _blacklist = blacklist;
    }

    public void Load(IEnumerable<DetectableGame> games)
    {
        _gamesByAppId = SteamApps.IndexByAppId(games);
        _appIds.Clear();
        Console.WriteLine($"[WindowsSteamGameDetector] Indexed {_gamesByAppId.Count} Steam app ids.");
    }

    public DetectedGame? Detect()
    {
        if (_gamesByAppId.Count == 0)
            return null;

        var processes = WindowsProcessSnapshot.Take();
        if (processes == null || !ProcessIdToSessionId(GetCurrentProcessId(), out uint ownSession))
            return null;

        var paths = new Dictionary<uint, (string ExeName, string? Path)>(processes.Count);
        var hits = new List<(WindowsProcessSnapshot.Entry Process, string AppId, string Path)>();

        foreach (var process in processes)
        {
            string? path = _paths.TryGetValue(process.Id, out var cached) && cached.ExeName == process.ExeName
                ? cached.Path
                : ProcessImagePath.TryGet((int)process.Id);
            paths[process.Id] = (process.ExeName, path);

            if (path == null || IsRedistributable(path))
                continue;

            string? appId = AppIdOf(path);
            if (appId == null || !_gamesByAppId.ContainsKey(appId))
                continue;

            if (!ProcessIdToSessionId(process.Id, out uint session) || session != ownSession)
                continue;

            if (_blacklist?.IsApplicationBlocked((int)process.Id, Path.GetFileNameWithoutExtension(process.ExeName)) == true)
                continue;

            hits.Add((process, appId, path));
        }

        _paths = paths;

        foreach (var hit in hits)
        {
            if (hits.Any(other => other.Process.Id == hit.Process.ParentId && other.AppId == hit.AppId))
                continue;

            var game = _gamesByAppId[hit.AppId];
            Console.WriteLine($"[WindowsSteamGameDetector] Steam app {hit.AppId} at '{hit.Path}' → game '{game.Name}' (IGDB: {game.IdIgdb ?? "null"})");
            _logger?.Info($"[WindowsSteamGameDetector] Steam app {hit.AppId} at '{hit.Path}' → game '{game.Name}' (IGDB: {game.IdIgdb ?? "null"}).");
            return new DetectedGame(game.Name, hit.Process.Id, game.IdIgdb, DetectionSource.SteamApp);
        }

        return null;
    }

    // Steam runs these from the game's folder on its first launch, before the game itself.
    private static bool IsRedistributable(string path) =>
        path.Contains(@"\_CommonRedist\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Engine\Extras\Redist\", StringComparison.OrdinalIgnoreCase);

    private string? AppIdOf(string path)
    {
        int at = path.IndexOf(Common, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        string rest = path[(at + Common.Length)..];
        int slash = rest.IndexOf('\\');
        if (slash <= 0)
            return null;

        string library = path[..at];
        string key = library + "|" + rest[..slash];
        if (!_appIds.ContainsKey(key))
        {
            ReadLibrary(library);
            _appIds.TryAdd(key, null);
        }

        return _appIds[key];
    }

    private void ReadLibrary(string library)
    {
        try
        {
            foreach (string manifest in Directory.EnumerateFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"))
            {
                string appId = Path.GetFileNameWithoutExtension(manifest)["appmanifest_".Length..];
                string? installDir = SteamApps.ReadInstallDir(manifest);
                if (installDir != null)
                    _appIds[library + "|" + installDir] = appId;
            }
        }
        catch
        {
            // An unreadable library names no app; its processes stay with the other tiers.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
