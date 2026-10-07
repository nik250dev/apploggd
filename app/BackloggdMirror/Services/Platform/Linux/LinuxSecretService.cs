using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace BackloggdMirror.Services.Platform.Linux;

internal enum KeyringFailure
{
    /// <summary>No Secret Service on the session bus (no keyring daemon, or no session bus at all).</summary>
    NoService,
    /// <summary>A keyring exists but could not be used: the unlock prompt was dismissed, the call failed...</summary>
    Unusable,
    /// <summary>The keyring answered, but it holds no secret for Apploggd.</summary>
    SecretMissing,
}

internal sealed class KeyringException : Exception
{
    public KeyringFailure Failure { get; }

    public KeyringException(KeyringFailure failure, string message, Exception? inner = null) : base(message, inner)
    {
        Failure = failure;
    }
}

/// <summary>
/// Just enough of the freedesktop Secret Service API (GNOME Keyring, KWallet, KeePassXC...) to keep one secret:
/// the key that encrypts the Data Protection key files.
/// </summary>
internal sealed class LinuxSecretService : IDisposable
{
    private const string ServiceName = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string NoPrompt = "/";
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, string> Attributes = new()
    {
        ["application"] = "Apploggd",
        ["purpose"] = "session-key",
    };

    private const string Label = "Apploggd session key";
    private const int SecretSize = 32;

    private readonly Connection _connection;
    private string _session = NoPrompt;

    private LinuxSecretService(Connection connection)
    {
        _connection = connection;
    }

    /// <summary>Reads Apploggd's secret, creating it when <paramref name="create"/> is set. May show the keyring's unlock prompt.</summary>
    public static async Task<byte[]> GetSecretAsync(bool create)
    {
        string? address = Address.Session;
        if (string.IsNullOrEmpty(address))
            throw new KeyringException(KeyringFailure.NoService, "There is no D-Bus session bus.");

        var connection = new Connection(address);
        using var service = new LinuxSecretService(connection);
        try
        {
            await connection.ConnectAsync();
            service._session = await service.OpenSessionAsync();

            byte[]? secret = await service.FindSecretAsync();
            if (secret != null) return secret;
            if (!create) throw new KeyringException(KeyringFailure.SecretMissing, "The keyring holds no Apploggd secret.");

            return await service.CreateSecretAsync();
        }
        catch (DBusException ex) when (IsMissingService(ex.ErrorName))
        {
            throw new KeyringException(KeyringFailure.NoService, $"No Secret Service on the session bus ({ex.ErrorName}).", ex);
        }
        catch (Exception ex) when (ex is not KeyringException)
        {
            throw new KeyringException(KeyringFailure.Unusable, $"The Secret Service call failed: {ex.Message}", ex);
        }
    }

    // Spawn.* covers a keyring daemon that is installed but cannot start in this session.
    private static bool IsMissingService(string errorName) =>
        errorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
        || errorName.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal);

    // "plain" leaves the secret unencrypted on the bus, which is private to the user; libsecret falls back to it too.
    private Task<string> OpenSessionAsync()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: ServicePath, @interface: ServiceInterface, member: "OpenSession", signature: "sv");
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null);
    }

    private async Task<byte[]?> FindSecretAsync()
    {
        var (unlocked, locked) = await SearchItemsAsync();
        string? item = unlocked.Length > 0 ? unlocked[0] : null;

        if (item == null && locked.Length > 0)
        {
            await UnlockAsync(locked[0]);
            item = locked[0];
        }

        if (item == null) return null;

        byte[] value = await GetItemSecretAsync(item);
        try
        {
            return DecodeSecret(value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private async Task<byte[]> CreateSecretAsync()
    {
        string collection = await ReadAliasAsync("default");
        if (collection == NoPrompt)
            throw new KeyringException(KeyringFailure.Unusable, "The keyring has no default collection to store the secret in.");
        await UnlockAsync(collection);

        byte[] secret = RandomNumberGenerator.GetBytes(SecretSize);
        byte[] value = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(secret));
        try
        {
            await RunPromptAsync(await CreateItemAsync(collection, value));
            return secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private Task<string> CreateItemAsync(string collection, byte[] value)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: collection, @interface: "org.freedesktop.Secret.Collection", member: "CreateItem", signature: "a{sv}(oayays)b");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["org.freedesktop.Secret.Item.Label"] = VariantValue.String(Label),
            ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(Attributes).AsVariantValue(),
        });
        writer.WriteStructureStart();
        writer.WriteObjectPath(_session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(value);
        writer.WriteString("text/plain");
        writer.WriteBool(true);

        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadObjectPathAsString();
            return reader.ReadObjectPathAsString();
        }, null);
    }

    private Task<(string[] Unlocked, string[] Locked)> SearchItemsAsync()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: ServicePath, @interface: ServiceInterface, member: "SearchItems", signature: "a{ss}");
        var start = writer.WriteDictionaryStart();
        foreach (var (key, value) in Attributes)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteString(value);
        }
        writer.WriteDictionaryEnd(start);

        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            return (ToStrings(reader.ReadArrayOfObjectPath()), ToStrings(reader.ReadArrayOfObjectPath()));
        }, null);
    }

    private Task<string> ReadAliasAsync(string alias)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: ServicePath, @interface: ServiceInterface, member: "ReadAlias", signature: "s");
        writer.WriteString(alias);
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null);
    }

    /// <summary>With auto-login the login keyring starts locked, and unlocking it shows the keyring's own password prompt.</summary>
    private async Task UnlockAsync(string objectPath) => await RunPromptAsync(await RequestUnlockAsync(objectPath));

    private Task<string> RequestUnlockAsync(string objectPath)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: ServicePath, @interface: ServiceInterface, member: "Unlock", signature: "ao");
        writer.WriteArray(new[] { new ObjectPath(objectPath) });
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadArrayOfObjectPath();
            return reader.ReadObjectPathAsString();
        }, null);
    }

    private async Task RunPromptAsync(string prompt)
    {
        if (prompt == NoPrompt) return;

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule { Type = MessageType.Signal, Interface = PromptInterface, Member = "Completed", Path = prompt };
        using var subscription = await _connection.AddMatchAsync(rule, static (m, _) => m.GetBodyReader().ReadBool(), (ex, dismissed, _, _) =>
        {
            if (ex != null) completed.TrySetException(ex);
            else completed.TrySetResult(dismissed);
        }, ObserverFlags.None, emitOnCapturedContext: false);

        await ShowPromptAsync(prompt);

        if (await Task.WhenAny(completed.Task, Task.Delay(PromptTimeout)) != completed.Task)
            throw new KeyringException(KeyringFailure.Unusable, "The keyring prompt got no answer.");
        if (await completed.Task)
            throw new KeyringException(KeyringFailure.Unusable, "The keyring prompt was dismissed.");
    }

    private Task ShowPromptAsync(string prompt)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: prompt, @interface: PromptInterface, member: "Prompt", signature: "s");
        writer.WriteString("");
        return _connection.CallMethodAsync(writer.CreateMessage());
    }

    private Task<byte[]> GetItemSecretAsync(string item)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: ServiceName, path: item, @interface: "org.freedesktop.Secret.Item", member: "GetSecret", signature: "o");
        writer.WriteObjectPath(_session);
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.AlignStruct();
            reader.ReadObjectPath();
            reader.ReadArrayOfByte();
            return reader.ReadArrayOfByte();
        }, null);
    }

    private static byte[] DecodeSecret(byte[] value)
    {
        byte[]? secret = null;
        try
        {
            secret = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(value));
        }
        catch (FormatException)
        {
        }

        if (secret?.Length != SecretSize)
            throw new KeyringException(KeyringFailure.Unusable, "The Apploggd secret in the keyring is not a valid key.");
        return secret;
    }

    private static string[] ToStrings(ObjectPath[] paths) => Array.ConvertAll(paths, p => p.ToString());

    public void Dispose()
    {
        // Closing the connection also closes the Secret Service session.
        _connection.Dispose();
    }
}
