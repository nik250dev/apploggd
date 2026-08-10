using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Which Chromium binary the whole process should launch.
    ///
    /// Immutable, with Channel and ExecutablePath mutually exclusive by construction: Playwright
    /// rejects being handed both ("Channel and executablePath are mutually exclusive"), so the
    /// private constructors make that mistake impossible.
    /// </summary>
    internal sealed class BrowserSelection
    {
        private BrowserSelection(string? channel, string? executablePath)
        {
            Channel = channel;
            ExecutablePath = executablePath;
        }

        /// <summary>Playwright channel ("chrome", "msedge"), or null.</summary>
        public string? Channel { get; }

        /// <summary>Absolute path to a specific binary, or null.</summary>
        public string? ExecutablePath { get; }

        /// <summary>True when using the Chromium that Playwright downloads.</summary>
        public bool IsBundled => Channel is null && ExecutablePath is null;

        /// <summary>Playwright's own Chromium (the historical behaviour).</summary>
        public static readonly BrowserSelection Bundled = new(null, null);

        public static BrowserSelection ForChannel(string channel) => new(channel, null);

        public static BrowserSelection ForExecutable(string path) => new(null, path);

        public override string ToString() =>
            IsBundled ? "bundled Chromium"
            : Channel is not null ? $"channel '{Channel}'"
            : $"executable '{ExecutablePath}'";
    }

    /// <summary>
    /// Centralizes Chromium launch options so the browser stays off-screen on every platform.
    /// </summary>
    internal static class BrowserLaunch
    {
        // Written once during the startup gate (LoginViewModel) and read from every Task.Run that
        // launches a browser. A single reference to an immutable object, rather than two separate
        // fields, guarantees a half-updated state can never be observed.
        private static BrowserSelection _selection = BrowserSelection.Bundled;

        /// <summary>
        /// Sanitized native UA of the system browser. Resolved once per process: reading it requires
        /// opening a page, and the binary does not change while the app is alive.
        /// </summary>
        private static string? _systemUserAgent;

        /// <summary>Browser the process is using right now.</summary>
        internal static BrowserSelection Current => Volatile.Read(ref _selection);

        /// <summary>
        /// Sets the browser for the whole process. Called once per startup (and again after a logout,
        /// with the same value) before any web call can run.
        /// </summary>
        internal static void Configure(BrowserSelection selection)
        {
            Volatile.Write(ref _selection, selection ?? BrowserSelection.Bundled);
            // The cached UA belongs to the previous browser; a changed selection invalidates it.
            Volatile.Write(ref _systemUserAgent, null);
        }

        /// <summary>
        /// Builds the launch options that keep the browser window off-screen.
        ///
        /// Uses Chromium's "new headless": it renders a real browser (far harder to detect than
        /// classic headless) without opening a window. It behaves the same on Windows, Linux and
        /// macOS, so no off-screen window trick is needed.
        ///
        /// Note: --headless=new is passed by hand and Headless stays false, so that Playwright does
        /// not also add the classic headless flag (--headless).
        /// </summary>
        public static BrowserTypeLaunchOptions HiddenOptions() => HiddenOptions(Current);

        /// <summary>
        /// Same as <see cref="HiddenOptions()"/> but for a specific selection, so the startup probe
        /// can validate a candidate with the EXACT production flags without touching global state.
        /// </summary>
        internal static BrowserTypeLaunchOptions HiddenOptions(BrowserSelection selection)
        {
            return new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--headless=new"
                },
                IgnoreDefaultArgs = new[] { "--enable-automation" },

                // Both null = identical to the historical behaviour (bundled Chromium).
                Channel = selection.Channel,
                ExecutablePath = selection.ExecutablePath
            };
        }

        /// <summary>
        /// User-Agent to inject into a BrowserContext.
        ///
        /// With the bundled Chromium, the usual spoofed UA is returned.
        ///
        /// With a system browser we start from its REAL UA — so the Sec-CH-UA and
        /// Sec-CH-UA-Full-Version-List client hints, which the browser emits with its true version,
        /// keep matching the UA — and only strip the "Headless" marker. Under --headless=new
        /// Chromium announces itself as "HeadlessChrome/NNN", and the BotStopper (Anubis) guarding
        /// Backloggd rejects that string outright: it answers 200 with the "access denied" page, so
        /// the real content NEVER arrives no matter how long we wait.
        ///
        /// Requires an already-launched browser, because the native UA can only be read by running
        /// JavaScript on a page.
        /// </summary>
        public static async Task<string?> ContextUserAgentAsync(IBrowser browser, string spoofedUserAgent)
        {
            if (Current.IsBundled) return spoofedUserAgent;

            var cached = Volatile.Read(ref _systemUserAgent);
            if (cached != null) return cached;

            var native = await TryReadNativeUserAgentAsync(browser);
            if (native == null)
            {
                // With no native UA, the spoofed one still beats letting the browser's through: at
                // the very least it guarantees the marker that triggers the block is absent.
                return spoofedUserAgent;
            }

            var sanitized = native.Replace("HeadlessChrome/", "Chrome/", StringComparison.Ordinal);
            Volatile.Write(ref _systemUserAgent, sanitized);
            return sanitized;
        }

        /// <summary>
        /// Reads navigator.userAgent on a blank page, without navigating anywhere. Returns null on
        /// any failure: ending up without a UA must not bring down the caller's operation.
        /// </summary>
        private static async Task<string?> TryReadNativeUserAgentAsync(IBrowser browser)
        {
            IBrowserContext? probe = null;
            try
            {
                probe = await browser.NewContextAsync();
                var page = await probe.NewPageAsync();
                return await page.EvaluateAsync<string>("() => navigator.userAgent");
            }
            catch
            {
                return null;
            }
            finally
            {
                if (probe != null)
                {
                    try { await probe.CloseAsync(); } catch { /* the probe context is disposable */ }
                }
            }
        }
    }
}
