using System;
using System.Diagnostics;

namespace BackloggdMirror.Services;

/// <summary>
/// Opens a URL in the user's browser.
///
/// Mind the distinction from <see cref="SystemBrowserDetector"/>: that one looks for
/// *Chromium-based* browsers because those are the only ones Playwright can drive. Here any browser
/// will do (Firefox included), so the primary route is the OS default handler and the Chromium
/// candidates are only the fallback.
/// </summary>
public sealed class ExternalLinkService
{
    private readonly IAppLogger _logger;
    private readonly ISystemBrowserDetector _browserDetector;

    public ExternalLinkService(IAppLogger logger, ISystemBrowserDetector? browserDetector = null)
    {
        _logger = logger;
        _browserDetector = browserDetector ?? new SystemBrowserDetector(logger);
    }

    /// <summary>
    /// Tries to open <paramref name="url"/> in the browser. Returns false when it could not, which
    /// the UI turns into "no browser installed".
    /// </summary>
    public bool TryOpen(string url)
    {
        // 1) OS default handler. UseShellExecute=true is mandatory: without it .NET tries to run the
        //    URL as if it were a binary and always fails. On Windows this opens the default browser;
        //    on Linux it delegates to xdg-open and on macOS to `open`.
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _logger.Info($"[ExternalLinkService] Opened '{url}' with the system default handler.");
            return true;
        }
        catch (Exception ex)
        {
            // Not necessarily an error: there may simply be no browser. Try the fallback before
            // concluding anything.
            _logger.Warning($"[ExternalLinkService] Default handler could not open '{url}': {ex.Message}");
        }

        // 2) Fallback: launch a Chromium found on disk directly. Covers a system that has a browser
        //    installed but no protocol association registered for it.
        foreach (var candidate in _browserDetector.FindCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate.ExecutablePath)) continue;

            try
            {
                Process.Start(new ProcessStartInfo(candidate.ExecutablePath, url) { UseShellExecute = false });
                _logger.Info($"[ExternalLinkService] Opened '{url}' with {candidate.DisplayName}.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[ExternalLinkService] {candidate.DisplayName} could not open '{url}': {ex.Message}");
            }
        }

        _logger.Warning($"[ExternalLinkService] No browser available to open '{url}'.");
        return false;
    }
}
