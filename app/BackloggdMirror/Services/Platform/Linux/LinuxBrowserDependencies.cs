using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// What the user needs to make Chromium start: the libraries it could not load and, on apt-based
/// distros, the install-deps command to run (with sudo, so the app cannot run it itself).
/// </summary>
public sealed record BrowserDependencyReport(IReadOnlyList<string> MissingLibraries, string? InstallCommand);

/// <summary>
/// Chromium's system libraries on Linux. A fresh distro may lack some of them (libnss3, libnspr4,
/// libasound2...): the download still succeeds and the binary is on disk, but the loader aborts it
/// at launch (exit code 127), which Playwright reports as "browser has been closed".
/// </summary>
internal static class LinuxBrowserDependencies
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private static readonly Regex LoaderError = new(@"error while loading shared libraries: ([^:\s]+):", RegexOptions.Compiled);

    private static readonly Regex LddNotFound = new(@"^\s*(\S+)\s+=>\s+not found", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Launches the bundled Chromium with the production options and returns why the system cannot
    /// run it, or null if it starts (or fails for any other reason, which keeps the old behaviour).
    /// </summary>
    public static async Task<BrowserDependencyReport?> CheckAsync(IAppLogger logger, string? nodePath, string cliPath)
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            var options = BrowserLaunch.HiddenOptions(BrowserSelection.Bundled);
            options.Timeout = (float)ProbeTimeout.TotalMilliseconds;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await using var browser = await playwright.Chromium.LaunchAsync(options);
                await browser.CloseAsync();
                logger.Info($"[LinuxBrowserDependencies] Bundled Chromium starts fine ({stopwatch.ElapsedMilliseconds} ms).");
                return null;
            }
            catch (Exception ex) when (IsMissingLibrariesError(ex.Message))
            {
                var libraries = FindMissingLibraries(playwright.Chromium.ExecutablePath, ex.Message);
                string? command = File.Exists("/usr/bin/apt-get") && nodePath != null
                    ? $"sudo \"{nodePath}\" \"{cliPath}\" install-deps chromium"
                    : null;
                logger.Error($"[LinuxBrowserDependencies] Bundled Chromium cannot start: missing system libraries ({string.Join(", ", libraries)}). Install command offered: {command ?? "(none, not an apt-based distro)"}");
                return new BrowserDependencyReport(libraries, command);
            }
            catch (Exception ex)
            {
                logger.Warning($"[LinuxBrowserDependencies] Bundled Chromium probe failed after {stopwatch.ElapsedMilliseconds} ms for a reason other than missing libraries; continuing: {ex.Message}");
                return null;
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[LinuxBrowserDependencies] Could not start the Playwright driver to probe Chromium: {ex.Message}");
            return null;
        }
    }

    public static bool IsMissingLibrariesError(string message) =>
        message.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("cannot open shared object file", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Host system is missing dependencies", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// All the libraries Chromium cannot resolve. The loader only names the first one, so ldd is
    /// asked for the full list; the loader's own message is the fallback.
    /// </summary>
    private static IReadOnlyList<string> FindMissingLibraries(string? executablePath, string errorMessage)
    {
        var libraries = new SortedSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
        {
            try
            {
                var psi = new ProcessStartInfo("ldd") { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add(executablePath);
                using var ldd = Process.Start(psi);
                if (ldd != null)
                {
                    string output = ldd.StandardOutput.ReadToEnd();
                    ldd.WaitForExit(5000);
                    foreach (Match m in LddNotFound.Matches(output)) libraries.Add(m.Groups[1].Value);
                }
            }
            catch
            {
                // Without ldd, the loader's message still names one library.
            }
        }

        foreach (Match m in LoaderError.Matches(errorMessage)) libraries.Add(m.Groups[1].Value);

        return libraries.ToList();
    }
}
