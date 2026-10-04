using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackloggdMirror.Models;

namespace BackloggdMirror.Services;

/// <summary>
/// Sessions left for later, persisted to pending_sessions.json. Only touched from the UI thread.
/// Like the blacklist, one instance lives for the whole process and outlives a logout.
/// </summary>
public sealed class PendingSessionService
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
    private List<PendingSession> _sessions = new();

    public PendingSessionService(IAppLogger? logger = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd"), logger)
    {
    }

    public PendingSessionService(string folder, IAppLogger? logger = null)
    {
        _folder = folder;
        _file = Path.Combine(folder, "pending_sessions.json");
        _logger = logger;
        Load();
    }

    public IReadOnlyList<PendingSession> ForUser(string? username) =>
        _sessions.Where(s => string.Equals(s.Username, username ?? string.Empty, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Add(PendingSession session)
    {
        if (_sessions.Any(s => s.Id == session.Id)) return;

        _sessions.Add(session);
        _logger?.Info($"[PendingSessionService] Added '{session.GameName}' ({session.Duration:hh\\:mm\\:ss}, ended {session.EndedAt:yyyy-MM-dd HH:mm}, identified: {session.IsIdentified}, save failed: {session.SaveFailed}).");
        Save();
    }

    /// <summary>False when it was not listed, e.g. an undo after it was already removed.</summary>
    public bool Remove(PendingSession session)
    {
        if (_sessions.RemoveAll(s => s.Id == session.Id) == 0) return false;

        _logger?.Info($"[PendingSessionService] Removed '{session.GameName}' ({session.Duration:hh\\:mm\\:ss}, ended {session.EndedAt:yyyy-MM-dd HH:mm}).");
        Save();
        return true;
    }

    /// <summary>Persists a change made to a listed session in place.</summary>
    public void Update(PendingSession session)
    {
        if (!_sessions.Any(s => s.Id == session.Id)) return;

        _logger?.Info($"[PendingSessionService] Updated '{session.GameName}' (identified: {session.IsIdentified}, url: {session.GameUrl ?? "none"}).");
        Save();
    }

    /// <summary>Empties the list in memory without touching disk, for after the data wipe has deleted the file.</summary>
    public void Reset()
    {
        _sessions = new List<PendingSession>();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;

            var sessions = JsonSerializer.Deserialize<List<PendingSession>>(File.ReadAllText(_file));
            if (sessions != null)
            {
                _sessions = sessions;
                _logger?.Info($"[PendingSessionService] Loaded {sessions.Count} pending sessions.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PendingSessionService] Could not read '{_file}'. Starting with no pending sessions for this run.", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_file, JsonSerializer.Serialize(_sessions, SaveOptions));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PendingSessionService] Could not write '{_file}'. The change applies to this run only.", ex);
        }
    }
}
