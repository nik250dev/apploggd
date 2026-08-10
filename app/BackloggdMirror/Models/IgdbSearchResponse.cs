using System.Text.Json.Serialization;

namespace BackloggdMirror.Models;

/// <summary>
/// Represents the JSON response from the IGDB search API endpoint.
/// Expected format: { "id_igdb": "12345" } or { "id_igdb": null }
/// </summary>
public class IgdbSearchResponse
{
    [JsonPropertyName("id_igdb")]
    public string? IdIgdb { get; set; }
}
