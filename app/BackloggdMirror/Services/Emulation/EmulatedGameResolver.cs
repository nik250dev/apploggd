using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services.Emulation;

/// <summary>
/// The cleaned name of a ROM turned into an IGDB id, scoped to its platform — plenty of titles
/// exist on both Game Boy and Game Boy Color, and logging the wrong one is worse than logging none.
/// Local databases first, then the search API; nulls are cached too, since detection polls.
/// </summary>
internal sealed class EmulatedGameResolver
{
    private const string ApiBaseUrl = "https://apploggd.nik250dev.workers.dev/api/v1/igdb";

    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger? _logger;

    public EmulatedGameResolver(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Synchronous to match the detection pass, exactly like <see cref="IgdbResolverService"/>.</summary>
    public string? Resolve(IReadOnlyList<EmulatedPlatform> platforms, RomNames names)
    {
        if (names.Names.Count == 0)
            return null;

        string cacheKey = $"{string.Join(",", platforms.Select(p => p.Key))}|{names.Primary}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        string? result = ResolveLocal(platforms, names) ?? ResolveRemote(platforms, names);

        _cache[cacheKey] = result;
        return result;
    }

    private string? ResolveLocal(IReadOnlyList<EmulatedPlatform> platforms, RomNames names)
    {
        string? bestId = null;
        string? bestName = null;
        string? bestPlatform = null;
        int bestLengthDiff = int.MaxValue;

        foreach (var platform in platforms)
        {
            if (!platform.HasLocalDatabase)
                continue;

            var index = EmulatedGamesDatabase.Instance.Get(platform.Key);
            if (index.NameIndex.Count == 0)
                continue;

            foreach (string name in names.Names)
            {
                string normalized = IgdbResolverService.NormalizeTitle(name).ToLowerInvariant();
                var (idIgdb, matchedName, exact, lengthDiff) = NameMatcher.FindBest(index.NameIndex, normalized);

                if (idIgdb == null)
                    continue;

                if (exact)
                {
                    Log($"[EmulatedGameResolver] Exact local match on '{platform.Key}': '{name}' → '{matchedName}' (IGDB: {idIgdb}).");
                    return idIgdb;
                }

                if (lengthDiff < bestLengthDiff)
                {
                    bestLengthDiff = lengthDiff;
                    bestId = idIgdb;
                    bestName = matchedName;
                    bestPlatform = platform.Key;
                }
            }
        }

        // Still an exact match once punctuation is gone, so it outranks any fuzzy one.
        foreach (var platform in platforms)
        {
            if (!platform.HasLocalDatabase)
                continue;

            var index = EmulatedGamesDatabase.Instance.Get(platform.Key);

            foreach (string name in names.Names)
            {
                string key = EmulatedGamesDatabase.LooseKey(IgdbResolverService.NormalizeTitle(name));
                if (key.Length > 0 && index.LooseIndex.TryGetValue(key, out string? idIgdb))
                {
                    Log($"[EmulatedGameResolver] Local match on '{platform.Key}' ignoring punctuation: '{name}' (IGDB: {idIgdb}).");
                    return idIgdb;
                }
            }
        }

        if (bestId != null)
        {
            Log($"[EmulatedGameResolver] Fuzzy local match on '{bestPlatform}': '{names.Primary}' → '{bestName}' (length diff: {bestLengthDiff}, IGDB: {bestId}). A wrong game logged for this session would start here.");
            return bestId;
        }

        // Dropping the subtitle is generic enough that only an exact hit can be trusted.
        if (names.WithoutSubtitle != null)
        {
            string normalized = IgdbResolverService.NormalizeTitle(names.WithoutSubtitle).ToLowerInvariant();

            foreach (var platform in platforms)
            {
                if (!platform.HasLocalDatabase)
                    continue;

                var index = EmulatedGamesDatabase.Instance.Get(platform.Key);
                var (idIgdb, matchedName, exact, _) = NameMatcher.FindBest(index.NameIndex, normalized);

                if (idIgdb != null && exact)
                {
                    Log($"[EmulatedGameResolver] Exact local match on '{platform.Key}' without subtitle: '{names.WithoutSubtitle}' → '{matchedName}' (IGDB: {idIgdb}).");
                    return idIgdb;
                }
            }
        }

        return null;
    }

    private string? ResolveRemote(IReadOnlyList<EmulatedPlatform> platforms, RomNames names)
    {
        string query = WebUtility.UrlEncode(names.Primary);
        var scoped = platforms.FirstOrDefault(p => p.IgdbPlatformId != null);

        if (scoped != null)
        {
            string? id = Query($"{ApiBaseUrl}/search/platform?query={query}&platform={scoped.IgdbPlatformId}", names.Primary, scoped.DisplayName);
            if (id != null)
                return id;
        }

        return Query($"{ApiBaseUrl}/search?query={query}", names.Primary, null);
    }

    private string? Query(string url, string name, string? platformName)
    {
        string scope = platformName != null ? $" on {platformName}" : " with no platform";

        try
        {
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Log($"[EmulatedGameResolver] The search API answered {(int)response.StatusCode} for '{name}'{scope}. The ROM stays unidentified for this session.", warning: true);
                return null;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var parsed = JsonSerializer.Deserialize<IgdbSearchResponse>(json);

            if (parsed?.IdIgdb != null)
            {
                Log($"[EmulatedGameResolver] API match for '{name}'{scope} → IGDB: {parsed.IdIgdb}.");
                return parsed.IdIgdb;
            }

            Log($"[EmulatedGameResolver] The search API knows no game for '{name}'{scope}.", warning: true);
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmulatedGameResolver] API error for '{name}'{scope}: {ex.Message}");
            _logger?.Error($"[EmulatedGameResolver] The search API call for '{name}'{scope} failed. The ROM stays unidentified for this session.", ex);
            return null;
        }
    }

    private void Log(string message, bool warning = false)
    {
        Console.WriteLine(message);

        if (warning)
            _logger?.Warning(message);
        else
            _logger?.Info(message);
    }
}
