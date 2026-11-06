using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackloggdMirror.Models;
using BackloggdMirror.Services.Platform.Linux;

namespace BackloggdMirror.Services;

/// <summary>
/// Games chosen by hand for an executable or a ROM, persisted to remembered_games.json. Only the UI thread
/// uses it: the detection pass hands its result back before anything looks here.
/// </summary>
public sealed class RememberedGameService
{
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _folder;
    private readonly string _file;
    private readonly IAppLogger? _logger;
    private List<RememberedGame> _entries = new();

    public RememberedGameService(IAppLogger? logger = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd"), logger)
    {
    }

    public RememberedGameService(string folder, IAppLogger? logger = null)
    {
        _folder = folder;
        _file = Path.Combine(folder, "remembered_games.json");
        _logger = logger;
        Load();
    }

    public IReadOnlyList<RememberedGame> Entries => _entries;

    /// <summary>The game remembered for what detection found, if any.</summary>
    public RememberedGame? Find(DetectedGame game) => _entries.FirstOrDefault(e => Matches(e, game));

    /// <summary>Takes the place of whatever was remembered for the same target.</summary>
    public void Remember(RememberedGame entry)
    {
        _entries = _entries.Where(e => !SameTarget(e, entry)).Append(entry).ToList();
        _logger?.Info($"[RememberedGameService] '{entry.GameName}' is now used for '{entry.Details}'.");
        Save();
    }

    /// <summary>False when it was not listed, e.g. an undo after the target was remembered again.</summary>
    public bool Remove(RememberedGame entry)
    {
        if (!_entries.Contains(entry)) return false;

        _entries = _entries.Where(e => !ReferenceEquals(e, entry)).ToList();
        _logger?.Info($"[RememberedGameService] '{entry.GameName}' is no longer used for '{entry.Details}'.");
        Save();
        return true;
    }

    /// <summary>For an undo: false when the target already has another game.</summary>
    public bool Restore(RememberedGame entry)
    {
        if (_entries.Any(e => SameTarget(e, entry))) return false;

        _entries = _entries.Append(entry).ToList();
        _logger?.Info($"[RememberedGameService] '{entry.GameName}' is used again for '{entry.Details}'.");
        Save();
        return true;
    }

    /// <summary>After the game of an entry was changed in place.</summary>
    public void Update(RememberedGame entry)
    {
        _logger?.Info($"[RememberedGameService] Updated the entry for '{entry.Details}' ('{entry.GameName}', IGDB {entry.IdIgdb ?? "unknown"}).");
        Save();
    }

    /// <summary>Empties the list in memory without touching disk, for after the data wipe has deleted the file.</summary>
    public void Reset() => _entries = new();

    /// <summary>Null when detection gave neither an executable path nor a content key to tie the game to.</summary>
    public static RememberedGame? ForDetectedGame(DetectedGame game, string gameName, string? gameUrl, string? coverUrl)
    {
        var entry = new RememberedGame
        {
            AddedAt = DateTime.Now,
            GameName = gameName,
            GameUrl = gameUrl,
            CoverUrl = coverUrl
        };

        if (game.Source == DetectionSource.Emulator)
        {
            if (string.IsNullOrEmpty(game.ContentKey)) return null;
            entry.Kind = RememberedGameKind.EmulatedGame;
            entry.EmulatorName = game.EmulatorName;
            entry.ContentKey = game.ContentKey;
            return entry;
        }

        if (string.IsNullOrEmpty(game.ExecutablePath)) return null;
        entry.Kind = RememberedGameKind.Application;
        entry.ExecutablePath = OperatingSystem.IsLinux() ? LinuxBlacklistFile.CanonicalPath(game.ExecutablePath) : game.ExecutablePath;
        return entry;
    }

    // A ROM keeps its game whatever emulator runs it.
    private static bool Matches(RememberedGame entry, DetectedGame game)
    {
        if (entry.IsEmulatedGame)
            return game.Source == DetectionSource.Emulator && game.ContentKey != null
                && string.Equals(entry.ContentKey, game.ContentKey, StringComparison.OrdinalIgnoreCase);

        return game.Source != DetectionSource.Emulator && entry.ExecutablePath != null && game.ExecutablePath != null
            && SamePath(entry.ExecutablePath, game.ExecutablePath);
    }

    private static bool SameTarget(RememberedGame a, RememberedGame b) =>
        a.Kind == b.Kind && (a.IsEmulatedGame
            ? string.Equals(a.ContentKey, b.ContentKey, StringComparison.OrdinalIgnoreCase)
            : a.ExecutablePath != null && b.ExecutablePath != null && SamePath(a.ExecutablePath, b.ExecutablePath));

    private static bool SamePath(string a, string b) =>
        string.Equals(BlacklistService.NormalizePath(a), BlacklistService.NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;

            var entries = JsonSerializer.Deserialize<List<RememberedGame>>(File.ReadAllText(_file));
            if (entries != null)
            {
                _entries = entries.Where(e => !string.IsNullOrEmpty(e.GameName)).ToList();
                _logger?.Info($"[RememberedGameService] Loaded {_entries.Count} remembered games.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"[RememberedGameService] Could not read '{_file}'. Starting with no remembered games for this run.", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries, SaveOptions));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[RememberedGameService] Could not write '{_file}'. The change applies to this run only.", ex);
        }
    }
}
