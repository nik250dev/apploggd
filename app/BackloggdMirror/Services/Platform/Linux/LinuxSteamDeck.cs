using System;
using System.IO;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>Whether this is a Steam Deck (LCD "Jupiter" or OLED "Galileo"), read from the DMI data the firmware exposes.</summary>
internal static class LinuxSteamDeck
{
    private static readonly Lazy<bool> Detected = new(Detect);

    public static bool IsSteamDeck => OperatingSystem.IsLinux() && Detected.Value;

    private static bool Detect()
    {
        string? vendor = ReadDmi("sys_vendor");
        string? product = ReadDmi("product_name");
        return vendor == "Valve" && (product == "Jupiter" || product == "Galileo");
    }

    private static string? ReadDmi(string field)
    {
        try
        {
            return File.ReadAllText(Path.Combine("/sys/devices/virtual/dmi/id", field)).Trim();
        }
        catch
        {
            return null;
        }
    }
}
