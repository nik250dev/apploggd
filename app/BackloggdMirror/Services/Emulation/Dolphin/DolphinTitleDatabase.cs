using System;
using System.IO;

namespace BackloggdMirror.Services.Emulation.Dolphin;

/// <summary>
/// Game ID to title, from the GameTDB list every Dolphin build ships in Sys. Always the English
/// file, whatever language Dolphin runs in, because that is the one whose names match IGDB; the
/// names inside the disc header are internal codenames ("SPORTS PACK for REVOLUTION").
/// </summary>
internal static class DolphinTitleDatabase
{
    private const string FileName = "wiitdb-en.txt";

    public static string? FindName(string? dolphinDir, string gameId)
    {
        if (string.IsNullOrEmpty(dolphinDir))
            return null;

        string path = Path.Combine(dolphinDir, "Sys", FileName);
        if (!File.Exists(path))
            return null;

        string prefix = gameId + " = ";

        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string name = line[prefix.Length..].Trim();
            return name.Length > 0 ? name : null;
        }

        return null;
    }
}
