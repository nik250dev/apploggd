using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.DuckStation;

/// <summary>
/// The running game's title as DuckStation shows it in the window that renders it: the main window
/// while it renders inside it, a separate window covering the monitor in fullscreen. The rest of the
/// time the main window reads "DuckStation 0.1-12070". Its other top-level windows (settings, memory
/// cards, log) render nothing, so they hold no native child and never cover the monitor.
/// </summary>
internal static class DuckStationWindows
{
    private const string AppName = "DuckStation";
    private const uint GW_OWNER = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>Null while no game runs, or while it renders to a separate window that is not fullscreen.</summary>
    public static string? FindGameTitle(int processId)
    {
        string? found = null;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != processId || !IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero || !IsQtWindow(hWnd))
                return true;

            string title = Title(hWnd);
            if (title.Length == 0 || IsAppTitle(title) || (!HasChild(hWnd) && !CoversMonitor(hWnd)))
                return true;

            found = title;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>"DuckStation 0.1-12070", plus " [Devel]" and the like on development builds.</summary>
    internal static bool IsAppTitle(string title) =>
        title.Equals(AppName, StringComparison.Ordinal) || title.StartsWith(AppName + " ", StringComparison.Ordinal);

    private static bool IsQtWindow(IntPtr hWnd)
    {
        var className = new StringBuilder(64);
        if (GetClassName(hWnd, className, className.Capacity) == 0)
            return false;

        string name = className.ToString();
        return name.StartsWith("Qt", StringComparison.Ordinal) && name.EndsWith("QWindowIcon", StringComparison.Ordinal);
    }

    private static string Title(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        return GetWindowText(hWnd, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    private static bool HasChild(IntPtr hWnd)
    {
        bool any = false;
        EnumChildWindows(hWnd, (_, _) =>
        {
            any = true;
            return false;
        }, IntPtr.Zero);
        return any;
    }

    private static bool CoversMonitor(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var window))
            return false;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref info))
            return false;

        return window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top
               && window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
    }
}
