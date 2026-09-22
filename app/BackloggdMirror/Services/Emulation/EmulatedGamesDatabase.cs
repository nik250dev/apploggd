using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia.Platform;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation;

/// <summary>
/// The per-platform game databases (detectable_emu_&lt;key&gt;.json), indexed by name and by IGDB id.
/// Loaded one platform at a time on first use: together they are about ten megabytes.
/// </summary>
public sealed class EmulatedGamesDatabase
{
    public static EmulatedGamesDatabase Instance { get; } = new();

    private readonly Dictionary<string, PlatformIndex> _platforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IAppLogger? Logger { get; set; }

    private EmulatedGamesDatabase()
    {
    }

    internal sealed class PlatformIndex
    {
        public List<(string NormalizedName, string? IdIgdb)> NameIndex { get; } = new();
        public Dictionary<string, DetectableGame> IgdbIndex { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads a platform database on first use. Never throws: a failure means an empty index.</summary>
    internal PlatformIndex Get(string key)
    {
        lock (_lock)
        {
            if (_platforms.TryGetValue(key, out var cached))
                return cached;

            var index = Build(key);
            _platforms[key] = index;
            return index;
        }
    }

    /// <summary>Only searches platforms already loaded, which is where an emulated session's id came from.</summary>
    public DetectableGame? FindByIgdbId(string? idIgdb)
    {
        if (string.IsNullOrEmpty(idIgdb))
            return null;

        lock (_lock)
        {
            foreach (var platform in _platforms.Values)
            {
                if (platform.IgdbIndex.TryGetValue(idIgdb, out var game))
                    return game;
            }
        }

        return null;
    }

    private PlatformIndex Build(string key)
    {
        var index = new PlatformIndex();

        try
        {
            var uri = new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/detectable_emu_{key}.json");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);

            var games = JsonSerializer.Deserialize<List<DetectableGame>>(reader.ReadToEnd());

            if (games == null)
            {
                Logger?.Error($"[EmulatedGamesDatabase] detectable_emu_{key}.json parsed as null. Games for that platform can only be identified through the API.");
                return index;
            }

            foreach (var game in games)
            {
                if (string.IsNullOrEmpty(game.IdIgdb))
                    continue;

                index.IgdbIndex.TryAdd(game.IdIgdb, game);

                string normalizedName = IgdbResolverService.NormalizeTitle(game.Name);
                if (!string.IsNullOrWhiteSpace(normalizedName))
                    index.NameIndex.Add((normalizedName, game.IdIgdb));

                if (game.Aliases == null)
                    continue;

                foreach (string alias in game.Aliases)
                {
                    string normalizedAlias = IgdbResolverService.NormalizeTitle(alias);
                    if (!string.IsNullOrWhiteSpace(normalizedAlias))
                        index.NameIndex.Add((normalizedAlias, game.IdIgdb));
                }
            }

            Console.WriteLine($"[EmulatedGamesDatabase] Loaded '{key}' with {index.IgdbIndex.Count} games and {index.NameIndex.Count} searchable names.");
            Logger?.Info($"[EmulatedGamesDatabase] Loaded '{key}' with {index.IgdbIndex.Count} games and {index.NameIndex.Count} searchable names.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmulatedGamesDatabase] Could not load detectable_emu_{key}.json: {ex.Message}");
            Logger?.Error($"[EmulatedGamesDatabase] Could not load detectable_emu_{key}.json. Games for that platform can only be identified through the API.", ex);
        }

        return index;
    }
}
