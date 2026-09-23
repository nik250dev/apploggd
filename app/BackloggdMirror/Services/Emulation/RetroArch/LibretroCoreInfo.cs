using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>
/// What the loaded core says about itself, from the &lt;core&gt;.info beside it. A table keyed on
/// the core name covers installs where that file is missing.
/// </summary>
internal sealed class LibretroCoreInfo
{
    /// <summary>Core cores that play media rather than games: they must never open a session.</summary>
    private static readonly string[] NonGameCores =
    {
        "mpv", "ffmpeg", "gme", "pocketcdg", "redbook", "imageviewer", "remotejoy",
        "advanced_tests", "dolphin_launcher"
    };

    private static readonly string[] NonGameSystemIds = { "movie", "music", "game_music", "adv_test_core" };

    private static readonly string[] ArcadeCores = { "mame", "fbneo", "fbalpha", "hbmame", "mess" };

    /// <summary>Longest prefix wins, so "mesen-s" is not read as "mesen" and "mednafen_psx" not as "mednafen".</summary>
    private static readonly (string Prefix, string[] SystemIds)[] FallbackSystems =
    {
        ("mgba", new[] { "game_boy_advance", "game_boy" }),
        ("vbam", new[] { "game_boy_advance", "game_boy" }),
        ("vba_next", new[] { "game_boy_advance" }),
        ("gpsp", new[] { "game_boy_advance" }),
        ("mednafen_gba", new[] { "game_boy_advance" }),
        ("gambatte", new[] { "game_boy" }),
        ("sameboy", new[] { "game_boy" }),
        ("tgbdual", new[] { "game_boy" }),
        ("gearboy", new[] { "game_boy" }),
        ("emux_gb", new[] { "game_boy" }),
        ("melonds", new[] { "nds" }),
        ("melondsds", new[] { "nds" }),
        ("desmume", new[] { "nds" }),
        ("desmume2015", new[] { "nds" }),
        ("noods", new[] { "nds" }),
        ("citra", new[] { "3ds" }),
        ("azahar", new[] { "3ds" }),
        ("panda3ds", new[] { "3ds" }),
        ("mupen64plus_next", new[] { "nintendo_64" }),
        ("parallel_n64", new[] { "nintendo_64" }),
        ("snes9x", new[] { "super_nes" }),
        ("bsnes", new[] { "super_nes" }),
        ("mesen-s", new[] { "super_nes" }),
        ("chimerasnes", new[] { "super_nes" }),
        ("mednafen_snes", new[] { "super_nes" }),
        ("mednafen_supafaust", new[] { "super_nes" }),
        ("fceumm", new[] { "nes" }),
        ("nestopia", new[] { "nes" }),
        ("mesen", new[] { "nes" }),
        ("quicknes", new[] { "nes" }),
        ("bnes", new[] { "nes" }),
        ("dolphin", new[] { "gamecube" }),
        ("ishiiruka", new[] { "gamecube" }),
        ("genesis_plus_gx", new[] { "mega_drive", "master_system" }),
        ("picodrive", new[] { "mega_drive", "master_system" }),
        ("blastem", new[] { "mega_drive" }),
        ("clownmdemu", new[] { "mega_drive" }),
        ("mednafen_psx", new[] { "playstation" }),
        ("pcsx_rearmed", new[] { "playstation" }),
        ("duckstation", new[] { "playstation" }),
        ("swanstation", new[] { "playstation" }),
        ("pcsx2", new[] { "playstation2" }),
        ("play", new[] { "playstation2" }),
        ("ppsspp", new[] { "playstation_portable" }),
        ("flycast", new[] { "dreamcast" }),
        ("directxbox", new[] { "xbox" })
    };

    public string ModuleName { get; }
    public string ShortName { get; }
    public string? DisplayName { get; }
    public IReadOnlyList<string> SystemIds { get; }
    public IReadOnlyList<string> SupportedExtensions { get; }
    public IReadOnlyList<string> Databases { get; }
    public bool IsNonGame { get; }
    public bool IsArcade { get; }

    private LibretroCoreInfo(string moduleName, string shortName, string? displayName, IReadOnlyList<string> systemIds,
        IReadOnlyList<string> supportedExtensions, IReadOnlyList<string> databases, bool isNonGame, bool isArcade)
    {
        ModuleName = moduleName;
        ShortName = shortName;
        DisplayName = displayName;
        SystemIds = systemIds;
        SupportedExtensions = supportedExtensions;
        Databases = databases;
        IsNonGame = isNonGame;
        IsArcade = isArcade;
    }

    private static readonly Dictionary<string, LibretroCoreInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();

    /// <summary>Cached for the life of the process: the .info of a core does not change while it is loaded, and the detector asks once per tick.</summary>
    public static LibretroCoreInfo Load(string moduleName, RetroArchProcessInfo processInfo)
    {
        string key = $"{processInfo.InfoDir}|{moduleName}";

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var built = Build(moduleName, processInfo);
            _cache[key] = built;
            return built;
        }
    }

    private static LibretroCoreInfo Build(string moduleName, RetroArchProcessInfo processInfo)
    {
        string baseName = moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? moduleName[..^4]
            : moduleName;

        string shortName = baseName.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase)
            ? baseName[..^"_libretro".Length]
            : baseName;

        string? displayName = null;
        var systemIds = new List<string>();
        var extensions = new List<string>();
        var databases = new List<string>();

        string? infoPath = processInfo.InfoDir != null ? Path.Combine(processInfo.InfoDir, baseName + ".info") : null;

        if (infoPath != null && File.Exists(infoPath))
        {
            var fields = RetroArchConfig.Parse(infoPath);

            if (fields.TryGetValue("display_name", out string? name))
                displayName = name;

            if (fields.TryGetValue("systemid", out string? systemId) && !string.IsNullOrWhiteSpace(systemId))
                systemIds.AddRange(Split(systemId));

            if (fields.TryGetValue("supported_extensions", out string? supported))
                extensions.AddRange(Split(supported));

            if (fields.TryGetValue("database", out string? database))
                databases.AddRange(Split(database));
        }

        if (systemIds.Count == 0)
        {
            systemIds.AddRange(FallbackFor(shortName));
        }

        bool isNonGame = NonGameCores.Any(c => shortName.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                         || systemIds.Any(id => NonGameSystemIds.Contains(id, StringComparer.OrdinalIgnoreCase));

        bool isArcade = ArcadeCores.Any(c => shortName.StartsWith(c, StringComparison.OrdinalIgnoreCase));

        return new LibretroCoreInfo(moduleName, shortName, displayName, systemIds, extensions, databases, isNonGame, isArcade);
    }

    public bool Supports(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;

        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] FallbackFor(string shortName)
    {
        string[]? best = null;
        int bestLength = 0;

        foreach (var (prefix, systemIds) in FallbackSystems)
        {
            if (shortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && prefix.Length > bestLength)
            {
                best = systemIds;
                bestLength = prefix.Length;
            }
        }

        return best ?? Array.Empty<string>();
    }

    private static IEnumerable<string> Split(string value)
    {
        return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
