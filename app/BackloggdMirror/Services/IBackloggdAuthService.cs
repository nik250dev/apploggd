using System.Threading.Tasks;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Process-wide holder of the authenticated Backloggd session. Every service that talks to the
    /// site reads its cookies and username from here.
    /// </summary>
    public interface IBackloggdAuthService
    {
        System.Net.CookieContainer Cookies { get; }
        string? Username { get; }
        void SetUsername(string username);
        Task<string?> LoginAsync(string username, string password);
        Task<string?> ResolveUsernameFromSession();
        void Logout();
    }
}
