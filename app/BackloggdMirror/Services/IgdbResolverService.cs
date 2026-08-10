using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>
/// Turns a window title into an IGDB id — the step that makes a tier-2 detection actually usable.
///
/// Three stages ordered by cost, since this runs on every detection: an in-memory cache, then fuzzy
/// matching against the local database, and only then a network call. Window titles are messy input
/// (decorations, version suffixes, episode names), so an exact match is the exception, not the rule.
/// </summary>
internal class IgdbResolverService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Index of normalized game names/aliases → id_igdb for local fuzzy matching.
    /// </summary>
    private List<(string NormalizedName, string? IdIgdb)> _nameIndex;

    /// <summary>
    /// Resolved window titles → id_igdb. Keyed on the raw title, so no normalization is repeated.
    /// Failures are cached as null on purpose: detection polls every second, and an unidentifiable
    /// game would otherwise hit the API once per second for as long as it stays open.
    /// </summary>
    private readonly Dictionary<string, string?> _resolvedCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IAppLogger? _logger;

    public IgdbResolverService(IAppLogger? logger = null)
    {
        _logger = logger;
        _nameIndex = BuildNameIndex();
    }

    /// <summary>
    /// Reloads the internal database from the local detectable_processed.json file.
    /// This is called when the file is updated in the background.
    /// </summary>
    public void ReloadDatabase()
    {
        _nameIndex = BuildNameIndex();

        // The cache holds verdicts derived from the old data, including nulls for games the update
        // may have just added.
        _resolvedCache.Clear();
    }

    /// <summary>
    /// Resolves the IGDB id for a window title, or null if it cannot be identified.
    /// Synchronous by necessity: the caller is <c>IGameDetectionStrategy.IsGameRunning</c>, which is
    /// itself synchronous. Safe because the whole detection pass runs off the UI thread.
    /// </summary>
    public string? ResolveIdIgdb(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return null;

        // 1. Check cache first
        if (_resolvedCache.TryGetValue(windowTitle, out string? cachedResult))
        {
            return cachedResult;
        }

        string? result = null;

        // 2. Try local fuzzy matching
        result = TryMatchLocal(windowTitle);

        // 3. Fallback to external API if no local match
        if (result == null)
        {
            result = TryMatchApi(windowTitle);
        }

        _resolvedCache[windowTitle] = result;

        return result;
    }

    /// <summary>
    /// Strips the decoration a window title carries but a database name does not: trademark symbols,
    /// version and build suffixes, and trailing bracketed tags. Applied to both sides of every
    /// comparison, so the two are always normalized the same way.
    /// </summary>
    internal static string NormalizeTitle(string title)
    {
        string normalized = title
            .Replace("™", "")
            .Replace("®", "")
            .Replace("©", "");

        // Remove version suffixes like " v1.2.3", " v2.15.374", " Build 12345"
        normalized = Regex.Replace(normalized, @"\s+v\d+[\d.]*\s*$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+Build\s+\d+\s*$", "", RegexOptions.IgnoreCase);

        // Remove trailing content in parentheses or brackets: " (Early Access)", " [64-bit]"
        normalized = Regex.Replace(normalized, @"\s*[\(\[][^\)\]]*[\)\]]\s*$", "");

        // Collapse multiple spaces and trim
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        return normalized;
    }

    /// <summary>
    /// Matches the title against the local index. Tried before the API because it is free, offline
    /// and covers the overwhelming majority of games.
    /// </summary>
    private string? TryMatchLocal(string windowTitle)
    {
        string normalizedInput = NormalizeTitle(windowTitle).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedInput))
            return null;

        // Pass 1: Exact match (case-insensitive, already lowered)
        foreach (var (normalizedName, idIgdb) in _nameIndex)
        {
            if (normalizedName.Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[IgdbResolverService] Exact local match: '{windowTitle}' → '{normalizedName}' (IGDB: {idIgdb ?? "null"})");
                _logger?.Info($"[IgdbResolverService] Exact local match: '{windowTitle}' → '{normalizedName}' (IGDB: {idIgdb ?? "null"}).");
                return idIgdb;
            }
        }

        // Pass 2: containment, which catches the common shapes an exact match misses — a title
        // carrying an edition suffix, or a window naming the current level after the game.
        // The closest length wins, since that is the candidate with the least unexplained text.
        string? bestIdIgdb = null;
        int bestLengthDiff = int.MaxValue;
        string? bestMatchName = null;

        foreach (var (normalizedName, idIgdb) in _nameIndex)
        {
            string nameLower = normalizedName.ToLowerInvariant();

            bool inputContainsName = normalizedInput.Contains(nameLower);
            bool nameContainsInput = nameLower.Contains(normalizedInput);

            if (inputContainsName || nameContainsInput)
            {
                int lengthDiff = Math.Abs(normalizedInput.Length - nameLower.Length);
                int maxLength = Math.Max(normalizedInput.Length, nameLower.Length);

                // Cap the unexplained text at 30% of the longer string. Without it, a short name
                // like "Rust" would match any title that merely contains it.
                if (maxLength > 0 && (double)lengthDiff / maxLength <= 0.30)
                {
                    if (lengthDiff < bestLengthDiff)
                    {
                        bestLengthDiff = lengthDiff;
                        bestIdIgdb = idIgdb;
                        bestMatchName = normalizedName;
                    }
                }
            }
        }

        if (bestIdIgdb != null)
        {
            Console.WriteLine($"[IgdbResolverService] Fuzzy local match: '{windowTitle}' → '{bestMatchName}' (diff: {bestLengthDiff}, IGDB: {bestIdIgdb})");
            _logger?.Info($"[IgdbResolverService] Fuzzy local match: '{windowTitle}' → '{bestMatchName}' (length diff: {bestLengthDiff}, IGDB: {bestIdIgdb}). A wrong game logged for this session would start here.");
            return bestIdIgdb;
        }

        return null;
    }

    /// <summary>
    /// Last resort: asks Cloudflare Worker, which proxies IGDB so no IGDB/Twitch
    /// credentials have to ship with the client. Blocking, matching the caller; the 5 s client
    /// timeout is what bounds the stall. Any failure returns null — an unidentified game still
    /// produces a valid session that the user can name by hand.
    /// </summary>
    private string? TryMatchApi(string windowTitle)
    {
        try
        {
            string normalizedQuery = NormalizeTitle(windowTitle);
            string encodedQuery = WebUtility.UrlEncode(normalizedQuery);
            string url = $"https://apploggd.nik250dev.workers.dev/api/v1/igdb/search?query={encodedQuery}";

            Console.WriteLine($"[IgdbResolverService] No local match for '{windowTitle}'. Querying API: {url}");
            // The query itself is left out of the file log: it is the raw window title, and the
            // normalized form below is enough to follow what was asked for.
            _logger?.Info($"[IgdbResolverService] No local match for '{windowTitle}'. Falling back to the search API with '{normalizedQuery}'.");

            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[IgdbResolverService] API returned status {response.StatusCode} for '{windowTitle}'.");
                _logger?.Warning($"[IgdbResolverService] The search API answered {(int)response.StatusCode} for '{windowTitle}'. The game stays unidentified for this session.");
                return null;
            }

            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var parsed = JsonSerializer.Deserialize<IgdbSearchResponse>(json);

            if (parsed?.IdIgdb != null)
            {
                Console.WriteLine($"[IgdbResolverService] API match: '{windowTitle}' → IGDB: {parsed.IdIgdb}");
                _logger?.Info($"[IgdbResolverService] API match: '{windowTitle}' → IGDB: {parsed.IdIgdb}.");
                return parsed.IdIgdb;
            }

            Console.WriteLine($"[IgdbResolverService] API returned null id_igdb for '{windowTitle}'.");
            _logger?.Warning($"[IgdbResolverService] The search API knows no game for '{windowTitle}'. The session will be offered without a title of its own.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IgdbResolverService] API error for '{windowTitle}': {ex.Message}");
            _logger?.Error($"[IgdbResolverService] The search API call for '{windowTitle}' failed. The game stays unidentified for this session.", ex);
            return null;
        }
    }

    /// <summary>
    /// Builds the (normalized name, id_igdb) list from the games database, taking both "name" and
    /// "aliases" so regional and abbreviated titles resolve as well as the canonical one.
    ///
    /// Reads the updated copy in %LOCALAPPDATA%\Apploggd first and falls back to the embedded
    /// resource, so a first or offline run still identifies games.
    /// </summary>
    private List<(string NormalizedName, string? IdIgdb)> BuildNameIndex()
    {
        var index = new List<(string, string?)>();

        try
        {
            string jsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "detectable_processed.json");
            string jsonContent;

            if (File.Exists(jsonPath))
            {
                Console.WriteLine($"[IgdbResolverService] Loading detectable_processed.json from disk: {jsonPath}");
                jsonContent = File.ReadAllText(jsonPath);
            }
            else
            {
                Console.WriteLine($"[IgdbResolverService] detectable_processed.json not found on disk. Falling back to embedded Avalonia resource.");
                try
                {
                    var uri = new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/detectable_processed.json");
                    using var stream = AssetLoader.Open(uri);
                    using var reader = new StreamReader(stream);
                    jsonContent = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IgdbResolverService] Failed to load embedded detectable_processed.json: {ex.Message}");
                    _logger?.Error("[IgdbResolverService] Neither the local nor the embedded detectable_processed.json could be read. Games can no longer be identified by title.", ex);
                    return index;
                }
            }
            var games = JsonSerializer.Deserialize<List<DetectableGame>>(jsonContent);

            if (games == null)
            {
                Console.WriteLine("[IgdbResolverService] Failed to parse detectable_processed.json.");
                _logger?.Error("[IgdbResolverService] detectable_processed.json parsed as null. Games can no longer be identified by title.");
                return index;
            }

            foreach (var game in games)
            {
                // An entry with no IGDB id cannot answer the only question asked here.
                if (string.IsNullOrEmpty(game.IdIgdb))
                    continue;

                string normalizedName = NormalizeTitle(game.Name);
                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    index.Add((normalizedName, game.IdIgdb));
                }

                // Add all aliases
                if (game.Aliases != null)
                {
                    foreach (var alias in game.Aliases)
                    {
                        string normalizedAlias = NormalizeTitle(alias);
                        if (!string.IsNullOrWhiteSpace(normalizedAlias))
                        {
                            index.Add((normalizedAlias, game.IdIgdb));
                        }
                    }
                }
            }

            Console.WriteLine($"[IgdbResolverService] Built name index with {index.Count} entries for local fuzzy matching.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IgdbResolverService] Error building name index: {ex.Message}");
            _logger?.Error("[IgdbResolverService] Building the name index from detectable_processed.json failed. Games can no longer be identified by title.", ex);
        }

        return index;
    }
}
