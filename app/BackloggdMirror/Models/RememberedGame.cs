using System;
using System.Text.Json.Serialization;

namespace BackloggdMirror.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RememberedGameKind
{
    /// <summary>An executable, keyed by its full path.</summary>
    Application,

    /// <summary>One game inside an emulator, keyed like its session (ROM path, game ID or title ID).</summary>
    EmulatedGame
}

/// <summary>A game chosen by hand for an executable or a ROM, to be used instead of what detection finds.</summary>
public sealed class RememberedGame
{
    public RememberedGameKind Kind { get; set; }
    public DateTime AddedAt { get; set; }

    public string? ExecutablePath { get; set; }
    public string? EmulatorName { get; set; }
    public string? ContentKey { get; set; }

    public string GameName { get; set; } = string.Empty;
    public string? GameUrl { get; set; }
    public string? CoverUrl { get; set; }

    /// <summary>Found the first time it is played, for the Home's artwork; null until then or if IGDB does not know it.</summary>
    public string? IdIgdb { get; set; }

    [JsonIgnore]
    public bool IsEmulatedGame => Kind == RememberedGameKind.EmulatedGame;

    [JsonIgnore]
    public bool IsApplication => Kind == RememberedGameKind.Application;

    /// <summary>The ROM's file name; the whole key goes in the tooltip.</summary>
    [JsonIgnore]
    public string ContentLabel => FileNameOf(ContentKey);

    [JsonIgnore]
    public string Details => (IsEmulatedGame ? ContentKey : ExecutablePath) ?? string.Empty;

    /// <summary>The executable's or the ROM's file name, as the confirmation names it.</summary>
    [JsonIgnore]
    public string TargetLabel => FileNameOf(Details);

    /// <summary>The last part of a path with either separator: a Proton game's path is a Windows one, even on Linux.</summary>
    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }
}
