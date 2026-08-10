namespace BackloggdMirror.Models;

/// <summary>
/// Contains the resolved game data from the local detectable_processed.json,
/// used to populate the session confirmation panel without needing Playwright preloading.
/// </summary>
public class GameLookupResult
{
    /// <summary>
    /// The canonical game name from the JSON database.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full URL to the cover image on IGDB, or null if no cover is available.
    /// Example: https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{coverId}.jpg
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>
    /// Full URL to a randomly selected artwork image on IGDB, or null if no artworks are available.
    /// Example: https://images.igdb.com/igdb/image/upload/t_720p/{artworkId}.jpg
    /// </summary>
    public string? ArtworkUrl { get; set; }

    /// <summary>
    /// The Backloggd game URL slug (e.g., "overwatch--1", "rocket-league").
    /// Used to navigate directly to https://backloggd.com/games/{slug}/ without searching.
    /// </summary>
    public string? BackloggdGameUrl { get; set; }
}
