using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Platform;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>
/// Answers "is a game running right now?", polled every three seconds by the main ViewModel.
///
/// Two tiers, in this order because they differ in confidence: matching the process executable
/// against the local database is exact and yields the IGDB id for free, whereas the window
/// heuristic only produces a window <em>title</em> that still has to be identified afterwards.
/// </summary>
public class GameDetectionService : IGameDetectionService
{
    private readonly IGameDetectionStrategy _strategy;

    /// <summary>
    /// Index for quick lookup: exe name (lowercase) → list of candidate matches.
    /// Each candidate holds the game name, the expected parent directory segments (if any),
    /// and the IGDB ID.
    /// </summary>
    private Dictionary<string, List<ExeCandidate>> _exeIndex;

    private readonly IAppLogger? _logger;

    public GameDetectionService(IAppLogger? logger = null)
    {
        _logger = logger;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var igdbResolver = new IgdbResolverService(logger);
            _strategy = new WindowsGameDetector(igdbResolver);
        }
        else
        {
            // The window heuristic is built on user32 P/Invoke, so it has no equivalent elsewhere.
            // Tier 1 (executable name) still works, so the app degrades rather than breaks.
            _strategy = new NullGameDetector();
        }

        _exeIndex = BuildExeIndex();
    }

    /// <summary>
    /// Reloads the internal database from the local detectable_processed.json file.
    /// This is called when the file is updated in the background.
    /// </summary>
    public void ReloadDatabase()
    {
        _exeIndex = BuildExeIndex();
        _strategy.ReloadDatabase();
    }

    public bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb)
    {
        // Priority 1: Executable name matching against the JSON database
        if (TryDetectByExecutableName(out gameName, out processId, out idIgdb))
        {
            return true;
        }

        // Priority 2: Window class / fullscreen analysis (existing strategy)
        return _strategy.IsGameRunning(out gameName, out processId, out idIgdb);
    }

    /// <summary>
    /// Builds the exe-name index from detectable_processed.json.
    ///
    /// Prefers the copy in %LOCALAPPDATA%\Apploggd, which is the one the background updater
    /// refreshes, and falls back to the embedded resource so a first run (or an offline one) still
    /// detects games. A failure here disables tier-1 detection but must not throw: the window
    /// heuristic can still carry the app.
    /// </summary>
    private Dictionary<string, List<ExeCandidate>> BuildExeIndex()
    {
        var index = new Dictionary<string, List<ExeCandidate>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string jsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "detectable_processed.json");
            string jsonContent;

            if (File.Exists(jsonPath))
            {
                Console.WriteLine($"[GameDetectionService] Loading detectable_processed.json from disk: {jsonPath}");
                jsonContent = File.ReadAllText(jsonPath);
            }
            else
            {
                Console.WriteLine($"[GameDetectionService] detectable_processed.json not found on disk. Falling back to embedded Avalonia resource.");
                try
                {
                    var uri = new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/detectable_processed.json");
                    using var stream = AssetLoader.Open(uri);
                    using var reader = new StreamReader(stream);
                    jsonContent = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameDetectionService] Failed to load embedded detectable_processed.json: {ex.Message}");
                    _logger?.Error("[GameDetectionService] Neither the local nor the embedded detectable_processed.json could be read. Executable-based detection is disabled for this run.", ex);
                    return index;
                }
            }
            var games = JsonSerializer.Deserialize<List<DetectableGame>>(jsonContent);

            if (games == null)
            {
                Console.WriteLine("[GameDetectionService] Failed to parse detectable_processed.json. Exe-based detection disabled.");
                _logger?.Error("[GameDetectionService] detectable_processed.json parsed as null. Executable-based detection is disabled for this run.");
                return index;
            }

            foreach (var game in games)
            {
                if (game.Executables == null) continue;

                foreach (var exe in game.Executables)
                {
                    // Only consider Windows executables
                    if (!string.Equals(exe.Os, "win32", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip launchers (e.g., project8.exe for Deadlock is a launcher, not the game)
                    if (exe.IsLauncher)
                        continue;

                    string exeName = exe.Name;

                    // Entries starting with '>' are Discord's argument-based matching syntax, which
                    // this app does not implement.
                    if (exeName.StartsWith(">"))
                        continue;

                    // An entry can carry directory segments ("portal/hl2.exe"), which exist because
                    // generic exe names belong to several games and need the path to disambiguate.
                    string[] segments = exeName.Split('/');
                    string fileName = segments[^1];

                    string[] parentSegments = segments.Length > 1
                        ? segments[..^1]
                        : Array.Empty<string>();

                    string key = fileName.ToLowerInvariant();

                    if (!index.TryGetValue(key, out var candidates))
                    {
                        candidates = new List<ExeCandidate>();
                        index[key] = candidates;
                    }

                    candidates.Add(new ExeCandidate
                    {
                        GameName = game.Name,
                        IdIgdb = game.IdIgdb,
                        ExpectedParentSegments = parentSegments
                    });
                }
            }

            Console.WriteLine($"[GameDetectionService] Loaded exe index with {index.Count} unique executable names from detectable_processed.json.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameDetectionService] Error loading detectable_processed.json: {ex.Message}");
            _logger?.Error("[GameDetectionService] Building the executable index from detectable_processed.json failed. Executable-based detection is disabled for this run.", ex);
        }

        return index;
    }

    /// <summary>
    /// Scans the running processes for one whose executable name matches the database. Returns on
    /// the first hit, so with several games open the winner is whichever the OS happens to list
    /// first — acceptable because a session is a single game by definition.
    /// </summary>
    private bool TryDetectByExecutableName(out string gameName, out uint processId, out string? idIgdb)
    {
        gameName = string.Empty;
        processId = 0;
        idIgdb = null;

        if (_exeIndex.Count == 0)
            return false;

        int currentSessionId;
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            currentSessionId = currentProcess.SessionId;
        }
        catch
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    // Only this desktop session: services and other users' processes cannot be the
                    // game this user is playing, and reading them tends to be denied anyway.
                    if (process.SessionId != currentSessionId)
                        continue;

                    string processFileName = process.ProcessName + ".exe";

                    if (!_exeIndex.TryGetValue(processFileName.ToLowerInvariant(), out var candidates))
                        continue;

                    foreach (var candidate in candidates)
                    {
                        if (candidate.ExpectedParentSegments.Length == 0)
                        {
                            // Unambiguous exe name: the match needs no path check.
                            gameName = candidate.GameName;
                            processId = (uint)process.Id;
                            idIgdb = candidate.IdIgdb;
                            Console.WriteLine($"[GameDetectionService] EXE match: process '{processFileName}' → game '{candidate.GameName}' (IGDB: {candidate.IdIgdb ?? "null"})");
                            _logger?.Info($"[GameDetectionService] EXE match: process '{processFileName}' → game '{candidate.GameName}' (IGDB: {candidate.IdIgdb ?? "null"}).");
                            return true;
                        }

                        string? fullPath = null;
                        try
                        {
                            fullPath = process.MainModule?.FileName;
                        }
                        catch
                        {
                            // MainModule is denied for elevated or protected processes. Without the
                            // path the candidate cannot be confirmed, and guessing would risk
                            // logging the wrong game.
                            continue;
                        }

                        if (string.IsNullOrEmpty(fullPath))
                            continue;

                        if (MatchesPathSegments(fullPath, candidate.ExpectedParentSegments))
                        {
                            gameName = candidate.GameName;
                            processId = (uint)process.Id;
                            idIgdb = candidate.IdIgdb;
                            Console.WriteLine($"[GameDetectionService] EXE+Path match: process '{processFileName}' at '{fullPath}' → game '{candidate.GameName}' (IGDB: {candidate.IdIgdb ?? "null"})");
                            _logger?.Info($"[GameDetectionService] EXE+Path match: process '{processFileName}' at '{fullPath}' → game '{candidate.GameName}' (IGDB: {candidate.IdIgdb ?? "null"}).");
                            return true;
                        }
                    }
                }
                catch
                {
                    // A process can exit mid-iteration, which throws on any property access.
                }
            }
        }
        finally
        {
            // Process.GetProcesses hands out OS handles that leak without this, and on a repeating
            // poll the leak would be continuous.
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks that the expected directory segments sit immediately above the executable, in order.
    /// Anchored at the end rather than searched anywhere in the path, so that a game installed
    /// under an unrelated folder of the same name cannot produce a false match.
    /// ["portal"] matches "C:\Steam\steamapps\common\portal\hl2.exe"; ["a", "b"] requires the path
    /// to end in ...\a\b\exe.exe.
    /// </summary>
    private static bool MatchesPathSegments(string fullPath, string[] expectedSegments)
    {
        string normalizedPath = fullPath.Replace('/', '\\');
        string[] pathParts = normalizedPath.Split('\\');

        if (pathParts.Length < expectedSegments.Length + 1)
            return false;

        for (int i = 0; i < expectedSegments.Length; i++)
        {
            // The exe occupies the last slot, so the parent segments end just before it.
            int pathIndex = pathParts.Length - 1 - expectedSegments.Length + i;
            if (!string.Equals(pathParts[pathIndex], expectedSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Represents a candidate game match from the JSON database, associated with
/// a specific executable file name.
/// </summary>
internal class ExeCandidate
{
    public string GameName { get; set; } = string.Empty;
    public string? IdIgdb { get; set; }
    /// <summary>
    /// Expected parent directory segments (e.g., ["portal"] for "portal/hl2.exe").
    /// Empty array means no path verification is needed (direct exe name match).
    /// </summary>
    public string[] ExpectedParentSegments { get; set; } = Array.Empty<string>();
}

/// <summary>Tier-2 detection, swapped per platform.</summary>
internal interface IGameDetectionStrategy
{
    bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb);
    void ReloadDatabase();
}

/// <summary>Used where tier-2 detection has no implementation: always reports "no game".</summary>
internal class NullGameDetector : IGameDetectionStrategy
{
    public bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb)
    {
        gameName = string.Empty;
        processId = 0;
        idIgdb = null;
        return false;
    }

    public void ReloadDatabase()
    {
        // Nothing to do
    }
}
