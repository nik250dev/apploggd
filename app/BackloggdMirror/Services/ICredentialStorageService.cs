using System.Collections.Generic;
using System.Net;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Persists the "Remember me" session across restarts. Cookies only — the password is never
    /// stored.
    /// </summary>
    public interface ICredentialStorageService
    {
        /// <summary>The username goes with the cookies so the app can start offline without asking Backloggd who they belong to.</summary>
        void SaveSession(IEnumerable<Cookie> cookies, string? username);

        /// <summary>
        /// Returns the stored session, with an empty cookie list when there is none or it cannot be
        /// decrypted. Never throws: an unreadable store degrades to a normal login. The username is
        /// null for files saved before it was stored.
        /// </summary>
        StoredSession LoadSession();

        void ClearCookies();
    }

    public sealed record StoredSession(List<Cookie> Cookies, string? Username);
}
