using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services;

/// <summary>
/// Tier-2 detection for Windows: guesses whether a visible window belongs to a game, for titles
/// absent from the executable database.
///
/// Two signals, both heuristic: a window class belonging to a known game engine (strong, engines
/// register distinctive classes) and a window covering the whole monitor (weak, plenty of ordinary
/// apps go fullscreen). Because the second one is weak, the exclusion lists in
/// <see cref="IsExcludedApp"/> are deliberately aggressive — a false positive here logs playtime
/// for a game the user never opened, which is worse than missing the session entirely.
///
/// What comes out is the window <em>title</em>, not a canonical name, so it still has to go through
/// <see cref="IgdbResolverService"/> to become an identified game.
/// </summary>
internal class WindowsGameDetector : IGameDetectionStrategy
{
    // GetProcessById on every window, on every poll, is far too much work for data that
    // barely changes. Keyed by PID and pruned below once the window is gone.
    private readonly Dictionary<uint, string> _processNameCache = new Dictionary<uint, string>();
    private readonly IgdbResolverService _igdbResolver;

    public WindowsGameDetector(IgdbResolverService igdbResolver)
    {
        _igdbResolver = igdbResolver;
    }

    public void ReloadDatabase()
    {
        _igdbResolver.ReloadDatabase();
    }

    // P/Invoke definitions
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public bool IsGameRunning(out string gameName, out uint processId, out string? idIgdb)
    {
        uint foundProcessId = 0;
        string foundGameName = string.Empty;
        bool gameFound = false;
        idIgdb = null; // Will be resolved after detection via IgdbResolverService
        HashSet<uint> currentActivePids = new HashSet<uint>();

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
                return true; // Continue enumeration

            GetWindowThreadProcessId(hWnd, out uint currentWindowPid);
            currentActivePids.Add(currentWindowPid);

            // 1. Check Window Class Name
            StringBuilder classNameSb = new StringBuilder(256);
            GetClassName(hWnd, classNameSb, classNameSb.Capacity);
            string className = classNameSb.ToString();

            if (IsKnownGameClass(className))
            {
                if (!IsExcludedApp(hWnd, className))
                {
                    foundGameName = GetWindowTitle(hWnd);
                    GetWindowThreadProcessId(hWnd, out foundProcessId);
                    gameFound = true;
                    return false; // Stop enumeration
                }
            }

            // 2. Check Fullscreen/Borderless
            if (IsFullscreen(hWnd))
            {
                if (!IsExcludedApp(hWnd, className))
                {
                    foundGameName = GetWindowTitle(hWnd);
                    GetWindowThreadProcessId(hWnd, out foundProcessId);
                    gameFound = true;
                    return false; // Stop enumeration
                }
            }

            return true; // Continue enumeration
        }, IntPtr.Zero);

        // Drop cache entries for processes that no longer have windows. Only safe when nothing was
        // found: a hit aborts the enumeration early, so currentActivePids would be incomplete and
        // this would evict live processes.
        if (!gameFound)
        {
            var pidsToRemove = _processNameCache.Keys.Where(pid => !currentActivePids.Contains(pid)).ToList();
            foreach (var pid in pidsToRemove)
            {
                _processNameCache.Remove(pid);
            }
        }

        gameName = foundGameName;
        processId = foundProcessId;

        // Resolve IGDB ID from the window title using local fuzzy match + API fallback
        if (gameFound && !string.IsNullOrEmpty(foundGameName))
        {
            idIgdb = _igdbResolver.ResolveIdIgdb(foundGameName);
        }

        return gameFound;
    }

    private string GetWindowTitle(IntPtr hWnd)
    {
        StringBuilder titleSb = new StringBuilder(256);
        GetWindowText(hWnd, titleSb, titleSb.Capacity);
        return titleSb.ToString();
    }

    /// <summary>
    /// Window classes registered by game engines and a few specific titles. A match here is treated
    /// as a strong signal, since no ordinary application registers these.
    /// </summary>
    private bool IsKnownGameClass(string className)
    {
        string[] gameClasses = {
            "UnrealWindow", // Unreal Engine 4/5
            "UnityWndClass", // Unity Engine
            "LaunchUnrealUWindowsClient", // Unreal Engine (Shipping builds)
            "Valve001", // Source Engine
            "SDL_app", // SDL2 (many indie games)
            "GLFW30", // GLFW (Minecraft)
            "YYGameMakerYY", // GameMaker
            "CryENGINE", // CryEngine
            "RiotWindowClass", // Riot Games (League of Legends, Valorant)
            "Respawn001", // Apex Legends / Titanfall
            "D3D Window", // Generic D3D
            "Win32Window0", // Generic
            "TankWindow", // Overwatch
            "Godot_Window", // Godot
            "FFXIVGAME", // Final Fantasy XIV
            "DarkSouls", // Dark Souls
            "Sekiro", // Sekiro
            "EldenRing", // Elden Ring
            "GrindStone", // GrindStone
            "UnitySecondaryWndClass", // Unity Secondary
            "CIrrDeviceWin32", // Irrlicht
            "OgreD3D9Wnd", // Ogre3D
            "TVPMainWindow" // Kirikiri
        };
        foreach (var cls in gameClasses)
        {
            if (className.Equals(cls, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the window covers its monitor entirely. Uses &gt;= on the size and &lt;= on the
    /// origin rather than exact equality, because borderless-fullscreen windows commonly overhang
    /// the monitor bounds by a pixel or two.
    /// </summary>
    private bool IsFullscreen(IntPtr hWnd)
    {
        if (GetWindowRect(hWnd, out RECT rect))
        {
            IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                MONITORINFO monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

                if (GetMonitorInfo(hMonitor, ref monitorInfo))
                {
                    int monitorWidth = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
                    int monitorHeight = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;
                    int windowWidth = rect.Right - rect.Left;
                    int windowHeight = rect.Bottom - rect.Top;
                    return windowWidth >= monitorWidth &&
                           windowHeight >= monitorHeight &&
                           rect.Left <= monitorInfo.rcMonitor.Left &&
                           rect.Top <= monitorInfo.rcMonitor.Top;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Rejects windows that are not games. This is what keeps the fullscreen rule usable: a
    /// maximised browser, a video player or the Windows shell all satisfy it, so both the window
    /// class and the owning process name are checked against exclusion lists.
    /// Launchers are excluded too — the storefront is not the game it starts.
    /// </summary>
    private bool IsExcludedApp(IntPtr hWnd, string className)
    {
        string[] excludedClasses = {
            // Browsers & Web Views
            "Chrome_WidgetWin_1", "MozillaWindowClass", "IEFrame", "OpWindow", "CefBrowserWindow",
            
            // Windows System & Shell
            "ApplicationFrameWindow", "CabinetWClass", "Progman", "WorkerW", "Shell_TrayWnd",
            "Windows.UI.Core.CoreWindow", "ConsoleWindowClass", "TaskManagerWindow",
            "OperationStatusWindow", "NativeHWNDHost", "DirectUIHWND",
            
            // Common Frameworks/Wrappers (Generic windows that aren't games)
            "HwndWrapper", "GDI+ Hook Window Class", "Qt5QWindowIcon", "Qt6QWindowIcon"
        };

        foreach (var cls in excludedClasses)
        {
            // Exact match, except for HwndWrapper: WPF appends a per-instance suffix to it.
            if (className.Equals(cls, StringComparison.OrdinalIgnoreCase) ||
                (cls == "HwndWrapper" && className.StartsWith("HwndWrapper", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        // The class alone is not enough: Electron and Qt apps share their class with real games.
        GetWindowThreadProcessId(hWnd, out uint processId);

        if (!_processNameCache.TryGetValue(processId, out string? processName) || processName == null)
        {
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    processName = process.ProcessName ?? string.Empty;
                }
            }
            catch
            {
                processName = string.Empty; // Ignore access errors
            }
            // Cached even when empty, so a denied process is not retried on every poll.
            _processNameCache[processId] = processName;
        }

        // Unknown process name: nothing to exclude on, so let the class-based verdict stand.
        if (string.IsNullOrEmpty(processName)) return false;

        string[] excludedProcesses = { 
            // Browsers
            "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore",
            
            // Windows System & Utilities
            "explorer", "SearchApp", "TextInputHost", "SnippingTool", "Taskmgr",
            "SystemSettings", "ApplicationFrameHost", "Calculator", "Notepad", "notepad++",
            "cmd", "powershell", "pwsh", "conhost", "csrss", "svchost", "RuntimeBroker",
            "LockApp", "Widgets", "Nvidia Share", "RadeonSoftware",
            
            // Game Launchers (The launcher itself is not the game)
            "steam", "steamwebhelper", "EpicGamesLauncher", "EADesktop", "Origin",
            "UbisoftConnect", "GalaxyClient", "Battle.net", "RiotClientServices",
            "itch", "Amazon Games UI",
            
            // Communication & Media
            "Discord", "Slack", "Teams", "Zoom", "Skype", "Spotify",
            "Obs64", "obs64", "vlc", "mpv", "Telegram", "WhatsApp",
            
            // Development Tools
            "Code", "devenv", "rider64", "studio64", "Unity Hub", "Godot"
        };

        foreach (var proc in excludedProcesses)
        {
            if (processName.Equals(proc, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
