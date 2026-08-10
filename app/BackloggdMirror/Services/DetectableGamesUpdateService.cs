using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BackloggdMirror.Services;

/// <summary>
/// Service responsible for downloading and updating the detectable_processed.json file
/// from the remote GitHub repository. This ensures the game detection database stays
/// up-to-date with the latest entries.
/// </summary>
public class DetectableGamesUpdateService
{
    private const string RemoteJsonUrl =
    "https://raw.githubusercontent.com/nik250dev/apploggd/data/detectable_processed.json";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly IAppLogger _logger;

    public DetectableGamesUpdateService(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Downloads the latest games database and replaces the local copy, using a conditional request
    /// so the ~10 MB payload only travels when it has actually changed.
    ///
    /// Every failure is non-fatal: the previous copy — or the embedded resource — stays in use, so
    /// detection keeps working with slightly older data rather than not at all.
    /// </summary>
    /// <returns>
    /// A <see cref="DetectableGamesUpdateResult"/> indicating the outcome of the update attempt.
    /// </returns>
    public async Task<DetectableGamesUpdateResult> TryUpdateAsync(Action<string>? onProgressMessage = null, CancellationToken cancellationToken = default)
    {
        string localFilePath = GetLocalJsonPath();
        string etagFilePath = localFilePath + ".etag";

        try
        {
            _logger.Info($"[DetectableGamesUpdateService] Checking for updates to detectable_processed.json...");

            using var request = new HttpRequestMessage(HttpMethod.Get, RemoteJsonUrl);

            // Both files must exist: an ETag without its database would claim we already hold
            // content we do not have, and the server would answer 304 with nothing to fall back on.
            if (File.Exists(localFilePath) && File.Exists(etagFilePath))
            {
                string etag = await File.ReadAllTextAsync(etagFilePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.IfNoneMatch.ParseAdd(etag);
                }
            }

            onProgressMessage?.Invoke(LocalizationService.Instance["Update_ConnectingToServer"]);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _logger.Info("[DetectableGamesUpdateService] Local database is already up-to-date. Skipping download.");
                return DetectableGamesUpdateResult.NotModified;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"[DetectableGamesUpdateService] Remote returned HTTP {(int)response.StatusCode}. Skipping update.");
                return DetectableGamesUpdateResult.NetworkError;
            }

            onProgressMessage?.Invoke(LocalizationService.Instance["Update_DownloadingDatabase"]);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Cheap sanity check before overwriting a working database: a captive-portal login page
            // or a GitHub error page would arrive as a perfectly successful 200.
            if (string.IsNullOrWhiteSpace(content) || (content[0] != '[' && content[0] != '{'))
            {
                _logger.Warning("[DetectableGamesUpdateService] Downloaded content does not appear to be valid JSON. Skipping update.");
                return DetectableGamesUpdateResult.InvalidContent;
            }

            var directory = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(localFilePath, content, cancellationToken);

            // Written after the database, never before: an ETag saved for content that failed to
            // write would suppress the next download and leave the stale copy in place for good.
            if (response.Headers.ETag != null && !string.IsNullOrWhiteSpace(response.Headers.ETag.Tag))
            {
                await File.WriteAllTextAsync(etagFilePath, response.Headers.ETag.Tag, cancellationToken);
            }

            _logger.Info($"[DetectableGamesUpdateService] Successfully updated detectable_processed.json ({content.Length} bytes).");
            return DetectableGamesUpdateResult.Success;
        }
        catch (TaskCanceledException)
        {
            _logger.Warning("[DetectableGamesUpdateService] Download timed out. Skipping update.");
            return DetectableGamesUpdateResult.NetworkError;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warning($"[DetectableGamesUpdateService] Network error: {ex.Message}. Skipping update.");
            return DetectableGamesUpdateResult.NetworkError;
        }
        catch (Exception ex)
        {
            _logger.Error("[DetectableGamesUpdateService] Unexpected error during update.", ex);
            return DetectableGamesUpdateResult.UnexpectedError;
        }
    }

    /// <summary>
    /// Location of the local database, in %LOCALAPPDATA%\Apploggd rather than beside the executable
    /// so it stays writable without elevation. The three services that read it look here first.
    /// </summary>
    private static string GetLocalJsonPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "detectable_processed.json");
    }
}

/// <summary>
/// Represents the outcome of a detectable games JSON update attempt.
/// Prepared for future handling of specific scenarios (e.g., showing user notifications).
/// </summary>
public enum DetectableGamesUpdateResult
{
    /// <summary>The file was downloaded and replaced successfully.</summary>
    Success,

    /// <summary>The remote file has not changed since the last download.</summary>
    NotModified,

    /// <summary>A network-related error occurred (timeout, DNS failure, no connectivity, etc.).</summary>
    NetworkError,

    /// <summary>The downloaded content was empty or did not appear to be valid JSON.</summary>
    InvalidContent,

    /// <summary>An unexpected error occurred during the update process.</summary>
    UnexpectedError
}
