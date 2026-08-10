using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace BackloggdMirror.Services
{
    public interface IBackloggdBrowserService
    {
        /// <summary>
        /// On failure, errorMessage carries one of a fixed set of tokens that LoginViewModel maps to
        /// localization keys: InvalidCredentials, BlockedByAntiBot, BrowserClosed, TimeoutError,
        /// NetworkError, BrowserExecutableNotFound, BrowserDepsMissing. Keep both sides in sync.
        /// </summary>
        Task<(string? username, System.Net.CookieContainer? cookies, string? errorMessage)> LoginAsync(string username, string password, bool rememberMe);
        Task PerformLogin();
        Task RegisterGame(string gameName, System.Net.CookieContainer cookieContainer, int gamePlayDateHours, int gamePlayDateMinutes, string? gameUrl = null);
        Task<List<BackloggdMirror.Models.JournalEntry>?> GetLastPlayedGames(string username, System.Net.CookieContainer cookieContainer);
        Task<(string? Title, Bitmap? Cover)> GetGameCoverAsync(string gameName);
        Task<List<BackloggdMirror.Models.GameSearchResult>> SearchGamesAsync(string query);
        Task<Avalonia.Media.Imaging.Bitmap?> DownloadImageAsync(string url);
    }
}
