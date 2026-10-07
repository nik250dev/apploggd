using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// Tier-2 detection for Linux: the games neither the executable nor the Steam app id identify, such
/// as a native game from outside Steam. Like the Windows tier it yields a window title, which
/// <see cref="IgdbResolverService"/> then turns into a game.
///
/// A window counts when its process looks like a game (<see cref="LinuxGameProcess"/>) and either the
/// window is fullscreen or the process runs on a game engine (Unity, Unreal, GameMaker...). That second case stands in
/// for the engine window classes Windows relies on, which X11 has no equivalent of: its classes are just
/// the program's name. Only X11 windows are seen, which under Wayland means XWayland (see <see cref="X11ClientWindows"/>).
/// </summary>
internal sealed class LinuxWindowGameDetector : IGameDetectionStrategy, IDisposable
{
    // A window can appear before the game loads its 3D API or its engine (Wine creates the window first),
    // so a verdict is only trusted for a while. Reading every windowed app's libraries on every pass would
    // also be wasteful: they are what changes least.
    private static readonly TimeSpan VerdictLifetime = TimeSpan.FromSeconds(30);

    // Bare versions that games put in their titles ("Factorio 2.0.77"). At least one dot, so that
    // "Doom 3" and "Half-Life 2" keep their numbers.
    private static readonly Regex TrailingVersion = new(@"\s+v?\d+(\.\d+)+[a-z]?\s*$", RegexOptions.IgnoreCase);

    private readonly IgdbResolverService _igdbResolver;
    private readonly IDetectionBlacklist? _blacklist;
    private readonly IAppLogger? _logger;
    private readonly X11ClientWindows _windows = new();
    private readonly Dictionary<int, (LinuxGameProcess.Verdict Verdict, DateTime Until)> _verdicts = new();

    public LinuxWindowGameDetector(IgdbResolverService igdbResolver, IDetectionBlacklist? blacklist, IAppLogger? logger)
    {
        _igdbResolver = igdbResolver;
        _blacklist = blacklist;
        _logger = logger;
    }

    public void ReloadDatabase() => _igdbResolver.ReloadDatabase();

    public bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb)
    {
        gameName = string.Empty;
        processId = 0;
        idIgdb = null;

        DateTime now = DateTime.UtcNow;
        foreach (int stale in _verdicts.Where(entry => entry.Value.Until <= now).Select(entry => entry.Key).ToList())
            _verdicts.Remove(stale);

        string? uid = LinuxProcFs.ReadUid(Environment.ProcessId);

        foreach (var (window, pid, fullscreen) in _windows.Windows())
        {
            if (pid <= 0 || pid == Environment.ProcessId)
                continue;

            bool fresh = !_verdicts.TryGetValue(pid, out var cached);
            if (fresh)
            {
                // The PID is whatever the client declared, and a remote X client's means nothing here.
                var classified = uid != null && LinuxProcFs.ReadUid(pid) == uid
                    ? LinuxGameProcess.Classify(pid, LinuxProcFs.TryGetImagePath(pid))
                    : new LinuxGameProcess.Verdict(false, null, "not the user's process");
                cached = (classified, now + VerdictLifetime);
                _verdicts[pid] = cached;
            }

            var verdict = cached.Verdict;
            if (!verdict.IsGame || !(fullscreen || verdict.Engine != null))
            {
                // Windowed apps are most of the desktop and are not worth a line each.
                if (fresh && fullscreen)
                    Console.WriteLine($"[LinuxWindowGameDetector] Fullscreen window of PID {pid}: not a game ({verdict.Reason})");
                continue;
            }

            string name = ExecutableNameOf(pid);
            if (_blacklist?.IsApplicationBlocked(pid, name) == true)
                continue;

            string title = _windows.Title(window);
            string how = fullscreen ? "Fullscreen window" : "Window";
            Console.WriteLine($"[LinuxWindowGameDetector] {how} '{title}' of '{name}' (PID {pid}) looks like a game ({verdict.Reason})");
            _logger?.Info($"[LinuxWindowGameDetector] {how} '{title}' of '{name}' (PID {pid}) looks like a game ({verdict.Reason}).");

            gameName = CleanTitle(title);
            if (gameName.Length == 0)
                gameName = name;

            processId = (uint)pid;
            idIgdb = _igdbResolver.ResolveIdIgdb(gameName);
            return true;
        }

        return false;
    }

    public void Dispose() => _windows.Dispose();

    // Without extension, as the blacklist compares it: "Megabonk" for Megabonk.x86_64.
    private static string ExecutableNameOf(int pid)
    {
        string? path = LinuxProcFs.TryGetImagePath(pid);
        return path == null ? string.Empty : Path.GetFileNameWithoutExtension(LinuxProcFs.FileNameOf(path));
    }

    internal static string CleanTitle(string title) => TrailingVersion.Replace(title, "").Trim();
}
