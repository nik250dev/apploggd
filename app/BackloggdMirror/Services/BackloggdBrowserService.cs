using Microsoft.Playwright;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System.IO;
using System.Net.Http;

namespace BackloggdMirror.Services
{
    public class BackloggdBrowserService : IBackloggdBrowserService
    {
        private readonly IAppLogger? _logger;

        public BackloggdBrowserService(IAppLogger? logger = null)
        {
            _logger = logger;
        }

        private static readonly string[] UserAgents = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:121.0) Gecko/20100101 Firefox/121.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15"
        };

        private string GetRandomUserAgent()
        {
            var random = new Random();
            return UserAgents[random.Next(UserAgents.Length)];
        }

        /// <summary>Root of Backloggd's current CMP (Google Funding Choices).</summary>
        private const string FundingChoicesRoot = ".fc-consent-root";

        /// <summary>Root of the previous CMP (Quantcast). Kept in case it is served again.</summary>
        private const string QuantcastRoot = "#qc-cmp2-ui";

        /// <summary>
        /// Grace period for the consent modal to show up. A third-party script injects it ~1 s AFTER
        /// DOMContentLoaded, so checking whether it is already in the DOM is not enough.
        /// </summary>
        private const int ConsentModalTimeoutMs = 8000;

        /// <summary>
        /// Closes the cookie consent modal if it appears.
        ///
        /// Backloggd has switched CMP: it used to serve Quantcast (<c>#qc-cmp2-ui</c>) and now serves
        /// Google Funding Choices (<c>.fc-consent-root</c>). Both are supported because the site
        /// picks the CMP and may switch again.
        ///
        /// This is critical rather than cosmetic: Funding Choices also mounts a full-screen
        /// <c>.fc-dialog-overlay</c> that intercepts clicks even when the dialog itself looks
        /// harmless. Left open, any later ClickAsync (the log button, the login submit) retries until
        /// its timeout, and the error surfacing upstream talks about a selector that "cannot be
        /// clicked" instead of the modal, which is where the fault actually is.
        /// </summary>
        private async Task HandleCookieModalAsync(IPage page)
        {
            if (!await WaitForConsentModalAsync(page)) return;

            try
            {
                if (await page.Locator(FundingChoicesRoot).CountAsync() > 0)
                {
                    await DismissFundingChoicesModalAsync(page);
                }
                else
                {
                    await DismissQuantcastModalAsync(page);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackloggdBrowserService] Error handling cookie modal: {ex.Message}");
                _logger?.Warning($"[HandleCookieModal] The consent modal appeared but could not be closed: {ex.Message}. Later clicks may be blocked by its overlay.");
            }
        }

        /// <summary>
        /// Waits for the modal and reports whether it arrived. Waiting is kept separate from closing
        /// on purpose: timing out here is the NORMAL case (consent already given, or no CMP served)
        /// and must stay silent, whereas failing to close one is worth logging.
        /// </summary>
        private static async Task<bool> WaitForConsentModalAsync(IPage page)
        {
            try
            {
                await page.WaitForSelectorAsync(
                    $"{QuantcastRoot}, {FundingChoicesRoot}",
                    new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = ConsentModalTimeoutMs });
                return true;
            }
            catch (PlaywrightException)
            {
                return false;
            }
        }

        /// <summary>
        /// Closes the Google Funding Choices CMP by rejecting consent (the same path already taken
        /// with Quantcast: personalised advertising is of no use here). Waits for the root to detach
        /// from the DOM, which is what guarantees the click-swallowing overlay left with it.
        /// </summary>
        private static async Task DismissFundingChoicesModalAsync(IPage page)
        {
            Console.WriteLine("[BackloggdBrowserService] Consent modal (Funding Choices) detected, handling...");

            // "Do not consent" is preferred; when the CMP is served without it (it varies by
            // visitor region), accepting also dismisses the overlay, which is what blocks us.
            var reject = page.Locator(".fc-cta-do-not-consent");
            var button = await reject.CountAsync() > 0 ? reject : page.Locator(".fc-cta-consent");

            await button.First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });

            await page.WaitForSelectorAsync(
                FundingChoicesRoot,
                new PageWaitForSelectorOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });

            Console.WriteLine("[BackloggdBrowserService] Consent modal handled.");
        }

        /// <summary>Legacy Quantcast CMP, kept exactly as it used to work.</summary>
        private static async Task DismissQuantcastModalAsync(IPage page)
        {
            Console.WriteLine("[BackloggdBrowserService] Consent modal (Quantcast) detected, handling...");
            await page.Locator($"{QuantcastRoot} .qc-cmp2-link-inline").ClickAsync();

            await page.WaitForSelectorAsync(".qc-cmp2-consent-info", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

            await page.Locator(".qc-cmp2-header-links").First.ClickAsync();
            await page.Locator(".css-1jqk1n3:not(.qc-cmp2-hide-desktop)").ClickAsync();
            Console.WriteLine("[BackloggdBrowserService] Consent modal handled.");
        }

        /// <summary>
        /// Cheap safety net (one DOM query, no waiting) for use right before a click that must not
        /// fail: the CMP is injected by a third-party script and can arrive late, after
        /// <see cref="HandleCookieModalAsync"/> has already looked.
        /// </summary>
        private async Task DismissConsentOverlayIfPresentAsync(IPage page)
        {
            try
            {
                if (await page.Locator(FundingChoicesRoot).CountAsync() > 0)
                {
                    await DismissFundingChoicesModalAsync(page);
                }
            }
            catch (Exception ex)
            {
                // If it cannot be closed here, carry on: the click below will report what happens.
                Console.WriteLine($"[BackloggdBrowserService] Late consent overlay not dismissed: {ex.Message}");
                _logger?.Warning($"[DismissConsentOverlay] A late consent overlay appeared and could not be closed: {ex.Message}. The click that follows may hit the overlay instead of the page.");
            }
        }

        /// <summary>
        /// Logs in by driving a real browser through the form, which is the only way past the
        /// anti-bot challenge. On success returns the canonical username plus the session cookies;
        /// on failure returns a typed error string that the ViewModel maps to a localized message.
        /// </summary>
        public async Task<(string? username, System.Net.CookieContainer? cookies, string? errorMessage)> LoginAsync(string username, string password, bool rememberMe)
        {
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, GetRandomUserAgent())
                });
                var page = await context.NewPageAsync();

                await page.GotoAsync("https://backloggd.com/login/");

                Console.WriteLine("[BackloggdBrowserService] Waiting for Bunny Shield challenge to pass...");
                _logger?.Info("[LoginAsync] Login page requested. Waiting up to 60 s for the anti-bot challenge to resolve and the form to appear.");
                // The login form appearing is the signal that the challenge let us through.
                try
                {
                    await page.WaitForSelectorAsync("#user_login", new PageWaitForSelectorOptions { Timeout = 60000 });
                }
                catch (System.TimeoutException)
                {
                    // A challenge unsolved after 60 s is not slowness, it is a block.
                    var loginBlock = await AntiBotDetection.DetectAsync(page);
                    if (loginBlock != null)
                    {
                        Console.WriteLine($"[BackloggdBrowserService] Anti-bot block: {loginBlock}");
                        _logger?.Error($"[LoginAsync] Request {loginBlock}. The challenge did not resolve within 60 s and the login form was never reached.");
                        return (null, null, "BlockedByAntiBot");
                    }

                    throw;
                }

                var pageTitle = await page.TitleAsync();
                Console.WriteLine($"[BackloggdBrowserService] Page Title after redirect: {pageTitle}");

                await HandleCookieModalAsync(page);

                Console.WriteLine("[BackloggdBrowserService] Filling credentials...");

                await page.FillAsync("#user_login", username);
                await page.FillAsync("#user_password", password);

                // Click "Remember me" if it exists
                if (rememberMe)
                {
                    var rememberMeCheckbox = page.Locator("#user_remember_me");
                    if (await rememberMeCheckbox.CountAsync() > 0)
                    {
                        await rememberMeCheckbox.CheckAsync();
                        Console.WriteLine("[BackloggdBrowserService] Checked 'Remember Me'");
                    }
                }

                // Scoped to #new_user: a bare button[type='submit'] would also match the navbar
                // search button.
                await page.ClickAsync("#new_user button[type='submit']");

                Console.WriteLine("[BackloggdBrowserService] Waiting for login completion...");
                try
                {
                    // The welcome banner is the success signal: it only renders once authenticated.
                    await page.WaitForSelectorAsync("#welcome-banner", new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch
                {
                    Console.WriteLine("[BackloggdBrowserService] Login verification timed out. Checking for errors...");
                    _logger?.Warning("[LoginAsync] The welcome banner never appeared within 10 s of submitting the form. Looking for a block or a form error to tell the two apart.");

                    // Check for a block before blaming the credentials: if the POST was answered with
                    // a protection screen there is neither a banner nor an error message to read, and
                    // telling the user their password is wrong would simply be false.
                    var submitBlock = await AntiBotDetection.DetectAsync(page);
                    if (submitBlock != null)
                    {
                        Console.WriteLine($"[BackloggdBrowserService] Anti-bot block: {submitBlock}");
                        _logger?.Error($"[LoginAsync] Request {submitBlock}. The form was submitted but the response was the protection screen, not the login result.");
                        return (null, null, "BlockedByAntiBot");
                    }

                    var errorMsg = await page.Locator(".alert-backloggd-error").TextContentAsync();
                    if (!string.IsNullOrEmpty(errorMsg))
                    {
                        Console.WriteLine($"[BackloggdBrowserService] Login failed: {errorMsg}");
                        _logger?.Warning($"[LoginAsync] Backloggd rejected the credentials with: {errorMsg.Trim()}");
                        return (null, null, "InvalidCredentials");
                    }
                }

                // Prefer the username Backloggd itself reports: the user may have typed an email, or
                // different casing, and every later profile URL is built from this value.
                var profileLink = await page.GetAttributeAsync("#welcome-banner a", "href");
                string resolvedUsername = username;
                if (!string.IsNullOrEmpty(profileLink))
                {
                    // href is like "/u/Username"
                    var match = System.Text.RegularExpressions.Regex.Match(profileLink, @"/u/([^/]+)");
                    if (match.Success)
                    {
                        resolvedUsername = match.Groups[1].Value;
                    }
                }

                var playwrightCookies = await context.CookiesAsync();
                var cookieContainer = new System.Net.CookieContainer();
                foreach (var cookie in playwrightCookies)
                {
                    cookieContainer.Add(CookieConversion.ToNet(cookie));
                }

                Console.WriteLine($"[BackloggdBrowserService] Login successful. Resolved user: {resolvedUsername}. Cookies: {playwrightCookies.Count}");
                // The count only: cookie values are the session itself and must never reach the log.
                _logger?.Info($"[LoginAsync] Login successful. Resolved user: {resolvedUsername}. Session cookies received: {playwrightCookies.Count}.");
                return (resolvedUsername, cookieContainer, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackloggdBrowserService] Login Exception: {ex.Message}");
                // Logged here rather than per branch below: every return that follows is this same
                // failure, only translated into the reason the ViewModel shows the user.
                _logger?.Error("[LoginAsync] The login flow threw before a session could be established.", ex);
                if (ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Browser has been closed", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Target page, context or browser has been closed", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, "BrowserClosed");
                }

                if (ex is TimeoutException || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, "TimeoutError");
                }

                if (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, "BrowserExecutableNotFound");
                }

                // Network-level failures while navigating to backloggd.com. These are Chromium
                // net:: errors (connection reset, DNS failure, offline, refused, unreachable...)
                // and mean we never reached the site, so they are NOT a credentials problem.
                if (ex.Message.Contains("net::ERR", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("NS_ERROR", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("ERR_INTERNET_DISCONNECTED", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("ERR_CONNECTION", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, "NetworkError");
                }

                // On Linux the browser binary can be present but fail to launch because the host
                // is missing shared libraries (libnss3, libatk, ...). We do not auto-install OS
                // dependencies (that would require root); instead we surface a clear message.
                if (ex.Message.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("cannot open shared object file", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Host system is missing dependencies", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, "BrowserDepsMissing");
                }

                return (null, null, $"UnknownError: {ex}");
            }
        }

        /// <summary>
        /// Dead code: opens the login page and waits a minute for a human to type into it. Predates
        /// the automated <see cref="LoginAsync"/> and is no longer called from anywhere.
        /// </summary>
        public async Task PerformLogin()
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

            var page = await browser.NewPageAsync();
            await page.GotoAsync("https://backloggd.com/login");
            await HandleCookieModalAsync(page);
            await page.WaitForTimeoutAsync(60000);
        }

        /// <summary>
        /// Writes a play session into the user's Backloggd journal, by driving the site's own log
        /// editor rather than any API.
        ///
        /// The time is ACCUMULATED onto whatever today's playthrough already holds, never
        /// overwritten — see the sum below. That is what makes several sessions of the same game in
        /// one day add up instead of each replacing the last.
        ///
        /// Errors propagate to the caller, which turns them into a toast; the catch here only
        /// enriches the log.
        /// </summary>
        public async Task RegisterGame(string gameName, System.Net.CookieContainer cookieContainer, int gamePlayDateHours, int gamePlayDateMinutes, string? gameUrl = null)
        {
            IPlaywright playwright = null;
            IBrowser browser = null;

            Console.WriteLine($"[RegisterGame] Starting registration for {gameName} (URL: {gameUrl ?? "search"})...");
            _logger?.Info($"[RegisterGame] Starting registration for {gameName} (URL: {gameUrl ?? "search"})...");
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, GetRandomUserAgent()),
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
            });

            await context.AddCookiesAsync(CookieConversion.ToPlaywright(cookieContainer));

            var page = await context.NewPageAsync();

            if (!string.IsNullOrEmpty(gameUrl))
            {
                // Callers supply the slug in either shape ("slug" or "/games/slug/") depending on
                // whether it came from the local database or from the manual game picker.
                string slug = gameUrl.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
                var fullUrl = $"https://backloggd.com/games/{slug}/";
                Console.WriteLine($"[RegisterGame] Navigating directly to: {fullUrl}");
                _logger?.Info($"[RegisterGame] Navigating directly to: {fullUrl}");
                await page.GotoAsync(fullUrl);
            }
            else
            {
                // No slug: the game was never identified, so it has to be found by name.
                Console.WriteLine($"[RegisterGame] No URL provided, searching for: {gameName}");
                _logger?.Info($"[RegisterGame] No URL provided, searching for: {gameName}");
                await page.GotoAsync("https://backloggd.com/search/games/");
            }

            await HandleCookieModalAsync(page);

            if (string.IsNullOrEmpty(gameUrl))
            {
                await page.FillAsync("#nav-bar-search", gameName);
                await page.ClickAsync(".search-btn");
                await page.WaitForSelectorAsync("#search-results");
                // Restricted to .main_game so the pick is the game itself, not one of its DLCs or
                // expansions, which the search returns alongside it.
                await page.Locator("#search-results :has(.main_game) .game-name").First.ClickAsync();
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                var logButtonSelector = ".side-section .d-none.d-md-flex .log-editor-btn";
                Console.WriteLine($"[RegisterGame] Waiting for log button: {logButtonSelector}");
                await page.WaitForSelectorAsync(logButtonSelector);

                await DismissConsentOverlayIfPresentAsync(page);

                await page.ClickAsync(logButtonSelector);

                await page.ClickAsync("#journal-nav");
                Console.WriteLine($"[RegisterGame] Clicked journal-nav");

                await page.ClickAsync("#jump-to-today");

                await page.WaitForSelectorAsync(".fc-day-today", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });

                await page.Locator(".fc-day-today").ClickAsync(new LocatorClickOptions { Force = true });
                Console.WriteLine($"[RegisterGame] Clicked today in calendar");

                // The first click sometimes only selects the day without opening the playthrough
                // modal, so give it a moment and click again if it did not appear.
                await page.WaitForTimeoutAsync(500);

                if (!await page.IsVisibleAsync("#playthrough-modal-content"))
                {
                    await page.Locator(".fc-day-today").ClickAsync(new LocatorClickOptions { Force = true });
                    Console.WriteLine($"[RegisterGame] Clicked span today Played");
                }

                await page.WaitForSelectorAsync("#playthrough-modal-content", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });

                // Read what today's playthrough already holds. These fields are empty on a first
                // session and carry the running total on any later one.
                string currentPlayDateHours = await page.InputValueAsync("#play_date_hours");
                string currentPlayDateMinutes = await page.InputValueAsync("#play_date_minutes");

                int hours = 0;
                int minutes = 0;

                if (!string.IsNullOrEmpty(currentPlayDateHours))
                {
                    int.TryParse(currentPlayDateHours, out hours);
                }

                if (!string.IsNullOrEmpty(currentPlayDateMinutes))
                {
                    int.TryParse(currentPlayDateMinutes, out minutes);
                }

                int currentTotalMinutes = hours * 60 + minutes;

                Console.WriteLine($"[RegisterGame] Current play date hours: {currentPlayDateHours}");
                Console.WriteLine($"[RegisterGame] Current play date minutes: {currentPlayDateMinutes}");

                // ADD to the existing total rather than replacing it. Overwriting here would discard
                // every earlier session of the same game on the same day.
                int gameTotalMinutes = (gamePlayDateHours * 60 + gamePlayDateMinutes) + currentTotalMinutes;

                // The form takes hours and minutes separately, so carry the overflow.
                int gameTotalHours = gameTotalMinutes / 60;
                gameTotalMinutes = gameTotalMinutes % 60;

                await page.FillAsync("#play_date_hours", gameTotalHours.ToString());
                await page.FillAsync("#play_date_minutes", gameTotalMinutes.ToString());

                await page.ClickAsync("#play-date-update");

                await page.WaitForSelectorAsync("#playthrough-modal-content", new PageWaitForSelectorOptions { State = WaitForSelectorState.Hidden });

                await page.ClickAsync("#btn-save-log");

                await page.WaitForSelectorAsync("#journal-game-modal", new PageWaitForSelectorOptions { State = WaitForSelectorState.Hidden });

                _logger?.Info($"[RegisterGame] Successfully registered '{gameName}'.");
            }
            catch (Exception ex)
            {
                // The flow is unchanged — the error still reaches the caller. This only records in
                // the log whether a block was what stopped the registration, because from the
                // outside it looks like a selector that never appeared and sends you hunting for
                // the fault in the wrong place.
                var block = await AntiBotDetection.DetectAsync(page);
                if (block != null)
                {
                    Console.WriteLine($"[RegisterGame] Anti-bot block: {block}");
                    _logger?.Error($"[RegisterGame] Request {block}. Registering '{gameName}' was interrupted by the protection, not by a page failure.");
                }
                else
                {
                    _logger?.Error($"[RegisterGame] Failed to register '{gameName}': {ex.Message}", ex);
                }

                throw;
            }
            finally
            {
                // This method owns its browser (unlike the ones using 'using'), so it has to close
                // it on every path, including the rethrow above.
                Console.WriteLine("[RegisterGame] Cleaning up browser session.");
                await browser.CloseAsync();
                playwright.Dispose();
            }
        }



        /// <summary>
        /// Scrapes the user's journal for the most recent entries shown on the home screen.
        ///
        /// Null and empty mean different things and callers rely on it: an empty list is "this
        /// profile has no entries", whereas null is "the journal could not be read" (403 or a block).
        /// Collapsing the two would tell a user with a full journal that they have played nothing.
        /// </summary>
        public async Task<List<BackloggdMirror.Models.JournalEntry>?> GetLastPlayedGames(string username, System.Net.CookieContainer cookieContainer)
        {
            _logger?.Info($"[GetLastPlayedGames] Fetching last played games for user '{username}'");
            var journalEntries = new List<BackloggdMirror.Models.JournalEntry>();
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, GetRandomUserAgent())
                });

                var cookies = CookieConversion.ToPlaywright(cookieContainer);
                Console.WriteLine($"[GetLastPlayedGames] Transferring {cookies.Count} cookies to Playwright.");
                await context.AddCookiesAsync(cookies);

                var page = await context.NewPageAsync();
                var url = $"https://backloggd.com/u/{username}/journal/";

                Console.WriteLine($"[BackloggdBrowserService] Navigating to {url}");
                _logger?.Info($"[GetLastPlayedGames] Reading the journal of '{username}'.");
                var response = await page.GotoAsync(url);

                if (response != null && response.Status == 403)
                {
                    Console.WriteLine($"[BackloggdBrowserService] ERROR: 403 Forbidden detected via Playwright!");
                    _logger?.Error("[GetLastPlayedGames] ERROR: 403 Forbidden detected via Playwright!");
                    return null;
                }

                try
                {
                    await page.WaitForSelectorAsync(".journal_entry", new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch (Exception ex)
                {
                    // The selector is equally absent when the profile has no entries and when the
                    // journal was never reached. Telling the two apart is what decides between
                    // returning an empty list and returning null.
                    var block = await AntiBotDetection.DetectAsync(page);
                    if (block != null)
                    {
                        Console.WriteLine($"[BackloggdBrowserService] Anti-bot block: {block}");
                        _logger?.Error($"[GetLastPlayedGames] Request {block}. Backloggd served its protection screen instead of the journal, so there is nothing to read.");
                        return null;
                    }

                    Console.WriteLine("[BackloggdBrowserService] No journal entries found or timeout.");
                    _logger?.Warning($"[GetLastPlayedGames] No journal entries found or timeout. Exception: {ex.Message}");
                    return journalEntries;
                }

                var entries = await page.Locator(".journal_entry").AllAsync();
                string currentMonthYear = "";
                int count = 0;

                foreach (var entry in entries)
                {
                    // Six is what the home screen shows; reading further would be wasted work.
                    if (count >= 6) break;

                    // A .journal_entry is either a month heading or a game. The heading carries the
                    // month and year that the game rows below it omit, so it has to be remembered
                    // as the loop goes: the rows only give a day number.
                    var monthYearLoc = entry.Locator(".month-year-date h4");
                    if (await monthYearLoc.CountAsync() > 0)
                    {
                        currentMonthYear = await monthYearLoc.InnerTextAsync();
                        currentMonthYear = currentMonthYear.Trim();
                    }

                    var resultLoc = entry.Locator(".result");
                    if (await resultLoc.CountAsync() > 0)
                    {
                        var gameName = await resultLoc.Locator(".game-name a").First.InnerTextAsync();
                        var coverImage = await resultLoc.Locator(".card-img").First.GetAttributeAsync("src");

                        // An entry can carry several .journal-time nodes (played time, "Started
                        // on...", and so on) with nothing in the markup to tell them apart, so the
                        // right one is picked by shape: it holds a duration and is not the
                        // "Started" label. "-" stands for an entry logged without any time.
                        var timeLocs = await resultLoc.Locator(".journal-time").AllAsync();
                        string playTime = "-";
                        foreach (var timeLoc in timeLocs)
                        {
                            var text = await timeLoc.InnerTextAsync();
                            if (text.Contains("h") || text.Contains("m") || text.Contains("s"))
                            {
                                if (!text.Contains("Started"))
                                {
                                    playTime = text.Trim();
                                    break;
                                }
                            }
                        }

                        var dayText = await resultLoc.Locator(".date-day").TextContentAsync();
                        dayText = dayText?.Trim() ?? "";

                        string fullDateStr = $"{dayText} {currentMonthYear}";
                        string relativeDate = fullDateStr;

                        if (DateTime.TryParse(fullDateStr, out DateTime date))
                        {
                            relativeDate = GetRelativeTime(date);
                        }

                        var journalEntry = new BackloggdMirror.Models.JournalEntry
                        {
                            GameName = gameName,
                            CoverImage = coverImage ?? "",
                            PlayTime = playTime,
                            RegistrationDate = relativeDate
                        };

                        journalEntries.Add(journalEntry);
                        count++;
                    }
                }

                _logger?.Info($"[GetLastPlayedGames] Successfully retrieved {journalEntries.Count} games.");
                return journalEntries;
            }
            catch (Exception ex)
            {
                _logger?.Error($"[GetLastPlayedGames] Unexpected error fetching last played games: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Renders a date as localized relative text ("today", "3 days ago"). Deliberately coarse:
        /// months and years are approximated as 30- and 365-day buckets, which is accurate enough
        /// for a "recently played" list and avoids calendar arithmetic nobody would notice.
        /// </summary>
        private string GetRelativeTime(DateTime date)
        {
            var diff = DateTime.Now.Date - date.Date;
            int days = diff.Days;
            var loc = LocalizationService.Instance;

            if (days == 0) return loc["Time_Today"];
            if (days == 1) return loc["Time_Yesterday"];
            if (days < 7) return string.Format(loc["Time_DaysAgo"], days);
            if (days < 30)
            {
                int weeks = days / 7;
                return weeks == 1 ? loc["Time_OneWeekAgo"] : string.Format(loc["Time_WeeksAgo"], weeks);
            }
            if (days < 365)
            {
                int months = days / 30;
                return months == 1 ? loc["Time_OneMonthAgo"] : string.Format(loc["Time_MonthsAgo"], months);
            }
            int years = days / 365;
            return years == 1 ? loc["Time_OneYearAgo"] : string.Format(loc["Time_YearsAgo"], years);
        }

        /// <summary>
        /// Dead code: covers now come from IGDB via <see cref="GameDataService"/>, which needs no
        /// browser. Nothing calls this, and it is only reachable through the interface.
        /// </summary>
        public async Task<(string? Title, Bitmap? Cover)> GetGameCoverAsync(string gameName)
        {
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

                var userAgent = GetRandomUserAgent();
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, userAgent)
                });

                var page = await context.NewPageAsync();

                // 1. Go to search page
                Console.WriteLine($"[GetGameCoverAsync] Searching for: {gameName}");
                await page.GotoAsync("https://backloggd.com/search/games/");

                // 2. Search for game
                await page.FillAsync("#nav-bar-search", gameName);
                await page.ClickAsync(".search-btn");

                // 3. Wait for results
                await page.WaitForSelectorAsync("#search-results");

                // Click the first result that matches the criteria
                // This triggers navigation, so we must wait for the next page to load
                var firstGame = page.Locator("#search-results :has(.main_game) .game-name").First;
                if (await firstGame.CountAsync() == 0)
                {
                    Console.WriteLine("[GetGameCoverAsync] No game found.");
                    _logger?.Warning($"[GetGameCoverAsync] Backloggd's search returned no game for '{gameName}'. The session is shown without a cover.");
                    return (null, null);
                }

                // Read the title before clicking, since the click navigates away. The subtitle
                // (release year) is nested inside the heading, so plain innerText would glue it to
                // the name; removing it from a detached clone leaves the real page untouched.
                var exactTitle = await firstGame.EvaluateAsync<string>(@"el => {
                    const h3 = el.querySelector('h3');
                    if (!h3) return el.innerText;

                    const clone = h3.cloneNode(true);
                    const subtitle = clone.querySelector('.subtitle-text');
                    if (subtitle) subtitle.remove();
                    
                    return clone.textContent;
                }");

                exactTitle = exactTitle?.Trim();
                Console.WriteLine($"[GetGameCoverAsync] Found exact title: {exactTitle}");

                await firstGame.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

                // 4. Extract cover image URL
                // The cover is usually in an <img> with class "card-img"
                var coverImg = page.Locator(".card-img").First;
                var src = await coverImg.GetAttributeAsync("src");

                if (string.IsNullOrEmpty(src))
                {
                    Console.WriteLine("[GetGameCoverAsync] Cover image source not found.");
                    _logger?.Warning($"[GetGameCoverAsync] Found '{exactTitle}' on Backloggd but its page carried no cover image. The session is shown without one.");
                    return (exactTitle, null);
                }

                Console.WriteLine($"[GetGameCoverAsync] Found cover URL: {src}");

                // 5. Download image and convert to Bitmap
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
                var imageBytes = await httpClient.GetByteArrayAsync(src);

                using var stream = new MemoryStream(imageBytes);
                return (exactTitle, new Bitmap(stream));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetGameCoverAsync] Error: {ex.Message}");
                _logger?.Error($"[GetGameCoverAsync] Looking up the cover for '{gameName}' failed. The session is shown without a cover or a resolved title.", ex);
                return (null, null);
            }
        }

        /// <summary>
        /// Backs the manual game picker, used when detection could not identify what was played.
        /// Always returns a list, empty on failure: the picker shows "no results" either way, and
        /// the log records whether a block was the real cause.
        /// </summary>
        public async Task<List<BackloggdMirror.Models.GameSearchResult>> SearchGamesAsync(string query)
        {
            var results = new List<BackloggdMirror.Models.GameSearchResult>();
            try
            {
                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

                var userAgent = GetRandomUserAgent();
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, userAgent)
                });

                var page = await context.NewPageAsync();

                Console.WriteLine($"[SearchGamesAsync] Searching for: {query}");
                await page.GotoAsync($"https://backloggd.com/search/games/{Uri.EscapeDataString(query)}/");

                try
                {
                    await page.WaitForSelectorAsync("#search-results .result", new PageWaitForSelectorOptions { Timeout = 5000 });
                }
                catch
                {
                    // Same trap as the journal: "no results" and "we never saw the page" look alike
                    // from here, and only one of them is the search's own doing.
                    var block = await AntiBotDetection.DetectAsync(page);
                    if (block != null)
                    {
                        Console.WriteLine($"[SearchGamesAsync] Anti-bot block: {block}");
                        _logger?.Error($"[SearchGamesAsync] Request {block}. The search for '{query}' never ran.");
                    }
                    else
                    {
                        Console.WriteLine("[SearchGamesAsync] No results found or timeout.");
                        _logger?.Warning($"[SearchGamesAsync] No results rendered for '{query}' within 5 s, and no block was detected. The picker will show an empty grid.");
                    }
                    return results;
                }

                var resultElements = await page.Locator("#search-results .result").AllAsync();

                foreach (var el in resultElements)
                {
                    // Capped at 20: each result costs several DOM round-trips, and the picker is
                    // meant for finding a known game, not browsing the catalogue.
                    if (results.Count >= 20) break;

                    var gameNameEl = el.Locator(".game-name h3");
                    if (await gameNameEl.CountAsync() == 0) continue;

                    // Same detached-clone trick as above, to drop the release-year subtitle.
                    var title = await gameNameEl.EvaluateAsync<string>(@"el => {
                        const clone = el.cloneNode(true);
                        const subtitle = clone.querySelector('.subtitle-text');
                        if (subtitle) subtitle.remove();
                        return clone.textContent;
                    }");
                    title = title.Trim();

                    var imgEl = el.Locator("img.card-img");
                    var coverUrl = await imgEl.CountAsync() > 0 ? await imgEl.GetAttributeAsync("src") : "";

                    var linkEl = el.Locator("a").First;
                    var link = await linkEl.CountAsync() > 0 ? (await linkEl.GetAttributeAsync("href") ?? "") : "";

                    // A cover is required, not optional: the picker is a visual grid, and an entry
                    // with no image is not identifiable at a glance.
                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(coverUrl))
                    {
                        results.Add(new BackloggdMirror.Models.GameSearchResult
                        {
                            Title = title,
                            CoverUrl = coverUrl,
                            RedirectLink = link
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SearchGamesAsync] Error: {ex.Message}");
                _logger?.Error($"[SearchGamesAsync] The manual search for '{query}' failed. The picker gets whatever was collected before the failure ({results.Count} results).", ex);
            }

            return results;
        }

        /// <summary>
        /// Fetches an image (cover or artwork) as a Bitmap, over plain HTTP rather than a browser:
        /// these come from IGDB's CDN, which serves them without any anti-bot layer.
        /// Returns null on failure, since a missing image only degrades the UI.
        /// </summary>
        public async Task<Avalonia.Media.Imaging.Bitmap?> DownloadImageAsync(string url)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(GetRandomUserAgent());
                var data = await httpClient.GetByteArrayAsync(url);
                using var stream = new MemoryStream(data);
                return new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackloggdBrowserService] DownloadImageAsync Error: {ex.Message}");
                _logger?.Warning($"[DownloadImageAsync] Could not download the image at {url}: {ex.Message}. The UI falls back to its placeholder.");
                return null;
            }
        }
    }
}
