using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>Whether a StatusNotifierItem host (Avalonia's only tray backend on Linux) exists; it can come and go at runtime.</summary>
internal static class LinuxTrayHost
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";

    private static Connection? _connection;
    private static IAppLogger? _logger;
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static volatile bool _isAvailable;
    private static bool _hasChecked;
    private static readonly TaskCompletionSource FirstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static bool IsAvailable => _isAvailable;

    /// <summary>Raised on a D-Bus thread, not the UI one.</summary>
    public static event Action? AvailabilityChanged;

    /// <summary>At login the host may register a few seconds after the app starts, so a missing tray is only final after the timeout.</summary>
    public static async Task<bool> WaitForHostAsync(TimeSpan timeout)
    {
        await FirstCheck.Task;
        if (_isAvailable) return true;

        var appeared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (_isAvailable) appeared.TrySetResult(); }
        AvailabilityChanged += OnChanged;
        try
        {
            if (_isAvailable) return true;
            await Task.WhenAny(appeared.Task, Task.Delay(timeout));
            return _isAvailable;
        }
        finally
        {
            AvailabilityChanged -= OnChanged;
        }
    }

    public static async Task StartAsync(IAppLogger logger)
    {
        _logger = logger;
        try
        {
            string? address = Address.Session;
            if (string.IsNullOrEmpty(address))
            {
                logger.Warning("[LinuxTrayHost] No D-Bus session bus: assuming there is no tray, so closing the window quits the app.");
                FirstCheck.TrySetResult();
                return;
            }

            _connection = new Connection(address);
            await _connection.ConnectAsync();

            await WatchAsync(new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = WatcherName });
            await WatchAsync(new MatchRule { Type = MessageType.Signal, Interface = WatcherName, Member = "StatusNotifierHostRegistered" });
            await WatchAsync(new MatchRule { Type = MessageType.Signal, Interface = WatcherName, Member = "StatusNotifierHostUnregistered" });

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.Warning($"[LinuxTrayHost] Could not query the tray over D-Bus, assuming there is none: {ex.Message}");
            FirstCheck.TrySetResult();
        }
    }

    private static async Task WatchAsync(MatchRule rule)
    {
        await _connection!.AddMatchAsync(rule, static (_, _) => true, static (ex, _, _, _) =>
        {
            if (ex == null) _ = RefreshAsync();
        }, ObserverFlags.None, emitOnCapturedContext: false);
    }

    private static async Task RefreshAsync()
    {
        await RefreshLock.WaitAsync();
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private static async Task RefreshCoreAsync()
    {
        bool available;
        try
        {
            available = await HasWatcherAsync() && await IsHostRegisteredAsync();
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[LinuxTrayHost] Tray query failed, treating it as absent: {ex.Message}");
            available = false;
        }

        if (_hasChecked && available == _isAvailable) return;

        bool changed = available != _isAvailable;
        _hasChecked = true;
        _isAvailable = available;
        _logger?.Info(available
            ? "[LinuxTrayHost] A tray host is available: \"Minimize to tray\" is offered."
            : "[LinuxTrayHost] No tray host: \"Minimize to tray\" is hidden and closing the window quits the app.");
        FirstCheck.TrySetResult();
        if (changed) AvailabilityChanged?.Invoke();
    }

    private static Task<bool> HasWatcherAsync()
    {
        using var writer = _connection!.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus", @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
        writer.WriteString(WatcherName);
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) => m.GetBodyReader().ReadBool(), null);
    }

    private static Task<bool> IsHostRegisteredAsync()
    {
        using var writer = _connection!.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: WatcherName, path: WatcherPath, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString(WatcherName);
        writer.WriteString("IsStatusNotifierHostRegistered");
        return _connection.CallMethodAsync(writer.CreateMessage(), static (m, _) =>
        {
            var value = m.GetBodyReader().ReadVariantValue();
            return value.Type == VariantValueType.Variant ? value.GetVariantValue().GetBool() : value.GetBool();
        }, null);
    }
}
