using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>
/// The reverse of <see cref="IgdbResolverService"/>: given an IGDB id, produces what the UI needs
/// to show (cover, artwork, Backloggd slug).
///
/// The local path is synchronous and instant, which matters because it feeds the session
/// confirmation panel the moment a game closes. The async path exists for ids that came back from
/// the search API and therefore have no local entry to read.
/// </summary>
public class GameDataService
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private const string ApiBaseUrl = "https://apploggd.nik250dev.workers.dev/api/v1/igdb";

    private Dictionary<string, DetectableGame> _igdbIndex;
    private static readonly Random _random = new();

    private readonly IAppLogger? _logger;

    public GameDataService(IAppLogger? logger = null)
    {
        _logger = logger;
        _igdbIndex = BuildIgdbIndex();
    }

    /// <summary>
    /// Reloads the internal database from the local detectable_processed.json file.
    /// This is called when the file is updated in the background.
    /// </summary>
    public void ReloadDatabase()
    {
        _igdbIndex = BuildIgdbIndex();
    }

    /// <summary>
    /// Looks up a game by its IGDB ID and returns a <see cref="GameLookupResult"/>
    /// with cover URL, artwork URL, Backloggd slug, and canonical name.
    /// Returns null if the IGDB ID is not found in the database.
    /// </summary>
    public virtual GameLookupResult? LookupByIgdbId(string? idIgdb)
    {
        if (string.IsNullOrEmpty(idIgdb))
            return null;

        if (!_igdbIndex.TryGetValue(idIgdb, out var game))
            return null;

        // Build cover URL
        string? coverUrl = null;
        if (!string.IsNullOrEmpty(game.Cover))
        {
            coverUrl = $"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{game.Cover}.jpg";
        }

        // Picked at random rather than always the first, so replaying the same game does not always
        // show the same background.
        string? artworkUrl = null;
        if (game.Artwork != null && game.Artwork.Count > 0)
        {
            var selectedArtwork = game.Artwork[_random.Next(game.Artwork.Count)];
            artworkUrl = $"https://images.igdb.com/igdb/image/upload/t_720p/{selectedArtwork}.jpg";
        }

        string? backloggdUrl = null;
        if (!string.IsNullOrEmpty(game.Url))
        {
            backloggdUrl = game.Url;
        }

        return new GameLookupResult
        {
            Name = game.Name,
            CoverUrl = coverUrl,
            ArtworkUrl = artworkUrl,
            BackloggdGameUrl = backloggdUrl
        };
    }

    /// <summary>
    /// Fallback for a game identified through the search API and therefore absent from the local
    /// database. Returns null only when all three endpoints come back empty; a partial result is
    /// still worth showing, since a missing cover degrades the panel but does not break the flow.
    /// </summary>
    public virtual async Task<GameLookupResult?> LookupByIgdbIdFromApiAsync(string idIgdb)
    {
        if (string.IsNullOrEmpty(idIgdb))
            return null;

        Console.WriteLine($"[GameDataService] API fallback lookup for IGDB ID: {idIgdb}");

        try
        {
            // Three independent endpoints, so they run together: sequentially the user would wait
            // out three round-trips before the confirmation panel filled in.
            var coverTask = FetchJsonFieldAsync($"{ApiBaseUrl}/cover?game_id={idIgdb}", "cover_url");
            var artworkTask = FetchArtworksAsync($"{ApiBaseUrl}/artwork?game_id={idIgdb}");
            var urlTask = FetchJsonFieldAsync($"{ApiBaseUrl}/url?game_id={idIgdb}", "game_url");

            await Task.WhenAll(coverTask, artworkTask, urlTask);

            string? coverUrl = await coverTask;
            List<string>? artworks = await artworkTask;
            string? gameUrl = await urlTask;

            string? backloggdSlug = ParseBackloggdSlug(gameUrl);

            string? artworkUrl = null;
            if (artworks != null && artworks.Count > 0)
            {
                artworkUrl = artworks[_random.Next(artworks.Count)];
            }

            if (coverUrl == null && artworkUrl == null && backloggdSlug == null)
            {
                Console.WriteLine($"[GameDataService] API fallback returned no data for IGDB ID: {idIgdb}");
                _logger?.Warning($"[GameDataService] The API fallback returned nothing for IGDB ID '{idIgdb}'. The confirmation panel gets no cover, artwork or Backloggd link.");
                return null;
            }

            Console.WriteLine($"[GameDataService] API fallback resolved: cover={coverUrl != null}, artwork={artworkUrl != null}, slug={backloggdSlug}");
            _logger?.Info($"[GameDataService] API fallback resolved IGDB ID '{idIgdb}': cover={coverUrl != null}, artwork={artworkUrl != null}, slug={backloggdSlug ?? "none"}.");

            return new GameLookupResult
            {
                // These endpoints do not return a name; the caller falls back to the window title.
                Name = string.Empty,
                CoverUrl = coverUrl,
                ArtworkUrl = artworkUrl,
                BackloggdGameUrl = backloggdSlug
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameDataService] API fallback error for IGDB ID '{idIgdb}': {ex.Message}");
            _logger?.Error($"[GameDataService] The API fallback for IGDB ID '{idIgdb}' failed. The confirmation panel gets no cover, artwork or Backloggd link.", ex);
            return null;
        }
    }

    /// <summary>
    /// Fetches a single string field from a JSON API response.
    /// Used for the cover and url endpoints.
    /// </summary>
    private async Task<string?> FetchJsonFieldAsync(string url, string fieldName)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty(fieldName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameDataService] Error fetching '{fieldName}' from {url}: {ex.Message}");
            _logger?.Warning($"[GameDataService] Could not fetch '{fieldName}' from {url}: {ex.Message}. That part of the game data will be missing.");
        }
        return null;
    }

    /// <summary>
    /// Fetches the artwork URLs array from the artwork API endpoint.
    /// Returns the full URLs directly (e.g., "https://images.igdb.com/igdb/image/upload/t_720p/ar5wr7.jpg").
    /// </summary>
    private async Task<List<string>?> FetchArtworksAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("artworks", out var artworksArray) && artworksArray.ValueKind == JsonValueKind.Array)
            {
                var result = new List<string>();
                foreach (var item in artworksArray.EnumerateArray())
                {
                    var artworkUrl = item.GetString();
                    if (!string.IsNullOrEmpty(artworkUrl))
                    {
                        result.Add(artworkUrl);
                    }
                }
                return result.Count > 0 ? result : null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameDataService] Error fetching artworks from {url}: {ex.Message}");
            _logger?.Warning($"[GameDataService] Could not fetch the artworks from {url}: {ex.Message}. The confirmation panel falls back to a plain background.");
        }
        return null;
    }

    /// <summary>
    /// Extracts the slug from a full IGDB game URL
    /// ("https://www.igdb.com/games/empulse" → "empulse").
    ///
    /// This rests on an assumption worth knowing about: Backloggd uses the same slug as IGDB, so
    /// the URL of one works for the other. Where that stops holding, the game page will 404 and
    /// registration falls back to searching by name.
    /// </summary>
    internal static string? ParseBackloggdSlug(string? igdbUrl)
    {
        if (string.IsNullOrEmpty(igdbUrl))
            return null;

        try
        {
            var uri = new Uri(igdbUrl);
            // Segments of "/games/empulse" are ["/", "games/", "empulse"].
            var segments = uri.Segments;
            if (segments.Length >= 3)
            {
                return segments[^1].TrimEnd('/');
            }
        }
        catch
        {
            // Not an absolute URL: fall back to taking whatever follows the last slash.
            var lastSlash = igdbUrl.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < igdbUrl.Length - 1)
            {
                return igdbUrl[(lastSlash + 1)..].TrimEnd('/');
            }
        }
        return null;
    }

    /// <summary>
    /// Indexes the games database by IGDB id for O(1) lookup, reading the updated copy in
    /// %LOCALAPPDATA%\Apploggd first and the embedded resource as a fallback.
    /// </summary>
    private Dictionary<string, DetectableGame> BuildIgdbIndex()
    {
        var index = new Dictionary<string, DetectableGame>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string jsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "detectable_processed.json");
            string jsonContent;

            if (File.Exists(jsonPath))
            {
                Console.WriteLine($"[GameDataService] Loading detectable_processed.json from disk: {jsonPath}");
                jsonContent = File.ReadAllText(jsonPath);
            }
            else
            {
                Console.WriteLine($"[GameDataService] detectable_processed.json not found on disk. Falling back to embedded Avalonia resource.");
                try
                {
                    var uri = new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/detectable_processed.json");
                    using var stream = AssetLoader.Open(uri);
                    using var reader = new StreamReader(stream);
                    jsonContent = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameDataService] Failed to load embedded detectable_processed.json: {ex.Message}");
                    _logger?.Error("[GameDataService] Neither the local nor the embedded detectable_processed.json could be read. Covers and Backloggd links have to come from the API for every game.", ex);
                    return index;
                }
            }
            var games = JsonSerializer.Deserialize<List<DetectableGame>>(jsonContent);

            if (games == null)
            {
                Console.WriteLine("[GameDataService] Failed to parse detectable_processed.json.");
                _logger?.Error("[GameDataService] detectable_processed.json parsed as null. Covers and Backloggd links have to come from the API for every game.");
                return index;
            }

            foreach (var game in games)
            {
                if (!string.IsNullOrEmpty(game.IdIgdb))
                {
                    // The database can list the same IGDB id more than once (re-releases sharing an
                    // entry); TryAdd keeps the first rather than throwing.
                    index.TryAdd(game.IdIgdb, game);
                }
            }

            Console.WriteLine($"[GameDataService] Built IGDB index with {index.Count} entries.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameDataService] Error loading detectable_processed.json: {ex.Message}");
            _logger?.Error("[GameDataService] Building the IGDB index from detectable_processed.json failed. Covers and Backloggd links have to come from the API for every game.", ex);
        }

        return index;
    }
}
