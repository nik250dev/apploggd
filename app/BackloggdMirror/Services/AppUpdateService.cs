using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BackloggdMirror.Services;

/// <summary>
/// An Apploggd release newer than the installed one.
/// <c>PublishedAt</c> is nullable because the date is decorative: if GitHub does not return it, or
/// it cannot be parsed, the notice is still shown.
/// </summary>
public sealed record AppUpdateInfo(string Version, string ReleaseUrl, DateTimeOffset? PublishedAt);

/// <summary>
/// Checks at startup whether a newer Apploggd release exists, by querying the latest release
/// published on GitHub.
///
/// Deliberately silent: every failure (no network, GitHub down, API format change, anonymous rate
/// limit reached) is reported as "no update" plus a warning in the log. A version notice must never
/// bother the user with errors nor hold up startup.
/// </summary>
public sealed class AppUpdateService
{
    // /releases/latest already excludes drafts and prereleases, which is exactly what we want:
    // only published stable versions are announced.
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/nik250dev/apploggd/releases/latest";

    /// <summary>Page the "Download" button falls back to when the release has no URL of its own.</summary>
    public const string ReleasesPageUrl = "https://github.com/nik250dev/apploggd/releases";

    private static readonly HttpClient _httpClient = CreateClient();

    private readonly IAppLogger _logger;

    public AppUpdateService(IAppLogger logger)
    {
        _logger = logger;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // GitHub's API answers 403 to any request without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Apploggd-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    /// <summary>
    /// Returns the latest release if it is newer than <paramref name="currentVersion"/>, or null if
    /// already up to date or the check fails for any reason.
    /// </summary>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion);
        if (current is null)
        {
            // Without a reliable local version there is nothing to compare against; announcing an
            // update would be guesswork.
            _logger.Warning($"[AppUpdateService] Could not parse the installed version ('{currentVersion}'). Skipping update check.");
            return null;
        }

        try
        {
            _logger.Info("[AppUpdateService] Checking for a newer Apploggd release...");

            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning($"[AppUpdateService] GitHub returned HTTP {(int)response.StatusCode}. Skipping update check.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = GetString(root, "tag_name");
            var latest = ParseVersion(tag);

            if (latest is null)
            {
                _logger.Warning($"[AppUpdateService] Could not parse the release tag ('{tag}'). Skipping update check.");
                return null;
            }

            if (latest <= current)
            {
                _logger.Info($"[AppUpdateService] Apploggd is up to date (installed {current}, latest {latest}).");
                return null;
            }

            var releaseUrl = GetString(root, "html_url") ?? ReleasesPageUrl;

            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(GetString(root, "published_at"), out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            // Show the tag as GitHub published it minus the leading "v", not the normalized Version:
            // "1.1" must read as "1.1", not "1.1.0".
            var displayVersion = (tag ?? latest.ToString()).TrimStart('v', 'V');

            _logger.Info($"[AppUpdateService] New version available: {displayVersion} (installed {current}).");
            return new AppUpdateInfo(displayVersion, releaseUrl, publishedAt);
        }
        catch (TaskCanceledException)
        {
            _logger.Warning("[AppUpdateService] Update check timed out.");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warning($"[AppUpdateService] Network error during update check: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.Warning($"[AppUpdateService] Malformed response from GitHub: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error("[AppUpdateService] Unexpected error during update check.", ex);
            return null;
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Extracts the numeric part of a version ("v1.2.3", "1.2.3-beta", "1.2.3+abc" → 1.2.3).
    /// Padded to three components because <see cref="Version"/> treats 1.1 &lt; 1.1.0, whereas "1.1"
    /// and "1.1.0" have to compare as the same version.
    /// </summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var match = Regex.Match(raw, @"\d+(\.\d+){0,3}");
        if (!match.Success) return null;

        var parts = match.Value.Split('.');
        var normalized = parts.Length >= 3
            ? match.Value
            : string.Join('.', parts) + string.Concat(System.Linq.Enumerable.Repeat(".0", 3 - parts.Length));

        return Version.TryParse(normalized, out var version) ? version : null;
    }
}
