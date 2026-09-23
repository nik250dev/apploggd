using System;
using System.Collections.Generic;
using System.Linq;
using BackloggdMirror.Services.Emulation.RetroArch;

namespace BackloggdMirror.Services.Emulation;

/// <summary>
/// Which console a piece of content belongs to, crossing extension and core: neither is enough on
/// its own, since VBA-M covers three systems and .iso belongs to six.
/// </summary>
internal static class EmulatedPlatformResolver
{
    /// <summary>Order breaks ties; only unambiguous extensions go here.</summary>
    private static readonly EmulatedPlatform[] _platforms =
    {
        new("gba", "Game Boy Advance", 24, new[] { "gba" }, new[] { "game_boy_advance" }, new[] { "Nintendo - Game Boy Advance" }, true),
        new("gbc", "Game Boy Color", 22, new[] { "gbc", "cgb" }, new[] { "game_boy" }, new[] { "Nintendo - Game Boy Color" }, true),
        new("gb", "Game Boy", 33, new[] { "gb", "sgb", "dmg" }, new[] { "game_boy" }, new[] { "Nintendo - Game Boy" }, true),
        new("nds", "Nintendo DS", 20, new[] { "nds", "dsi", "ids" }, new[] { "nds" }, new[] { "Nintendo - Nintendo DS" }, true),
        new("3ds", "Nintendo 3DS", 37, new[] { "3ds", "cci", "cxi", "cia", "3dsx" }, new[] { "3ds" }, new[] { "Nintendo - Nintendo 3DS" }, true),
        new("n64", "Nintendo 64", 4, new[] { "n64", "z64", "v64", "ndd" }, new[] { "nintendo_64" }, new[] { "Nintendo - Nintendo 64" }, true),
        new("snes", "Super Nintendo", 19, new[] { "sfc", "smc", "fig", "swc", "bs", "st" }, new[] { "super_nes" }, new[] { "Nintendo - Super Nintendo Entertainment System" }, true),
        new("nes", "Nintendo Entertainment System", 18, new[] { "nes", "fds", "unf", "unif" }, new[] { "nes" }, new[] { "Nintendo - Nintendo Entertainment System" }, true),
        new("ngc", "GameCube", 21, new[] { "gcm", "gcz", "tgc", "dol" }, new[] { "gamecube" }, new[] { "Nintendo - GameCube" }, true),
        new("wii", "Wii", 5, new[] { "wbfs", "wad" }, new[] { "gamecube" }, new[] { "Nintendo - Wii" }, true),
        new("genesis", "Mega Drive/Genesis", 29, new[] { "md", "gen", "smd", "68k", "sgd", "32x" }, new[] { "mega_drive" }, new[] { "Sega - Mega Drive - Genesis", "Sega - 32X" }, true),
        new("gg", "Game Gear", 35, new[] { "gg" }, new[] { "mega_drive" }, new[] { "Sega - Game Gear" }, true),
        new("psx", "PlayStation", 7, new[] { "pbp", "ecm" }, new[] { "playstation" }, new[] { "Sony - PlayStation" }, true),
        new("ps2", "PlayStation 2", 8, Array.Empty<string>(), new[] { "playstation2" }, new[] { "Sony - PlayStation 2" }, true),
        new("psp", "PlayStation Portable", 38, new[] { "cso" }, new[] { "playstation_portable" }, new[] { "Sony - PlayStation Portable" }, true),
        new("dc", "Dreamcast", 23, new[] { "cdi", "gdi" }, new[] { "dreamcast" }, new[] { "Sega - Dreamcast" }, true),
        new("xbox", "Xbox", 11, new[] { "xbe" }, new[] { "xbox" }, new[] { "Microsoft - Xbox" }, true),
        new("ps3", "PlayStation 3", 9, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true),
        new("vita", "PlayStation Vita", 46, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true),
        new("wiiu", "Wii U", 41, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true),
        new("x360", "Xbox 360", 12, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true),
        new("sms", "Master System", 64, new[] { "sms" }, new[] { "master_system", "mega_drive" }, new[] { "Sega - Master System - Mark III" }, false),
        new("saturn", "Sega Saturn", 32, Array.Empty<string>(), new[] { "sega_saturn" }, new[] { "Sega - Saturn" }, false),
        new("pce", "PC Engine", 86, new[] { "pce", "sgx" }, new[] { "pc_engine" }, new[] { "NEC - PC Engine" }, false),
        new("vb", "Virtual Boy", 87, new[] { "vb", "vboy" }, new[] { "virtual_boy" }, new[] { "Nintendo - Virtual Boy" }, false),
        new("ws", "WonderSwan", 57, new[] { "ws", "wsc" }, new[] { "wonderswan" }, new[] { "Bandai - WonderSwan" }, false),
        new("arcade", "Arcade", 52, Array.Empty<string>(), new[] { "mame", "fb_alpha", "neogeo", "hbmame" }, new[] { "MAME", "FBNeo - Arcade Games", "FB Alpha" }, false)
    };

    /// <summary>Content extensions that name no platform on their own but must still pass the reader's filter.</summary>
    private static readonly HashSet<string> AmbiguousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "iso", "bin", "img", "cue", "chd", "ccd", "mds", "toc", "elf", "m3u", "nrg", "rvz", "zip", "7z"
    };

    private static readonly Dictionary<string, List<EmulatedPlatform>> _byExtension = BuildExtensionIndex();

    public static IReadOnlyList<EmulatedPlatform> All => _platforms;

    public static EmulatedPlatform? ByKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        return _platforms.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsContentExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        return _byExtension.ContainsKey(extension) || AmbiguousExtensions.Contains(extension);
    }

    /// <summary>Playlist name to platform; longest prefix wins, as "Nintendo - Game Boy" also prefixes Color and Advance.</summary>
    public static EmulatedPlatform? FromDatabaseName(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return null;

        string name = databaseName.Trim();
        if (name.EndsWith(".lpl", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        EmulatedPlatform? best = null;
        int bestLength = 0;

        foreach (var platform in _platforms)
        {
            foreach (string prefix in platform.DatabasePrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLength)
                {
                    best = platform;
                    bestLength = prefix.Length;
                }
            }
        }

        return best;
    }

    /// <summary>Candidates, most confident first; empty means unknown and identification goes unscoped.</summary>
    public static IReadOnlyList<EmulatedPlatform> Resolve(string? extension, LibretroCoreInfo? core, string? historyDatabaseName)
    {
        // RetroArch writes a single database only when the content came from a scanned playlist;
        // otherwise it copies the whole list the core supports, which names no platform in particular.
        var fromHistory = FromDatabaseNames(historyDatabaseName);
        if (fromHistory.Count == 1)
            return fromHistory;

        var fromExtension = FromExtension(extension);
        var fromCore = Order(FromCore(core).Concat(fromHistory));

        if (fromExtension.Count > 0 && fromCore.Count > 0)
        {
            var intersection = fromCore.Where(fromExtension.Contains).ToList();
            if (intersection.Count > 0)
                return intersection;
        }

        if (fromCore.Count > 0)
            return fromCore;

        return fromExtension;
    }

    private static List<EmulatedPlatform> FromDatabaseNames(string? databaseNames)
    {
        var matched = new HashSet<EmulatedPlatform>();

        if (!string.IsNullOrWhiteSpace(databaseNames))
        {
            foreach (string name in databaseNames.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var platform = FromDatabaseName(name);
                if (platform != null)
                    matched.Add(platform);
            }
        }

        return Order(matched);
    }

    /// <summary>Table order, so a candidate list is deterministic whatever order the sources came in.</summary>
    private static List<EmulatedPlatform> Order(IEnumerable<EmulatedPlatform> platforms)
    {
        var matched = new HashSet<EmulatedPlatform>(platforms);
        return _platforms.Where(matched.Contains).ToList();
    }

    private static List<EmulatedPlatform> FromExtension(string? extension)
    {
        if (!string.IsNullOrEmpty(extension) && _byExtension.TryGetValue(extension, out var platforms))
            return new List<EmulatedPlatform>(platforms);

        return new List<EmulatedPlatform>();
    }

    private static List<EmulatedPlatform> FromCore(LibretroCoreInfo? core)
    {
        if (core == null)
            return new List<EmulatedPlatform>();

        var systemIds = new HashSet<string>(core.SystemIds, StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<EmulatedPlatform>();

        foreach (string database in core.Databases)
        {
            var platform = FromDatabaseName(database);
            if (platform != null)
                matched.Add(platform);
        }

        foreach (var platform in _platforms)
        {
            if (platform.LibretroSystemIds.Any(systemIds.Contains))
                matched.Add(platform);
        }

        return Order(matched);
    }

    private static Dictionary<string, List<EmulatedPlatform>> BuildExtensionIndex()
    {
        var index = new Dictionary<string, List<EmulatedPlatform>>(StringComparer.OrdinalIgnoreCase);

        foreach (var platform in _platforms)
        {
            foreach (string extension in platform.Extensions)
            {
                if (!index.TryGetValue(extension, out var list))
                {
                    list = new List<EmulatedPlatform>();
                    index[extension] = list;
                }
                list.Add(platform);
            }
        }

        return index;
    }
}
