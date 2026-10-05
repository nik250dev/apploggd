using BackloggdMirror.Services.Platform.Linux;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;

namespace BackloggdMirror.Services
{
    /// <summary>
    /// Persists the "Remember me" session on disk, encrypted with ASP.NET Data Protection.
    /// Only cookies are stored, never the password: the app never needs to replay the login form,
    /// so keeping a recoverable password would be risk without benefit.
    /// </summary>
    public class CredentialStorageService : ICredentialStorageService
    {
        private readonly IDataProtectionProvider _dataProtectionProvider;
        private readonly string _storagePath;
        private readonly string _usernamePath;
        private readonly IAppLogger _logger;
        private readonly LinuxKeyringKeyProtection? _linuxKeyProtection;

        public CredentialStorageService(IAppLogger logger, string? customKeysDirectory = null, string? customStoragePath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string keysDirectory = customKeysDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "Keys");

            var services = new ServiceCollection();
            var dataProtectionBuilder = services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
                .SetApplicationName("Apploggd");

            // DPAPI ties the keys to the Windows user account, so a copied key file is useless
            // elsewhere. On Linux the system keyring plays that role; on macOS key secrecy falls
            // back to file-system permissions.
            if (OperatingSystem.IsWindows())
            {
                dataProtectionBuilder.ProtectKeysWithDpapi();
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxKeyringKeyProtection.Configure(dataProtectionBuilder, logger);
            }

            var serviceProvider = services.BuildServiceProvider();
            _dataProtectionProvider = serviceProvider.GetDataProtectionProvider();

            if (OperatingSystem.IsLinux())
            {
                _linuxKeyProtection = new LinuxKeyringKeyProtection(keysDirectory, serviceProvider);
            }

            _storagePath = customStoragePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apploggd", "user.dat");
            _usernamePath = Path.ChangeExtension(_storagePath, ".name");
        }

        public void SaveSession(IEnumerable<Cookie> cookies, string? username)
        {
            try
            {
                var cookieDtos = new List<CookieDto>();
                foreach (var cookie in cookies)
                {
                    cookieDtos.Add(new CookieDto
                    {
                        Name = cookie.Name,
                        Value = cookie.Value,
                        Domain = cookie.Domain,
                        Path = cookie.Path,
                        Secure = cookie.Secure,
                        HttpOnly = cookie.HttpOnly,
                        Expires = cookie.Expires
                    });
                }

                var json = JsonSerializer.Serialize(cookieDtos);
                if (_linuxKeyProtection != null)
                {
                    _linuxKeyProtection.Write(() => WriteProtected(json));
                }
                else
                {
                    WriteProtected(json);
                }

                // Kept beside user.dat, not inside it, so older versions can still read the cookies.
                if (string.IsNullOrEmpty(username)) DeleteUsername();
                else File.WriteAllText(_usernamePath, username);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CredentialStorageService] Failed to save cookies: {ex.Message}");
                _logger.Error($"[CredentialStorageService] Failed to save cookies: {ex.Message}", ex);
            }
        }

        private string WriteProtected(string json)
        {
            var protector = _dataProtectionProvider.CreateProtector("CookieStorage");
            var protectedData = protector.Protect(json);

            var directory = Path.GetDirectoryName(_storagePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_storagePath, protectedData);
            return protectedData;
        }

        public StoredSession LoadSession()
        {
            var cookies = new List<Cookie>();
            string? username = null;

            if (!File.Exists(_storagePath))
            {
                return new StoredSession(cookies, username);
            }

            try
            {
                var protectedData = File.ReadAllText(_storagePath);
                var protector = _dataProtectionProvider.CreateProtector("CookieStorage");
                var json = protector.Unprotect(protectedData);

                var cookieDtos = JsonSerializer.Deserialize<List<CookieDto>>(json);
                if (cookieDtos != null)
                {
                    foreach (var dto in cookieDtos)
                    {
                        cookies.Add(new Cookie
                        {
                            Name = dto.Name,
                            Value = dto.Value,
                            Domain = dto.Domain,
                            Path = dto.Path,
                            Secure = dto.Secure,
                            HttpOnly = dto.HttpOnly,
                            Expires = dto.Expires
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CredentialStorageService] Failed to load cookies: {ex.Message}");
                _logger.Error($"[CredentialStorageService] Failed to load cookies: {ex.Message}", ex);
                if (_linuxKeyProtection != null && LinuxKeyringKeyProtection.DecryptionBlockedByKeyring)
                {
                    _logger.Warning("[CredentialStorageService] The keyring was unavailable, so the saved session is kept for the next start.");
                    return new StoredSession(cookies, username);
                }
                // Unprotect fails for good (lost or rotated keys, corrupted file), so the file is
                // dead weight: dropping it degrades to a normal login instead of failing every start.
                try { File.Delete(_storagePath); } catch { }
                DeleteUsername();
                return new StoredSession(cookies, null);
            }

            try
            {
                if (File.Exists(_usernamePath)) username = File.ReadAllText(_usernamePath).Trim();
            }
            catch (Exception ex)
            {
                _logger.Warning($"[CredentialStorageService] Could not read the saved username: {ex.Message}. The app will not be able to start offline.");
            }

            if (cookies.Count > 0 && _linuxKeyProtection?.CanMoveKeysToKeyring() == true)
            {
                SaveSession(cookies, username);
            }

            return new StoredSession(cookies, username);
        }

        public void ClearCookies()
        {
            if (File.Exists(_storagePath))
            {
                File.Delete(_storagePath);
            }
            DeleteUsername();
        }

        private void DeleteUsername()
        {
            try { if (File.Exists(_usernamePath)) File.Delete(_usernamePath); } catch { }
        }

        private class CookieDto
        {
            public string Name { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Domain { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public bool Secure { get; set; }
            public bool HttpOnly { get; set; }
            public DateTime Expires { get; set; }
        }
    }
}
