using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BackloggdMirror.Services;

/// <summary>
/// Mirrors the "start with Windows" setting into the OS autostart mechanism. It lives in a service
/// rather than in the ViewModel because the entry has to be reconciled on every startup, long
/// before any window exists — see <see cref="Reconcile"/> for why that is not optional.
/// </summary>
public class AutostartService
{
    /// <summary>
    /// Appended to the executable in the autostart entry so the app can tell a logon launch from
    /// the user opening it by hand, and stay out of the way in the first case.
    /// </summary>
    public const string StartupArgument = "--startup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Windows records here what the user disabled from Task Manager > Startup apps. The Run entry
    // survives untouched, so an entry can look perfectly healthy and still never launch.
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "Apploggd";

    private readonly IAppLogger _logger;

    public AutostartService(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whether this launch came from the autostart entry, in which case nothing should be put on
    /// screen. Static because it is answered before any service graph exists.
    /// </summary>
    public static bool IsSilentStart(string[]? args)
    {
        if (args == null) return false;
        return Array.Exists(args, a => string.Equals(a, StartupArgument, StringComparison.OrdinalIgnoreCase));
    }

    // The guard attribute is what lets the platform analyzer accept the registry calls that every
    // "if (!IsSupported) return;" below protects.
    [SupportedOSPlatformGuard("windows")]
    private static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// The command the Run entry must hold for *this* copy of the app. Quoted because the path may
    /// contain spaces, and built from ProcessPath rather than Assembly.Location, which comes back
    /// empty for single-file publishes.
    /// </summary>
    private static string? BuildCommand()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return null;
        return $"\"{path}\" {StartupArgument}";
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadCurrentCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// Turns autostart on or off, reporting whether the OS actually ended up in the requested state.
    /// Callers must not persist the setting on a false: a settings file claiming an autostart the
    /// registry knows nothing about is what makes the toggle lie.
    /// </summary>
    public bool SetEnabled(bool enable)
    {
        if (!IsSupported)
        {
            // TODO: Linux (.desktop file in ~/.config/autostart/) and macOS (LaunchAgents plist).
            _logger.Warning($"[AutostartService] Autostart is not implemented for {RuntimeInformation.OSDescription}. The setting will have no effect.");
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                _logger.Error($@"[AutostartService] Could not open HKCU\{RunKeyPath} for writing.");
                return false;
            }

            if (enable)
            {
                string? command = BuildCommand();
                if (command == null)
                {
                    _logger.Error("[AutostartService] Environment.ProcessPath is empty, so there is no path to register.");
                    return false;
                }

                key.SetValue(ValueName, command);

                // Turning the toggle on is an explicit request, so a leftover "disabled" flag from
                // Task Manager is cleared: otherwise the entry would sit there looking correct and
                // never run, with no way to fix it from inside the app.
                ClearWindowsDisabledFlag();

                _logger.Info($"[AutostartService] Autostart enabled: {command}");
            }
            else
            {
                if (key.GetValue(ValueName) != null)
                {
                    key.DeleteValue(ValueName);
                    _logger.Info("[AutostartService] Autostart disabled: registry entry removed.");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"[AutostartService] Failed to {(enable ? "enable" : "disable")} autostart.", ex);
            return false;
        }
    }

    /// <summary>
    /// Brings the registry back in line with the saved setting at startup. Nothing else does this,
    /// and the two drift apart on their own: the app ships as an archive the user extracts wherever
    /// they like, so a new version in a new folder leaves the Run entry pointing at an executable
    /// that is no longer there — while the toggle keeps showing "on".
    /// </summary>
    public void Reconcile(bool shouldBeEnabled)
    {
        if (!IsSupported) return;

        try
        {
            string? current = ReadCurrentCommand();

            if (!shouldBeEnabled)
            {
                if (current != null)
                {
                    _logger.Info("[AutostartService] Setting is off but a Run entry was still present; removing it.");
                    SetEnabled(false);
                }
                return;
            }

            string? expected = BuildCommand();
            if (expected == null)
            {
                _logger.Error("[AutostartService] Environment.ProcessPath is empty; cannot verify the autostart entry.");
                return;
            }

            if (current == null)
            {
                _logger.Warning("[AutostartService] Setting is on but the Run entry was missing; recreating it.");
                SetEnabled(true);
            }
            else if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                // The usual cause is the app having been moved or a new version extracted elsewhere.
                _logger.Warning($"[AutostartService] Autostart entry is stale. Was: {current} — now: {expected}. Rewriting it.");
                SetEnabled(true);
            }
            else if (IsDisabledByWindows())
            {
                // Deliberately not undone here: the user turned it off in Task Manager and startup
                // is not the moment to overrule that. Logged so the reason is findable.
                _logger.Warning("[AutostartService] The autostart entry is correct but Windows has it disabled (Task Manager > Startup apps). Re-enable it there, or toggle the setting off and on again.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[AutostartService] Failed to reconcile the autostart entry.", ex);
        }
    }

    /// <summary>
    /// Whether Task Manager has this entry switched off. The payload is undocumented; only the
    /// first byte matters and an odd value means disabled. Used for the log message and nothing
    /// else, so a wrong guess here costs a misleading line rather than broken behaviour.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsDisabledByWindows()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, false);
        if (key?.GetValue(ValueName) is byte[] state && state.Length > 0)
        {
            return (state[0] & 1) != 0;
        }
        return false;
    }

    /// <summary>
    /// Drops the Task Manager override entirely. Windows treats a missing value as enabled, which
    /// is unambiguous in a way that writing a payload back would not be.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void ClearWindowsDisabledFlag()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
                _logger.Info("[AutostartService] Cleared the Task Manager \"disabled\" flag for the autostart entry.");
            }
        }
        catch (Exception ex)
        {
            // Not fatal: the Run entry is written either way, and this only matters if the user had
            // previously disabled the app from Task Manager.
            _logger.Warning($"[AutostartService] Could not clear the Task Manager disabled flag: {ex.Message}");
        }
    }
}
