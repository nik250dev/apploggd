using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BackloggdMirror.Models;

/// <summary>
/// Represents a game entry from the detectable_processed.json database.
/// Used to match running processes against known game executables.
/// </summary>
public class DetectableGame
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("executables")]
    public List<DetectableExecutable> Executables { get; set; } = new();

    [JsonPropertyName("id_igdb")]
    public string? IdIgdb { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("artwork")]
    public List<string>? Artwork { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Represents a single executable entry within a detectable game definition.
/// </summary>
public class DetectableExecutable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("is_launcher")]
    public bool IsLauncher { get; set; }
}
