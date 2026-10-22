using System;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>Whether the app runs under Gamescope, the compositor of SteamOS game mode.</summary>
internal static class LinuxGamescope
{
    // Gamescope exports GAMESCOPE_WAYLAND_DISPLAY to everything it launches; the SteamOS session also names the desktop.
    public static bool IsActive => OperatingSystem.IsLinux()
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
            || Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "gamescope");
}
