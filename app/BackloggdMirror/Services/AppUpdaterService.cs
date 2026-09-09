using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace BackloggdMirror.Services;

/// <summary>
/// In-app updater, backed by Velopack. Outside a Velopack package (dotnet run, a hand-copied
/// folder) <see cref="IsSupported"/> is false and every method is a no-op.
/// Not to be confused with <see cref="AppUpdateService"/>, which only announces a new release.
/// </summary>
public sealed class AppUpdaterService
{
    /// <summary>Overrides the feed with a URL or a local folder, to test a release before publishing it.</summary>
    public const string FeedOverrideVariable = "APPLOGGD_UPDATE_FEED";

    private const string GithubRepoUrl = "https://github.com/nik250dev/apploggd";

    /// <summary>
    /// True when Velopack relaunched this process after applying an update, so the app can show what
    /// changed. Set from <c>VelopackApp.OnRestarted</c> in Program.Main, long before any ViewModel.
    /// </summary>
    public static bool RestartedAfterUpdate { get; set; }

    private readonly IAppLogger _logger;
    private readonly UpdateManager? _manager;

    public AppUpdaterService(IAppLogger logger)
    {
        _logger = logger;

        try
        {
            _manager = new UpdateManager(CreateSource(logger));
        }
        catch (Exception ex)
        {
            // A broken updater must never stop the app from running.
            _logger.Error("[AppUpdaterService] Could not initialise Velopack.", ex);
        }
    }

    /// <summary>True when this copy is a Velopack package and can therefore update itself.</summary>
    public bool IsSupported => _manager?.IsInstalled == true;

    public string? PackagedVersion => _manager?.CurrentVersion?.ToString();

    private static IUpdateSource CreateSource(IAppLogger logger)
    {
        var overrideFeed = Environment.GetEnvironmentVariable(FeedOverrideVariable);

        if (!string.IsNullOrWhiteSpace(overrideFeed))
        {
            if (Directory.Exists(overrideFeed))
            {
                logger.Warning($"[AppUpdaterService] Using the local feed override: {overrideFeed}");
                return new SimpleFileSource(new DirectoryInfo(overrideFeed));
            }

            if (Uri.TryCreate(overrideFeed, UriKind.Absolute, out _))
            {
                logger.Warning($"[AppUpdaterService] Using the web feed override: {overrideFeed}");
                return new SimpleWebSource(overrideFeed);
            }

            logger.Warning($"[AppUpdaterService] Ignoring {FeedOverrideVariable}: '{overrideFeed}' is neither an existing folder nor a URL.");
        }

        // prerelease: false, so drafts and prereleases are ignored, same as AppUpdateService.
        return new GithubSource(GithubRepoUrl, null, false);
    }

    /// <summary>Pending update, or null when up to date. Silent on failure, like AppUpdateService.</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (_manager is null || !IsSupported)
        {
            _logger.Info("[AppUpdaterService] Not running from a Velopack package; skipping the update check.");
            return null;
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync();

            _logger.Info(update is null
                ? $"[AppUpdaterService] Apploggd is up to date (installed {PackagedVersion})."
                : $"[AppUpdaterService] Update available: {update.TargetFullRelease.Version} (installed {PackagedVersion}).");

            return update;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[AppUpdaterService] Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the update; the installed copy is only touched by <see cref="ApplyOnExit"/>.</summary>
    public async Task<bool> DownloadAsync(UpdateInfo update, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (_manager is null || !IsSupported) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(update, onProgress, cancelToken: cancellationToken);
            _logger.Info($"[AppUpdaterService] Downloaded version {update.TargetFullRelease.Version}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Info("[AppUpdaterService] Update download cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("[AppUpdaterService] Failed to download the update.", ex);
            return false;
        }
    }

    /// <summary>
    /// Arms Update.exe to swap in the new version once this process exits, then relaunch. The caller
    /// must shut the app down straight after: the updater gives up waiting after 60 seconds.
    ///
    /// Not <c>ApplyUpdatesAndRestart</c>, which kills the process on the spot but always shows
    /// Velopack's own generic progress window; this one takes <c>silent</c>, so the only update UI
    /// the user sees is ours. The single-instance mutex needs no special handling — Windows frees it
    /// when the process dies. Restarted without arguments, so an update applied from a silent start
    /// comes back visible.
    /// </summary>
    public bool ApplyOnExit(UpdateInfo update)
    {
        if (_manager is null || !IsSupported) return false;

        try
        {
            _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, restartArgs: null);
            _logger.Info($"[AppUpdaterService] Update.exe is armed for {update.TargetFullRelease.Version} and waiting for this process to exit.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("[AppUpdaterService] Could not hand the update over to Update.exe.", ex);
            return false;
        }
    }
}
