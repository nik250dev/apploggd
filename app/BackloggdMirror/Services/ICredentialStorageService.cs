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
        void SaveCookies(IEnumerable<Cookie> cookies);

        /// <summary>
        /// Returns the stored cookies, or an empty list when there are none or they cannot be
        /// decrypted. Never null, and never throws: an unreadable store degrades to a normal login.
        /// </summary>
        List<Cookie> LoadCookies();

        void ClearCookies();
    }
}
