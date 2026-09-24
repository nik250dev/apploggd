using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>What the detection tiers ask of the blacklist, from the detection thread.</summary>
public interface IDetectionBlacklist
{
    /// <summary>Bumped on every change, so a detection pass that started before one can be thrown away.</summary>
    long Version { get; }

    /// <param name="processName">As <see cref="Process.ProcessName"/> gives it, without ".exe".</param>
    bool IsApplicationBlocked(int processId, string? processName);

    bool IsContentBlocked(string emulatorName, string contentKey);
}

/// <summary>
/// The user's blacklist, persisted to blacklist.json. Written from the UI thread and read by the
/// detection pass on a worker thread, so every change swaps in a new immutable snapshot instead of
/// mutating the one a pass may be reading.
/// </summary>
public sealed class BlacklistService : IDetectionBlacklist
{
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(Array.Empty<BlacklistEntry>());

        public readonly IReadOnlyList<BlacklistEntry> Entries;
        public readonly HashSet<string> ExecutableNames = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> NameOnlyExecutables = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ExecutablePaths = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Contents = new(StringComparer.OrdinalIgnoreCase);

        public Snapshot(IReadOnlyList<BlacklistEntry> entries)
        {
            Entries = entries;

            foreach (var entry in entries)
            {
                if (entry.IsEmulatedGame)
                {
                    if (entry.EmulatorName != null && entry.ContentKey != null)
                        Contents.Add(ContentId(entry.EmulatorName, entry.ContentKey));
                    continue;
                }

                string? name = ProcessNameOf(entry);
                if (name == null)
                    continue;

                ExecutableNames.Add(name);

                if (entry.ExecutablePath != null)
                    ExecutablePaths.Add(NormalizePath(entry.ExecutablePath));
                else
                    NameOnlyExecutables.Add(name);
            }
        }
    }

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _folder;
    private readonly string _file;
    private readonly IAppLogger? _logger;
    private readonly object _writeLock = new();

    private volatile Snapshot _snapshot = Snapshot.Empty;
    private long _version;

    public BlacklistService(IAppLogger? logger = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd"), logger)
    {
    }

    public BlacklistService(string folder, IAppLogger? logger = null)
    {
        _folder = folder;
        _file = Path.Combine(folder, "blacklist.json");
        _logger = logger;
        Load();
    }

    public IReadOnlyList<BlacklistEntry> Entries => _snapshot.Entries;

    public long Version => Interlocked.Read(ref _version);

    public bool IsApplicationBlocked(int processId, string? processName)
    {
        var snapshot = _snapshot;

        // The name gates the path lookup, so a blacklist with nothing running costs a set lookup.
        if (string.IsNullOrEmpty(processName) || !snapshot.ExecutableNames.Contains(processName))
            return false;

        if (snapshot.NameOnlyExecutables.Contains(processName))
            return true;

        // An unreadable path cannot rule the match out; the user did blacklist this name.
        string? path = ProcessImagePath.TryGet(processId);
        return path == null || snapshot.ExecutablePaths.Contains(NormalizePath(path));
    }

    public bool IsContentBlocked(string emulatorName, string contentKey) =>
        _snapshot.Contents.Contains(ContentId(emulatorName, contentKey));

    /// <summary>False when the same target is already listed.</summary>
    public bool Add(BlacklistEntry entry)
    {
        lock (_writeLock)
        {
            var current = _snapshot.Entries;
            if (current.Any(e => e.SameTargetAs(entry)))
                return false;

            Publish(current.Append(entry).ToList());
        }

        _logger?.Info($"[BlacklistService] Added '{entry.DisplayName}' ({entry.Kind}: {entry.Details}).");
        Save();
        return true;
    }

    /// <summary>False when the entry was not listed, e.g. an undo after it was already removed by hand.</summary>
    public bool Remove(BlacklistEntry entry)
    {
        lock (_writeLock)
        {
            var current = _snapshot.Entries;
            if (!current.Contains(entry))
                return false;

            Publish(current.Where(e => !ReferenceEquals(e, entry)).ToList());
        }

        _logger?.Info($"[BlacklistService] Removed '{entry.DisplayName}' ({entry.Kind}: {entry.Details}).");
        Save();
        return true;
    }

    /// <summary>
    /// Empties the list in memory without touching disk. For after the data wipe, which has already
    /// deleted the file: this instance outlives the logout that follows.
    /// </summary>
    public void Reset()
    {
        lock (_writeLock)
        {
            Publish(Array.Empty<BlacklistEntry>());
        }
    }

    public static BlacklistEntry ForDetectedGame(DetectedGame game, string displayName)
    {
        if (game.Source == DetectionSource.Emulator && game.EmulatorName != null && game.ContentKey != null)
        {
            return new BlacklistEntry
            {
                Kind = BlacklistEntryKind.EmulatedGame,
                DisplayName = displayName,
                AddedAt = DateTime.Now,
                EmulatorName = game.EmulatorName,
                ContentKey = game.ContentKey
            };
        }

        string? executableName = game.ExecutablePath != null ? Path.GetFileName(game.ExecutablePath) : ProcessFileName(game.ProcessId);

        return new BlacklistEntry
        {
            Kind = BlacklistEntryKind.Application,
            DisplayName = displayName,
            AddedAt = DateTime.Now,
            ExecutablePath = game.ExecutablePath,
            ExecutableName = executableName
        };
    }

    public static BlacklistEntry ForExecutable(string path)
    {
        string? description = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
        }
        catch
        {
            // No version resource: the file name is a fine name too.
        }

        return new BlacklistEntry
        {
            Kind = BlacklistEntryKind.Application,
            DisplayName = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim(),
            AddedAt = DateTime.Now,
            ExecutablePath = path,
            ExecutableName = Path.GetFileName(path)
        };
    }

    /// <summary>True when the entry covers this session, so adding it should end the session too.</summary>
    public static bool Covers(BlacklistEntry entry, DetectedGame game)
    {
        if (entry.IsEmulatedGame)
        {
            return string.Equals(entry.EmulatorName, game.EmulatorName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.ContentKey, game.ContentKey, StringComparison.OrdinalIgnoreCase);
        }

        if (entry.ExecutablePath != null && game.ExecutablePath != null)
            return string.Equals(NormalizePath(entry.ExecutablePath), NormalizePath(game.ExecutablePath), StringComparison.OrdinalIgnoreCase);

        return entry.ExecutableName != null && game.ExecutablePath != null
            && string.Equals(entry.ExecutableName, Path.GetFileName(game.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    private void Publish(IReadOnlyList<BlacklistEntry> entries)
    {
        _snapshot = new Snapshot(entries);
        Interlocked.Increment(ref _version);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
                return;

            var entries = JsonSerializer.Deserialize<List<BlacklistEntry>>(File.ReadAllText(_file));
            if (entries != null)
            {
                Publish(entries);
                _logger?.Info($"[BlacklistService] Loaded {entries.Count} blacklist entries.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"[BlacklistService] Could not read '{_file}'. Starting with an empty blacklist for this run.", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_file, JsonSerializer.Serialize(_snapshot.Entries, SaveOptions));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[BlacklistService] Could not write '{_file}'. The change applies to this run only.", ex);
        }
    }

    private static string? ProcessNameOf(BlacklistEntry entry)
    {
        string? fileName = entry.ExecutableName ?? (entry.ExecutablePath != null ? Path.GetFileName(entry.ExecutablePath) : null);
        return string.IsNullOrEmpty(fileName) ? null : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? ProcessFileName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePath(string path) => path.Replace('/', '\\');

    private static string ContentId(string emulatorName, string contentKey) => emulatorName + "|" + contentKey;
}
