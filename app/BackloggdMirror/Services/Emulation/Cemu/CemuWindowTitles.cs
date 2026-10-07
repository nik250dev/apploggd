using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation.Cemu;

/// <summary>The running title, as Cemu names it in its window. <paramref name="Name"/> is the short name, in the console language.</summary>
internal sealed record CemuTitle(string TitleId, string? Name);

/// <summary>
/// While a game runs Cemu titles its main window "Cemu 2.6 - FPS: 60.00 [Vulkan] [NVIDIA GPU]
/// [TitleId: 00050000-10144d00] Wii Sports Club [US v112]", with no setting to turn it off. The
/// title is not reset on "Stop emulation", so on its own it cannot tell that the game ended.
/// </summary>
internal static class CemuWindowTitles
{
    private static readonly Regex RunningTitle = new(
        @"\[TitleId: ([0-9a-f]{8})-([0-9a-f]{8})\](?: \[Online[^\]]*\])? (.*?)(?: \[(?:JP |US |EU )?v\d+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // What Cemu writes when a standalone .rpx or the title's metadata gives no name.
    private static readonly string[] PlaceholderNames = { "Unknown Game", "Unknown Title" };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    /// <summary>Null while Cemu sits in its game list or shows "loading...".</summary>
    public static CemuTitle? Find(int processId)
    {
        CemuTitle? found = null;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != processId)
                return true;

            var text = new StringBuilder(512);
            if (GetWindowText(hWnd, text, text.Capacity) == 0)
                return true;

            found = Parse(text.ToString());
            return found == null;
        }, IntPtr.Zero);

        return found;
    }

    internal static CemuTitle? Parse(string windowTitle)
    {
        var match = RunningTitle.Match(windowTitle);
        if (!match.Success)
            return null;

        string titleId = (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant();
        string name = match.Groups[3].Value.Trim();
        bool placeholder = name.Length == 0 || Array.Exists(PlaceholderNames, p => p.Equals(name, StringComparison.OrdinalIgnoreCase));

        return new CemuTitle(titleId, placeholder ? null : name);
    }
}
