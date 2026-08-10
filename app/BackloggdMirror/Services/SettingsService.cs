using System;
using System.IO;
using System.Text.Json;

namespace BackloggdMirror.Services;

/// <summary>
/// User settings, serialized as-is to settings.json. The class is its own DTO, so every public
/// property here becomes a key in the file — renaming one silently resets that setting for
/// existing users.
/// </summary>
public class SettingsService
{
    private readonly string _settingsFolder;
    private readonly string _settingsFile;

    // A field, never a property: every public property of this class is serialized into
    // settings.json, and the logger has no business being written there.
    private readonly IAppLogger? _logger;

    public bool HasSeenSessionDiscardWarningV3 { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string Language { get; set; } = "System";

    private static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd");

    /// <summary>
    /// Kept genuinely parameterless (rather than folded into the overload below with a default
    /// argument) because <see cref="JsonSerializer"/> needs a public parameterless constructor to
    /// deserialize this class into itself in <see cref="Load"/>.
    /// </summary>
    public SettingsService() : this(DefaultFolder, null)
    {
    }

    public SettingsService(IAppLogger? logger) : this(DefaultFolder, logger)
    {
    }

    public SettingsService(string settingsFolder, IAppLogger? logger = null)
    {
        _settingsFolder = settingsFolder;
        _settingsFile = Path.Combine(_settingsFolder, "settings.json");
        _logger = logger;
    }

    /// <summary>
    /// Loads settings from disk, falling back to the defaults on any failure: unreadable settings
    /// must not stop the app from starting.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFile))
            {
                var json = File.ReadAllText(_settingsFile);
                // Deserializes into a throwaway instance and copies the values across, because the
                // paths this instance was constructed with must survive the load.
                var settings = JsonSerializer.Deserialize<SettingsService>(json);
                if (settings != null)
                {
                    HasSeenSessionDiscardWarningV3 = settings.HasSeenSessionDiscardWarningV3;
                    MinimizeToTray = settings.MinimizeToTray;
                    StartWithWindows = settings.StartWithWindows;
                    Language = settings.Language ?? "System";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading settings: {ex.Message}");
            _logger?.Error($"[SettingsService] Could not read settings.json from '{_settingsFile}'. Falling back to the default settings for this run.", ex);
        }
    }

    /// <summary>
    /// Returns the in-memory settings to their defaults without touching disk. Used after wiping
    /// the application data: otherwise the live instance would write the old settings back out on
    /// the next Save().
    /// </summary>
    public void Reset()
    {
        HasSeenSessionDiscardWarningV3 = false;
        MinimizeToTray = true;
        StartWithWindows = false;
        Language = "System";
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(_settingsFolder))
            {
                Directory.CreateDirectory(_settingsFolder);
            }

            var json = JsonSerializer.Serialize(this);
            File.WriteAllText(_settingsFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving settings: {ex.Message}");
            _logger?.Error($"[SettingsService] Could not write settings.json to '{_settingsFile}'. The user's changes will be lost on the next start.", ex);
        }
    }
}
