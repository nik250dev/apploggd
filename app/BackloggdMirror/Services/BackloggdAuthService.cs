using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Holds the live Backloggd session (cookies and canonical username) shared by every other
    /// service, and resolves who that session belongs to.
    ///
    /// <see cref="LoginAsync"/> is the original HttpClient + CSRF implementation and is kept for
    /// reference only: the anti-bot layer now answers plain HTTP clients with a block page, so the
    /// real login runs through <see cref="IBackloggdBrowserService"/>. Session restore, in contrast,
    /// still lives here — see <see cref="ResolveUsernameFromSession"/>, which does drive a browser.
    /// </summary>
    public class BackloggdAuthService : IBackloggdAuthService
    {
        private readonly HttpClient _httpClient;
        private readonly IAppLogger? _logger;

        // Rotated per request so repeat traffic does not carry one constant fingerprint.
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

        public System.Net.CookieContainer Cookies { get; private set; }
        public string? Username { get; private set; }

        public void SetUsername(string username)
        {
            Username = username;
        }

        public BackloggdAuthService(IAppLogger? logger = null)
        {
            _logger = logger;
            Cookies = new System.Net.CookieContainer();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = Cookies
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        }

        /// <summary>
        /// Legacy pure-HTTP login (Rails form: fetch page, extract CSRF token, post credentials).
        /// Superseded by the Playwright flow, which is the only one that gets past the anti-bot
        /// challenge. Returns the canonical username, or null on any failure.
        /// </summary>
        public async Task<string?> LoginAsync(string username, string password)
        {
            try
            {
                // 1. Get the login page to fetch cookies and CSRF token
                var loginUrl = "https://backloggd.com/users/sign_in";
                var getResponse = await _httpClient.GetAsync(loginUrl);
                getResponse.EnsureSuccessStatusCode();
                var html = await getResponse.Content.ReadAsStringAsync();

                // Anchored on the sign_in action because the page carries several forms (search, …)
                // and picking the wrong token gets the post rejected.
                var tokenMatch = Regex.Match(html, "action=\"/users/sign_in\".*?name=\"authenticity_token\" value=\"(.*?)\"", RegexOptions.Singleline);
                if (!tokenMatch.Success)
                {
                    // Fall back to the page-wide meta token.
                    tokenMatch = Regex.Match(html, "name=\"csrf-token\" content=\"(.*?)\"");
                }
                if (!tokenMatch.Success)
                {
                    Console.WriteLine("Failed to find authenticity_token");
                    _logger?.Error("[BackloggdAuthService] No authenticity_token found on the sign-in page. Either Backloggd changed its login form or a protection page was served instead of it.");
                    return null;
                }
                var csrfToken = tokenMatch.Groups[1].Value;

                // 2. Prepare form data
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("authenticity_token", csrfToken),
                    new KeyValuePair<string, string>("user[login]", username),
                    new KeyValuePair<string, string>("user[password]", password),
                    new KeyValuePair<string, string>("user[remember_me]", "1"),
                    new KeyValuePair<string, string>("commit", "Log In")
                });

                // 3. Post credentials
                var request = new HttpRequestMessage(HttpMethod.Post, loginUrl);
                request.Headers.Referrer = new Uri(loginUrl);
                request.Headers.Add("Origin", "https://backloggd.com");
                request.Content = content;

                var postResponse = await _httpClient.SendAsync(request);
                postResponse.EnsureSuccessStatusCode();

                var responseHtml = await postResponse.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(responseHtml);

                // Redirects are followed automatically, so the final URL is the verdict: a successful
                // login lands on the homepage, a rejected one re-renders sign_in.
                if (postResponse.RequestMessage?.RequestUri?.ToString().Contains("/users/sign_in") == true)
                {
                    return null;
                }

                var cookies = Cookies.GetCookies(new Uri("https://backloggd.com"));
                Console.WriteLine($"[BackloggdAuthService] Login successful. Cookies found: {cookies.Count}");
                // The count only: cookie values are the session itself and must never reach the log.
                _logger?.Info($"[BackloggdAuthService] HTTP login accepted. Session cookies received: {cookies.Count}.");

                // 4. Fetch the homepage: the welcome banner is where the canonical username lives,
                //    and it can differ from what the user typed (Backloggd accepts email too).
                var mainPageResponse = await _httpClient.GetAsync("https://backloggd.com/");
                mainPageResponse.EnsureSuccessStatusCode();
                var mainPageHtml = await mainPageResponse.Content.ReadAsStringAsync();

                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(mainPageHtml);

                var welcomeBanner = doc.GetElementbyId("welcome-banner");
                if (welcomeBanner != null)
                {
                    var link = welcomeBanner.SelectSingleNode(".//a[@href]");
                    if (link != null)
                    {
                        var href = link.GetAttributeValue("href", "");
                        // href looks like "/u/Username"
                        var match = Regex.Match(href, @"/u/([^/]+)");
                        if (match.Success)
                        {
                            Username = match.Groups[1].Value;
                            return Username;
                        }
                    }
                }

                // No banner means no username, and the session is useless without one: every later
                // call builds profile URLs from it. Falling back to the typed value would be wrong,
                // since it may be an email or differ in spelling.
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login error: {ex.Message}");
                _logger?.Error("[BackloggdAuthService] The HTTP login flow threw before it could resolve a username.", ex);
                return null;
            }
        }

        /// <summary>
        /// Validates the stored cookies by loading Backloggd with them and reading back who we are.
        /// This is the only way to know a session is still alive: expiry happens server-side without
        /// any local trace. Returns the username, or null if the session is no longer valid.
        /// </summary>
        public async Task<string?> ResolveUsernameFromSession()
        {
            try
            {
                if (Cookies.Count == 0) return null;

                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.HiddenOptions());

                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = await BrowserLaunch.ContextUserAgentAsync(browser, GetRandomUserAgent())
                });

                await context.AddCookiesAsync(CookieConversion.ToPlaywright(Cookies));

                var page = await context.NewPageAsync();
                Console.WriteLine("[BackloggdAuthService] Navigating to backloggd.com to resolve username from session...");
                _logger?.Info("[BackloggdAuthService] Validating the stored session by loading backloggd.com with its cookies.");
                await page.GotoAsync("https://backloggd.com/");

                // The navbar search box only exists on a real Backloggd page, so waiting for it also
                // covers the anti-bot challenge resolving itself.
                try
                {
                    await page.WaitForSelectorAsync("#nav-bar-search", new PageWaitForSelectorOptions { Timeout = 15000 });
                }
                catch
                {
                    Console.WriteLine("[BackloggdAuthService] Timeout waiting for '#nav-bar-search'. We might still be blocked by Bunny Shield or the page failed to load.");
                    _logger?.Warning("[BackloggdAuthService] backloggd.com never rendered its navbar within 15 s. The page is most likely the anti-bot challenge, so the stored session is reported as invalid and the user is sent back to the login.");
                    return null;
                }

                // The welcome banner is only rendered for a logged-in visitor, so finding it doubles
                // as proof the session is still valid.
                var welcomeBannerLocator = page.Locator("#welcome-banner a");
                if (await welcomeBannerLocator.CountAsync() > 0)
                {
                    var href = await welcomeBannerLocator.First.GetAttributeAsync("href");
                    if (!string.IsNullOrEmpty(href))
                    {
                        var match = Regex.Match(href, @"/u/([^/]+)");
                        if (match.Success)
                        {
                            Username = match.Groups[1].Value;
                            Console.WriteLine($"[BackloggdAuthService] Successfully resolved username from session: {Username}");

                            // Adopt the cookies the browser ends up with: the visit itself rotates
                            // and extends them, so keeping the stale set would shorten the session.
                            var playwrightCookies = await context.CookiesAsync();
                            var updatedCookies = new System.Net.CookieContainer();
                            foreach (var cookie in playwrightCookies)
                            {
                                updatedCookies.Add(CookieConversion.ToNet(cookie));
                            }
                            Cookies = updatedCookies;

                            return Username;
                        }
                    }
                }

                Console.WriteLine("[BackloggdAuthService] Welcome banner not found. Session might be expired or invalid.");
                _logger?.Warning("[BackloggdAuthService] The page loaded but showed no welcome banner, so nobody is logged in: the stored session has expired server-side.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackloggdAuthService] Failed to resolve username from session: {ex.Message}");
                _logger?.Error("[BackloggdAuthService] Validating the stored session threw. It is treated as invalid, so the user has to log in again.", ex);
                return null;
            }
        }
        public void Logout()
        {
            Cookies = new System.Net.CookieContainer();
            Username = null;
        }


    }
}
