using System;
using System.Collections.Generic;
using System.Net;
using NetCookie = System.Net.Cookie;
using PlaywrightCookie = Microsoft.Playwright.Cookie;
using PlaywrightCookieResult = Microsoft.Playwright.BrowserContextCookiesResult;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Translates cookies between Playwright and <see cref="System.Net"/>, which is done every time a
    /// session crosses between the browser (where Backloggd is actually driven) and the
    /// <see cref="CookieContainer"/> that holds the session for the rest of the process.
    ///
    /// Kept in one place because the mapping is not symmetric and has two traps. Playwright measures
    /// expiry in Unix seconds and uses -1 for a session cookie, while <see cref="NetCookie"/> uses
    /// <see cref="DateTime.MinValue"/> for the same thing. And the two directions do not even use the
    /// same Playwright type: reading a context yields <see cref="PlaywrightCookieResult"/>
    /// (non-nullable fields), while writing one back takes <see cref="PlaywrightCookie"/> (nullable).
    ///
    /// Both cookie types are aliased here rather than imported, because an unqualified "Cookie" is
    /// ambiguous between the two namespaces.
    /// </summary>
    internal static class CookieConversion
    {
        /// <summary>Cookies read back from a browser context.</summary>
        public static NetCookie ToNet(PlaywrightCookieResult cookie) =>
            ToNet(cookie.Name, cookie.Value, cookie.Path, cookie.Domain, cookie.Expires, cookie.Secure, cookie.HttpOnly);

        /// <summary>Cookies built by hand, where every optional field may be absent.</summary>
        public static NetCookie ToNet(PlaywrightCookie cookie) =>
            ToNet(cookie.Name, cookie.Value, cookie.Path ?? "/", cookie.Domain ?? string.Empty,
                  cookie.Expires ?? -1, cookie.Secure ?? false, cookie.HttpOnly ?? false);

        /// <summary>
        /// Note that Playwright's Unix-seconds expiry is a float, whose 24-bit mantissa cannot
        /// resolve a present-day timestamp (~1.7e9) closer than about two minutes. Harmless for a
        /// multi-week "remember me" token, but it does mean the converted expiry is an
        /// approximation, never an exact instant.
        /// </summary>
        private static NetCookie ToNet(string name, string value, string path, string domain, float expires, bool secure, bool httpOnly)
        {
            var net = new NetCookie(name, value, path, domain)
            {
                Secure = secure,
                HttpOnly = httpOnly
            };

            // Anything else — -1, negative, or beyond what DateTime can hold — means "no expiry we
            // can represent", so the cookie stays a session cookie. That is the lenient direction:
            // it keeps the cookie alive locally and lets the server be the one to reject it, rather
            // than discarding a session we merely failed to parse.
            if (expires > 0 && expires <= 253402300799f)
            {
                try
                {
                    net.Expires = DateTimeOffset.FromUnixTimeSeconds((long)expires).UtcDateTime;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Leave it as a session cookie.
                }
            }

            return net;
        }

        /// <summary>
        /// Reverse direction, for handing a stored session back to a browser context.
        /// </summary>
        public static PlaywrightCookie ToPlaywright(NetCookie cookie)
        {
            return new PlaywrightCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = cookie.Path,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                Expires = cookie.Expires == DateTime.MinValue
                    ? -1
                    : (float)(cookie.Expires.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds
            };
        }

        /// <summary>
        /// Every Backloggd cookie a container holds, converted for a browser context.
        /// </summary>
        public static List<PlaywrightCookie> ToPlaywright(CookieContainer container)
        {
            var cookies = new List<PlaywrightCookie>();
            foreach (NetCookie cookie in container.GetCookies(new Uri("https://backloggd.com")))
            {
                cookies.Add(ToPlaywright(cookie));
            }
            return cookies;
        }
    }
}
