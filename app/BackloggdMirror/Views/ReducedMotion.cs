using System;
using System.Runtime.InteropServices;

namespace BackloggdMirror.Views;

/// <summary>The system's "show animations" switch, read when needed so a change applies to the next modal.</summary>
internal static class ReducedMotion
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [MarshalAs(UnmanagedType.Bool)] ref bool pvParam, uint fWinIni);

    // Linux has no single switch to read cheaply here, so it animates.
    public static bool IsOn
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                bool animate = true;
                return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref animate, 0) && !animate;
            }
            catch
            {
                return false;
            }
        }
    }
}
