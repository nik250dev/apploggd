using System;
using System.IO;
using System.Text.Json.Serialization;

namespace BackloggdMirror.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlacklistEntryKind
{
    /// <summary>An executable. For an emulator, every game it runs is ignored with it.</summary>
    Application,

    /// <summary>One game inside an emulator, keyed like its session (ROM path, game ID or title ID).</summary>
    EmulatedGame
}

/// <summary>Something detection must ignore. Serialized as-is to blacklist.json.</summary>
public sealed class BlacklistEntry
{
    public BlacklistEntryKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }

    /// <summary>Null when the path could not be read, which makes the entry match by <see cref="ExecutableName"/> alone.</summary>
    public string? ExecutablePath { get; set; }
    public string? ExecutableName { get; set; }

    public string? EmulatorName { get; set; }
    public string? ContentKey { get; set; }

    [JsonIgnore]
    public bool IsEmulatedGame => Kind == BlacklistEntryKind.EmulatedGame;

    [JsonIgnore]
    public bool IsApplication => Kind == BlacklistEntryKind.Application;

    /// <summary>The ROM's file name; the whole path goes in the tooltip.</summary>
    [JsonIgnore]
    public string ContentLabel => ContentKey != null && ContentKey.Contains('\\') ? Path.GetFileName(ContentKey) : ContentKey ?? string.Empty;

    [JsonIgnore]
    public string ApplicationLabel => ExecutablePath ?? ExecutableName ?? string.Empty;

    [JsonIgnore]
    public string Details => IsEmulatedGame ? ContentKey ?? string.Empty : ApplicationLabel;

    public bool SameTargetAs(BlacklistEntry other)
    {
        if (Kind != other.Kind)
            return false;

        if (IsEmulatedGame)
        {
            return string.Equals(EmulatorName, other.EmulatorName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ContentKey, other.ContentKey, StringComparison.OrdinalIgnoreCase);
        }

        return ExecutablePath != null && other.ExecutablePath != null
            ? string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(ExecutableName, other.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }
}
