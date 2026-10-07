using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// The Linux stand-in for DPAPI: Data Protection key files are encrypted with a secret kept in the system keyring.
/// Without a keyring (no Secret Service at all) keys stay in plain text, guarded only by file permissions,
/// and move to the keyring the first time one is reachable.
/// </summary>
internal sealed class LinuxKeyringKeyProtection
{
    private static readonly object SecretLock = new();
    private static byte[]? _secret;
    private static KeyringException? _failure;
    private static IAppLogger? _logger;
    private static volatile bool _decryptionBlocked;

    private readonly string _keysDirectory;
    private readonly IKeyManager _keyManager;
    private readonly TimeSpan _newKeyLifetime;

    public LinuxKeyringKeyProtection(string keysDirectory, IServiceProvider dataProtectionServices)
    {
        _keysDirectory = keysDirectory;
        _keyManager = dataProtectionServices.GetRequiredService<IKeyManager>();
        _newKeyLifetime = dataProtectionServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeyManagementOptions>>().Value.NewKeyLifetime;
    }

    public static void Configure(IDataProtectionBuilder builder, IAppLogger logger)
    {
        _logger = logger;
        // Otherwise the first Protect after CreateNewKey still gets the old key ring (refreshed in the background).
        AppContext.SetSwitch("Microsoft.AspNetCore.DataProtection.KeyManagement.DisableAsyncKeyRingUpdate", true);
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new LinuxKeyringXmlEncryptor());
    }

    /// <summary>A key needed the keyring and it could not be used, so a failed decryption says nothing about the saved session itself.</summary>
    public static bool DecryptionBlockedByKeyring => _decryptionBlocked;

    /// <summary>Cached for the whole process, failures included: a dismissed unlock prompt must not come back on every key.</summary>
    internal static byte[] GetSecret(bool create)
    {
        lock (SecretLock)
        {
            if (_secret != null) return _secret;
            if (_failure != null && !(create && _failure.Failure == KeyringFailure.SecretMissing)) throw _failure;

            try
            {
                // Off the calling thread: this runs on the UI thread, and the D-Bus replies must not need it.
                _secret = Task.Run(() => LinuxSecretService.GetSecretAsync(create)).GetAwaiter().GetResult();
                _failure = null;
                _logger?.Info("[CredentialStorageService] Session keys are protected with the system keyring.");
                return _secret;
            }
            catch (KeyringException ex)
            {
                _failure = ex;
                if (ex.Failure == KeyringFailure.NoService)
                    _logger?.Warning($"[CredentialStorageService] No system keyring, so session keys are stored unencrypted, protected only by file permissions. {ex.Message}");
                else if (ex.Failure == KeyringFailure.Unusable)
                    _logger?.Warning($"[CredentialStorageService] The system keyring could not be used: {ex.Message}");
                throw;
            }
        }
    }

    internal static byte[] GetSecretForDecryption()
    {
        try
        {
            return GetSecret(create: false);
        }
        catch (KeyringException ex) when (ex.Failure != KeyringFailure.SecretMissing)
        {
            _decryptionBlocked = true;
            throw;
        }
    }

    private static bool TryGetSecret()
    {
        try
        {
            GetSecret(create: true);
            return true;
        }
        catch (KeyringException)
        {
            return false;
        }
    }

    /// <summary>Whether there are keys outside the keyring and the keyring is reachable to take them. May show the unlock prompt.</summary>
    public bool CanMoveKeysToKeyring() => FindKeysOutsideKeyring().Count > 0 && TryGetSecret();

    /// <summary>
    /// Runs <paramref name="protectAndWrite"/> (which returns the payload it wrote) under a fresh keyring key when older
    /// keys are outside the keyring. Those are deleted only once the written payload proves it no longer needs them.
    /// </summary>
    public void Write(Func<string> protectAndWrite)
    {
        var oldKeys = FindKeysOutsideKeyring();
        if (oldKeys.Count == 0 || !TryGetSecret())
        {
            protectAndWrite();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        IKey newKey = _keyManager.CreateNewKey(now, now + _newKeyLifetime);
        string payload = protectAndWrite();

        if (PayloadKeyId(payload) != newKey.KeyId)
        {
            _logger?.Warning("[CredentialStorageService] The session was not saved with the new keyring key; the unencrypted keys are kept.");
            return;
        }

        foreach (string file in oldKeys)
        {
            File.Delete(file);
        }
        _logger?.Info($"[CredentialStorageService] Moved the session to a keyring-protected key and deleted {oldKeys.Count} unencrypted key file(s).");
    }

    private List<string> FindKeysOutsideKeyring()
    {
        var files = new List<string>();
        if (!Directory.Exists(_keysDirectory)) return files;

        string keyringDecryptor = typeof(LinuxKeyringXmlDecryptor).FullName + ",";
        try
        {
            foreach (string file in Directory.EnumerateFiles(_keysDirectory, "key-*.xml"))
            {
                try
                {
                    string? decryptor = XDocument.Load(file).Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "encryptedSecret")?
                        .Attribute("decryptorType")?.Value;
                    if (decryptor == null || !decryptor.StartsWith(keyringDecryptor, StringComparison.Ordinal))
                    {
                        files.Add(file);
                    }
                }
                catch (System.Xml.XmlException)
                {
                    // Unreadable: not ours to judge, so it is left alone.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warning($"[CredentialStorageService] Could not check the key files: {ex.Message}");
            return new List<string>();
        }
        return files;
    }

    // Data Protection payloads start with a 4-byte magic header followed by the key id.
    private static Guid? PayloadKeyId(string payload)
    {
        try
        {
            string base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            byte[] bytes = Convert.FromBase64String(base64);
            return bytes.Length >= 20 ? new Guid(bytes.AsSpan(4, 16)) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>AES-GCM with the keyring secret. Falls back to plain text only when there is no keyring at all.</summary>
internal sealed class LinuxKeyringXmlEncryptor : IXmlEncryptor
{
    internal const int NonceSize = 12;
    internal const int TagSize = 16;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        byte[] secret;
        try
        {
            secret = LinuxKeyringKeyProtection.GetSecret(create: true);
        }
        catch (KeyringException ex) when (ex.Failure == KeyringFailure.NoService)
        {
            return new NullXmlEncryptor().Encrypt(plaintextElement);
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            byte[] blob = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = blob.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(secret, TagSize))
            {
                aes.Encrypt(nonce, plaintext, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize));
            }

            var element = new XElement("encryptedKey",
                new XComment(" Encrypted with a secret from the system keyring. "),
                new XElement("value", Convert.ToBase64String(blob)));
            return new EncryptedXmlInfo(element, typeof(LinuxKeyringXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Created by Data Protection from the type name stored in each key file.</summary>
internal sealed class LinuxKeyringXmlDecryptor : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        byte[] secret = LinuxKeyringKeyProtection.GetSecretForDecryption();

        byte[] blob = Convert.FromBase64String((string?)encryptedElement.Element("value")
            ?? throw new CryptographicException("The keyring-encrypted key has no value."));
        const int headerSize = LinuxKeyringXmlEncryptor.NonceSize + LinuxKeyringXmlEncryptor.TagSize;
        if (blob.Length < headerSize) throw new CryptographicException("The keyring-encrypted key is truncated.");

        byte[] plaintext = new byte[blob.Length - headerSize];
        try
        {
            using (var aes = new AesGcm(secret, LinuxKeyringXmlEncryptor.TagSize))
            {
                aes.Decrypt(blob.AsSpan(0, LinuxKeyringXmlEncryptor.NonceSize), blob.AsSpan(headerSize),
                    blob.AsSpan(LinuxKeyringXmlEncryptor.NonceSize, LinuxKeyringXmlEncryptor.TagSize), plaintext);
            }
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
