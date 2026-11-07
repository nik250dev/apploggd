using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>Tier 1.2: Steam games by app id, for the ones the executable database does not list.</summary>
internal interface ISteamGameDetector
{
    void Load(IEnumerable<DetectableGame> games);

    DetectedGame? Detect();
}

/// <summary>What the Steam app id tiers of Windows and Linux share: the database index and the library manifests.</summary>
internal static class SteamApps
{
    private static readonly Regex InstallDirPattern = new("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);

    public static Dictionary<string, DetectableGame> IndexByAppId(IEnumerable<DetectableGame> games)
    {
        var index = new Dictionary<string, DetectableGame>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            foreach (var sku in game.ThirdPartySkus ?? new List<DetectableSku>())
            {
                if (sku.Distributor == "steam" && !string.IsNullOrEmpty(sku.Id))
                    index.TryAdd(sku.Id, game);
            }
        }

        return index;
    }

    /// <summary>The folder under steamapps/common that an appmanifest_&lt;id&gt;.acf installs its app in.</summary>
    public static string? ReadInstallDir(string manifestPath)
    {
        try
        {
            var match = InstallDirPattern.Match(File.ReadAllText(manifestPath));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
