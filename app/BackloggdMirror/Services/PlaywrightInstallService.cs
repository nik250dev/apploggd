using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace BackloggdMirror.Services;

/// <summary>
/// Ensures the Chromium browser required by Playwright is available on the current machine,
/// downloading and installing it if necessary. Works cross-platform (Windows, Linux, macOS):
/// the Playwright Node driver ships with the application, while the browser binaries are
/// downloaded into the per-user cache (%LOCALAPPDATA%\ms-playwright, ~/.cache/ms-playwright,
/// ~/Library/Caches/ms-playwright) on first run.
/// </summary>
public class PlaywrightInstallService : IBrowserProvisioner
{
    private readonly IAppLogger _logger;
    private readonly ISystemBrowserDetector _systemBrowserDetector;

    /// <summary>Cap per probe attempt, so a browser that never answers cannot hang startup.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cap for the WHOLE probing sequence. Without it, several broken candidates multiplied by
    /// <see cref="ProbeTimeout"/> would turn startup into a wait of minutes.
    /// </summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(45);

    public PlaywrightInstallService(IAppLogger logger, ISystemBrowserDetector? systemBrowserDetector = null)
    {
        _logger = logger;
        _systemBrowserDetector = systemBrowserDetector ?? new SystemBrowserDetector(logger);
    }

    /// <summary>
    /// Decides which browser the process will use, as a cascade and without downloading anything:
    /// <list type="number">
    /// <item>the Chromium Playwright has already downloaded;</item>
    /// <item>failing that, a system Chrome/Edge/Chromium that actually launches;</item>
    /// <item>failing that, <see cref="BrowserResolution.NoBrowserAvailable"/>, so the caller can ask
    /// the user for permission before downloading the ~400 MB.</item>
    /// </list>
    /// Re-evaluated on every startup: no preference is persisted, so the app adapts on its own when
    /// the user installs or uninstalls browsers.
    /// </summary>
    public async Task<BrowserResolution> ResolveBrowserAsync(Action<string>? onProgress = null)
    {
        LogEnvironmentDiagnostics();
        onProgress?.Invoke(LocalizationService.Instance["Browser_Install_Checking"]);

        // 1) Playwright's bundled Chromium.
        if (await IsChromiumInstalledAsync())
        {
            _logger.Info("[PlaywrightInstallService] Using Playwright's bundled Chromium.");
            BrowserLaunch.Configure(BrowserSelection.Bundled);
            return BrowserResolution.PlaywrightChromium;
        }

        // 2) A system browser.
        _logger.Info("[PlaywrightInstallService] Bundled Chromium missing. Probing system browsers...");
        onProgress?.Invoke(LocalizationService.Instance["Browser_Detect_System"]);

        var working = await FindWorkingSystemBrowserAsync();
        if (working != null)
        {
            _logger.Info($"[PlaywrightInstallService] Using system browser: {working}.");
            BrowserLaunch.Configure(working);
            return BrowserResolution.SystemBrowser;
        }

        // 3) Nothing usable. The caller decides whether to ask and download.
        _logger.Warning("[PlaywrightInstallService] No usable browser found; the user must be prompted.");
        BrowserLaunch.Configure(BrowserSelection.Bundled);   // a download would produce the bundled one
        return BrowserResolution.NoBrowserAvailable;
    }

    /// <summary>
    /// Returns the first system browser that launches successfully, or null.
    /// </summary>
    private async Task<BrowserSelection?> FindWorkingSystemBrowserAsync()
    {
        var candidates = _systemBrowserDetector.FindCandidates();
        if (candidates.Count == 0) return null;

        IPlaywright playwright;
        try
        {
            // One driver for the whole probing sequence.
            playwright = await Playwright.CreateAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("[PlaywrightInstallService] Could not start the Playwright driver; cannot probe system browsers.", ex);
            return null;
        }

        using var budget = new CancellationTokenSource(ProbeBudget);
        try
        {
            foreach (var candidate in candidates)
            {
                foreach (var attempt in AttemptsFor(candidate))
                {
                    if (budget.IsCancellationRequested)
                    {
                        _logger.Warning("[PlaywrightInstallService] System-browser probe budget exhausted; giving up.");
                        return null;
                    }

                    if (await ProbeAsync(playwright, attempt)) return attempt;
                }
            }

            return null;
        }
        finally
        {
            playwright.Dispose();
        }
    }

    /// <summary>
    /// Ways to launch a candidate, in order of preference. The channel comes first because it is the
    /// route Playwright supports (it applies the chrome/msedge specific handling); the raw path is
    /// the fallback for when the internal channel resolution fails to find the binary.
    /// </summary>
    private static IEnumerable<BrowserSelection> AttemptsFor(SystemBrowserCandidate candidate)
    {
        if (candidate.Channel != null) yield return BrowserSelection.ForChannel(candidate.Channel);
        if (candidate.ExecutablePath != null) yield return BrowserSelection.ForExecutable(candidate.ExecutablePath);
    }

    /// <summary>
    /// Launches the candidate with the EXACT production options and closes it again.
    ///
    /// Finding the binary on disk is not enough: the registry entry may be stale after an uninstall,
    /// a snap Chromium often cannot write its temporary profile, Linux hosts may be missing system
    /// libraries, a corporate policy may block the launch, and a future Chrome could reject
    /// --headless=new. None of that is visible without actually launching it.
    /// </summary>
    private async Task<bool> ProbeAsync(IPlaywright playwright, BrowserSelection selection)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = BrowserLaunch.HiddenOptions(selection);
            options.Timeout = (float)ProbeTimeout.TotalMilliseconds;

            await using var browser = await playwright.Chromium.LaunchAsync(options);
            if (!browser.IsConnected)
            {
                _logger.Warning($"[PlaywrightInstallService] Probe for {selection} launched but disconnected immediately.");
                return false;
            }

            await browser.CloseAsync();
            _logger.Info($"[PlaywrightInstallService] Probe OK for {selection} in {stopwatch.ElapsedMilliseconds} ms.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[PlaywrightInstallService] Probe failed for {selection} after {stopwatch.ElapsedMilliseconds} ms: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether Chromium is installed and installs it if it is missing.
    /// </summary>
    /// <param name="onProgress">Optional callback invoked with user-facing progress messages.</param>
    /// <returns>A <see cref="BrowserInstallResult"/> describing the outcome.</returns>
    public async Task<BrowserInstallResult> EnsureChromiumInstalledAsync(Action<string>? onProgress = null)
    {
        try
        {
            LogEnvironmentDiagnostics();

            if (await IsChromiumInstalledAsync())
            {
                _logger.Info("[PlaywrightInstallService] Chromium already installed. Skipping download.");
                return BrowserInstallResult.AlreadyInstalled;
            }

            _logger.Info("[PlaywrightInstallService] Chromium not found. Starting download/install...");
            onProgress?.Invoke(LocalizationService.Instance["Browser_Install_Downloading"]);

            // The install downloads a large payload (~400 MB); run it off the UI thread.
            var (exitCode, output) = await Task.Run(RunInstall);

            // Always log the CLI output.
            if (!string.IsNullOrWhiteSpace(output))
            {
                _logger.Info($"[PlaywrightInstallService] Playwright install output:{Environment.NewLine}{output}");
            }

            var (installed, verifyError) = await TryResolveChromiumAsync();
            if (exitCode != 0 || !installed)
            {
                // Distinguish a genuine install/download failure from a post-install
                // verification error: the CLI can exit 0 (browser downloaded fine) yet the
                // managed path resolution still throw — e.g. System.Text.Json reflection
                // disabled by trimming — which is a runtime/config problem, not a bad download.
                if (exitCode == 0 && verifyError != null)
                {
                    _logger.Error("[PlaywrightInstallService] Chromium install reported success (exit code 0) but post-install verification failed. This is a runtime/configuration problem (e.g. System.Text.Json reflection disabled by trimming), not a download failure.", verifyError);
                }
                else
                {
                    _logger.Error($"[PlaywrightInstallService] Chromium install failed (exit code {exitCode}). See the install output logged above for the reason.");
                }
                return BrowserInstallResult.Failed;
            }

            _logger.Info("[PlaywrightInstallService] Chromium installed successfully.");
            return BrowserInstallResult.Installed;
        }
        catch (Exception ex)
        {
            _logger.Error("[PlaywrightInstallService] Unexpected error while installing Chromium.", ex);
            return BrowserInstallResult.Failed;
        }
    }

    /// <summary>
    /// Logs environment details that are useful to diagnose install failures (OS, architecture,
    /// whether the bundled driver is present, the expected browser path, relevant env vars).
    /// </summary>
    private void LogEnvironmentDiagnostics()
    {
        try
        {
            _logger.Info($"[PlaywrightInstallService] BaseDirectory: {AppContext.BaseDirectory}");

            var (nodePath, cliPath) = LocateDriver();
            _logger.Info($"[PlaywrightInstallService] Driver node: {nodePath ?? "NOT FOUND"} (exists={nodePath != null && File.Exists(nodePath)}); cli.js: {cliPath} (exists={File.Exists(cliPath)})");

            var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            var home = Environment.GetEnvironmentVariable("HOME");
            _logger.Info($"[PlaywrightInstallService] PLAYWRIGHT_BROWSERS_PATH={browsersPath ?? "(default)"}; HOME={home ?? "(unset)"}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"[PlaywrightInstallService] Could not gather environment diagnostics: {ex.Message}");
        }
    }

    /// <summary>
    /// Determines whether the Chromium executable is present on disk. This is the source of
    /// truth (rather than a persisted flag) so it stays correct if the user clears the browser
    /// cache or the Playwright version changes.
    /// </summary>
    private async Task<bool> IsChromiumInstalledAsync()
    {
        var (installed, error) = await TryResolveChromiumAsync();
        if (error != null)
        {
            // ExecutablePath can throw when the browser is not installed; treat as "not installed".
            _logger.Warning($"[PlaywrightInstallService] Could not resolve Chromium path (treated as not installed): {error.Message}");
        }
        return installed;
    }

    /// <summary>
    /// Attempts to resolve the Chromium executable path via the Playwright driver and check it
    /// exists on disk. Returns whether it is installed and, on failure, the exception that was
    /// thrown so callers can distinguish a genuine "not installed" state from a runtime/config
    /// error (e.g. System.Text.Json reflection disabled by trimming) instead of masking it.
    /// </summary>
    private async Task<(bool installed, Exception? error)> TryResolveChromiumAsync()
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            var path = playwright.Chromium.ExecutablePath;
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
            _logger.Info($"[PlaywrightInstallService] Expected Chromium path: {path ?? "(null)"} (exists={exists})");
            return (exists, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    /// <summary>
    /// Runs "playwright install chromium". Prefers launching the bundled Node driver as a child
    /// process with redirected stdout/stderr so the real output (and failure reason) is captured
    /// into the log. Falls back to the in-process installer if the driver cannot be located.
    /// </summary>
    private (int exitCode, string output) RunInstall()
    {
        var (nodePath, cliPath) = LocateDriver();

        if (nodePath == null || !File.Exists(nodePath) || !File.Exists(cliPath))
        {
            _logger.Warning($"[PlaywrightInstallService] Bundled Playwright driver not found (node='{nodePath ?? "null"}', cli='{cliPath}'). This usually means the '.playwright' folder was not deployed next to the executable. Falling back to in-process installer (output may not be captured).");
            return RunInstallInProcess();
        }

        EnsureExecutable(nodePath);

        var psi = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        psi.ArgumentList.Add(cliPath);
        psi.ArgumentList.Add("install");
        // Since Playwright 1.49 headless mode lives in a separate binary (chromium-headless-shell)
        // that "install chromium" also downloads by default: ~270 MB extra on disk. This app ALWAYS
        // launches the full Chromium (Headless=false + --headless=new, see BrowserLaunch.HiddenOptions),
        // so that shell would never be opened.
        psi.ArgumentList.Add("--no-shell");
        psi.ArgumentList.Add("chromium");
        // Verbose install diagnostics from Playwright itself.
        psi.Environment["DEBUG"] = "pw:install";

        var sb = new StringBuilder();
        void Append(string? data)
        {
            if (data == null) return;
            lock (sb) sb.AppendLine(data);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Generous timeout: Chromium is ~400 MB and connections vary.
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                _logger.Error("[PlaywrightInstallService] Chromium install timed out after 10 minutes.");
                return (-1, sb.ToString());
            }

            // Ensure the async output handlers have flushed.
            process.WaitForExit();
            return (process.ExitCode, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.Error("[PlaywrightInstallService] Failed to launch the Playwright driver process.", ex);
            return (-1, sb.ToString());
        }
    }

    /// <summary>
    /// Fallback installer that runs the CLI in-process. Captures .NET Console output only
    /// (the Node child process output may bypass this), so it is a last resort.
    /// </summary>
    private static (int exitCode, string output) RunInstallInProcess()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            Console.SetError(capture);
            // --no-shell: same reason as in RunInstall, the headless shell is not downloaded.
            int code = Microsoft.Playwright.Program.Main(new[] { "install", "--no-shell", "chromium" });
            return (code, capture.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    /// <summary>
    /// Locates the bundled Node executable and the Playwright CLI entry point inside the
    /// '.playwright' folder deployed next to the application.
    /// </summary>
    private static (string? nodePath, string cliPath) LocateDriver()
    {
        string driverDir = Path.Combine(AppContext.BaseDirectory, ".playwright");
        string cliPath = Path.Combine(driverDir, "package", "cli.js");
        string nodeDir = Path.Combine(driverDir, "node");
        string nodeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";

        string? nodePath = null;
        if (Directory.Exists(nodeDir))
        {
            // The node binary lives under a platform subfolder (e.g. node/linux-x64/node),
            // or directly under node/ on some layouts. Search both.
            nodePath = Directory.GetFiles(nodeDir, nodeName, SearchOption.AllDirectories).FirstOrDefault();
        }

        return (nodePath, cliPath);
    }

    /// <summary>
    /// On Unix, makes sure the given file has the execute bit set (it can be lost when the
    /// build output is copied/unzipped onto Linux/macOS, which breaks the driver).
    /// </summary>
    private void EnsureExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            var wanted = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (mode != wanted)
            {
                File.SetUnixFileMode(path, wanted);
                _logger.Info($"[PlaywrightInstallService] Added execute permission to driver node: {path}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[PlaywrightInstallService] Could not set execute permission on '{path}': {ex.Message}");
        }
    }
}

/// <summary>
/// Which browser the application will drive, decided at startup.
/// </summary>
public enum BrowserResolution
{
    /// <summary>Playwright's bundled Chromium is installed and will be used.</summary>
    PlaywrightChromium,

    /// <summary>The bundled one is missing, but a system Chrome/Edge/Chromium launched fine.</summary>
    SystemBrowser,

    /// <summary>Nothing usable; the user has to be asked before downloading.</summary>
    NoBrowserAvailable
}

/// <summary>
/// Resolves which browser to use and, if the user authorizes it, downloads Playwright's Chromium.
/// </summary>
public interface IBrowserProvisioner
{
    Task<BrowserResolution> ResolveBrowserAsync(Action<string>? onProgress = null);

    Task<BrowserInstallResult> EnsureChromiumInstalledAsync(Action<string>? onProgress = null);
}

/// <summary>
/// Represents the outcome of a Chromium install/verification attempt.
/// </summary>
public enum BrowserInstallResult
{
    /// <summary>Chromium was already present; nothing was downloaded.</summary>
    AlreadyInstalled,

    /// <summary>Chromium was missing and has been downloaded and installed successfully.</summary>
    Installed,

    /// <summary>The install failed (network error, permissions, unexpected error, etc.).</summary>
    Failed
}
