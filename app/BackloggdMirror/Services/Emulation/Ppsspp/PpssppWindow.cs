using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>
/// PPSSPP's main window and the message it answers for external tools since 1.20:
/// WM_USER_GET_CURRENT_GAMEID returns "DISC_ID_DISC_VERSION" from the PARAM.SFO while a game is
/// booted, paused included, and nothing once it shuts down. Older versions leave the message to
/// DefWindowProc, which answers 0 as if no game were running.
/// </summary>
internal static class PpssppWindow
{
    private const string MainWindowClass = "PPSSPPWnd";
    private const uint WM_USER_GET_CURRENT_GAMEID = 0x8000 + 0x311A;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const uint MessageTimeoutMs = 250;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    public static IntPtr Find(int processId)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != processId)
                return true;

            var className = new StringBuilder(64);
            if (GetClassName(hWnd, className, className.Capacity) == 0 || className.ToString() != MainWindowClass)
                return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    public static string Title(IntPtr window)
    {
        var text = new StringBuilder(512);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    /// <summary>
    /// Empty when no game is booted or the version predates the message; null when PPSSPP did not
    /// answer, as happens when it runs elevated and UIPI drops the message.
    /// </summary>
    public static string? GameId(IntPtr window)
    {
        var id = new StringBuilder(16);

        // Four characters per call, packed little-endian.
        for (int chunk = 0; chunk < 4; chunk++)
        {
            if (SendMessageTimeout(window, WM_USER_GET_CURRENT_GAMEID, new IntPtr(chunk), IntPtr.Zero,
                    SMTO_ABORTIFHUNG, MessageTimeoutMs, out IntPtr result) == IntPtr.Zero)
                return null;

            uint packed = (uint)result.ToInt64();
            for (int i = 0; i < 4; i++)
            {
                char c = (char)((packed >> (i * 8)) & 0xFF);
                if (c == '\0')
                    return id.ToString();

                id.Append(c);
            }
        }

        return id.ToString();
    }
}
