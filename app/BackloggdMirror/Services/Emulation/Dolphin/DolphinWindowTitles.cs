using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation.Dolphin;

/// <summary>
/// Degraded mode, for a Dolphin whose memory cannot be read. While a game runs, Dolphin ends its
/// window titles with "Name (GAMEID)", or the bare ID when its database has no name for it. It
/// depends on the "Show Active Title in Window Title" setting and says nothing of the console.
/// </summary>
internal static class DolphinWindowTitles
{
    private static readonly Regex GameIdAtEnd = new(@"(?:\(([A-Z0-9]{6})\)|\| ([A-Z0-9]{6}))\s*$", RegexOptions.Compiled);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    public static DolphinDisc? FindDisc(int processId)
    {
        string? gameId = null;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != processId)
                return true;

            var title = new StringBuilder(512);
            if (GetWindowText(hWnd, title, title.Capacity) == 0)
                return true;

            var match = GameIdAtEnd.Match(title.ToString());
            if (!match.Success)
                return true;

            gameId = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return false;
        }, IntPtr.Zero);

        return gameId != null ? new DolphinDisc(gameId, null) : null;
    }
}
