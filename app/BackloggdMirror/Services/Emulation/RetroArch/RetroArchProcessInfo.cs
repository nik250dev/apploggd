using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>
/// Where a running RetroArch lives and where its cfg points the info and history files.
/// Cached per PID because none of it changes while the process is alive.
/// </summary>
internal sealed class RetroArchProcessInfo
{
    private static readonly Dictionary<int, RetroArchProcessInfo> _cache = new();
    private static readonly object _cacheLock = new();

    public int ProcessId { get; }
    public DateTime StartTimeUtc { get; }
    public string? ExePath { get; }
    public string? RootDir { get; }
    public string? ConfigPath { get; }
    public string? InfoDir { get; }
    public string? HistoryPath { get; }
    public bool HistoryEnabled { get; }

    private RetroArchProcessInfo(int processId, DateTime startTimeUtc, string? exePath, string? rootDir,
        string? configPath, string? infoDir, string? historyPath, bool historyEnabled)
    {
        ProcessId = processId;
        StartTimeUtc = startTimeUtc;
        ExePath = exePath;
        RootDir = rootDir;
        ConfigPath = configPath;
        InfoDir = infoDir;
        HistoryPath = historyPath;
        HistoryEnabled = historyEnabled;
    }

    public static RetroArchProcessInfo For(Process process)
    {
        DateTime startTimeUtc;
        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            startTimeUtc = DateTime.MinValue;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(process.Id, out var cached) && cached.StartTimeUtc == startTimeUtc)
                return cached;

            var built = Build(process, startTimeUtc);
            _cache[process.Id] = built;
            return built;
        }
    }

    public static void Forget(int processId)
    {
        lock (_cacheLock)
        {
            _cache.Remove(processId);
        }
    }

    private static RetroArchProcessInfo Build(Process process, DateTime startTimeUtc)
    {
        string? exePath = null;
        try
        {
            exePath = process.MainModule?.FileName;
        }
        catch
        {
            // Denied for an elevated RetroArch, where the query below still answers.
        }

        if (string.IsNullOrEmpty(exePath))
            exePath = QueryImagePath(process.Id);

        string? rootDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch");

        // The Microsoft Store build cannot write to its install folder, so its cfg, info and playlists
        // live in the package's LocalState, which is also what "~" means there.
        string? packageDir = QueryPackageDataDir(process.Id);
        string? homeDir = packageDir ?? Environment.GetEnvironmentVariable("HOME");
        string? dataDir = packageDir ?? rootDir;

        var arguments = SplitArguments(QueryCommandLine(process.Id));

        // RetroArch resolves a relative -c against its working directory, which frontends set to its folder.
        string? configPath = FirstExisting(
            OptionValues(arguments, 'c', "config").Select(value => ResolveArgument(value, rootDir, homeDir)).LastOrDefault(),
            rootDir != null ? Path.Combine(rootDir, "retroarch.cfg") : null,
            packageDir != null ? Path.Combine(packageDir, "retroarch.cfg") : null,
            Path.Combine(appDataDir, "retroarch.cfg"));

        var config = configPath != null ? RetroArchConfig.Parse(configPath) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // --appendconfig takes "a.cfg|b.cfg", each overriding the keys of the ones before.
        foreach (string value in OptionValues(arguments, null, "appendconfig"))
        {
            foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? appendPath = ResolveArgument(part, rootDir, homeDir);
                if (appendPath == null || !File.Exists(appendPath))
                    continue;

                foreach (var (key, appended) in RetroArchConfig.Parse(appendPath))
                    config[key] = appended;
            }
        }

        string? infoDir = ResolvePath(config, "libretro_info_path", rootDir, homeDir)
                          ?? (dataDir != null ? Path.Combine(dataDir, "info") : null);

        string? historyPath = ResolvePath(config, "content_history_path", rootDir, homeDir);
        if (historyPath == null || !File.Exists(historyPath))
        {
            string? defaultHistory = dataDir != null
                ? Path.Combine(dataDir, "playlists", "builtin", "content_history.lpl")
                : null;

            if (defaultHistory != null && File.Exists(defaultHistory))
            {
                historyPath = defaultHistory;
            }
            else
            {
                string appDataHistory = Path.Combine(appDataDir, "content_history.lpl");
                if (File.Exists(appDataHistory))
                    historyPath = appDataHistory;
                else
                    historyPath = historyPath ?? defaultHistory;
            }
        }

        bool historyEnabled = !config.TryGetValue("history_list_enable", out string? enabled)
                              || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);

        return new RetroArchProcessInfo(process.Id, startTimeUtc, exePath, rootDir, configPath, infoDir, historyPath, historyEnabled);
    }

    /// <summary>
    /// The path of the exe without opening the process for reading, which is the only way to get it
    /// when RetroArch runs elevated: PROCESS_QUERY_LIMITED_INFORMATION crosses integrity levels.
    /// </summary>
    private static string? QueryImagePath(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>With the same limited access as the exe path, so it works for an elevated RetroArch too.</summary>
    private static string? QueryCommandLine(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
                return null;

            buffer = Marshal.AllocHGlobal(needed);
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
                return null;

            // A UNICODE_STRING whose Buffer points just past it, inside the same allocation.
            int length = (ushort)Marshal.ReadInt16(buffer);
            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    /// <summary>The LocalState of the package, or null for any RetroArch not installed from the Microsoft Store.</summary>
    private static string? QueryPackageDataDir(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var name = new StringBuilder(256);
            uint length = (uint)name.Capacity;
            if (GetPackageFamilyName(handle, ref length, name) != 0)
                return null;

            string localState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", name.ToString(), "LocalState");
            return Directory.Exists(localState) ? localState : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static IReadOnlyList<string> SplitArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return Array.Empty<string>();

        IntPtr argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero)
            return Array.Empty<string>();

        try
        {
            var arguments = new string[count];
            for (int i = 0; i < count; i++)
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>The values of an option in the forms getopt_long accepts: "-c x", "-cx", "--config x" and "--config=x".</summary>
    internal static IEnumerable<string> OptionValues(IReadOnlyList<string> arguments, char? shortName, string longName)
    {
        string longForm = "--" + longName;
        string? shortForm = shortName != null ? "-" + shortName : null;

        for (int i = 1; i < arguments.Count; i++)
        {
            string argument = arguments[i];

            if (argument == "--")
                yield break;

            if (argument == longForm || argument == shortForm)
            {
                if (i + 1 < arguments.Count)
                    yield return arguments[++i];
            }
            else if (argument.StartsWith(longForm + "=", StringComparison.Ordinal))
            {
                yield return argument[(longForm.Length + 1)..];
            }
            else if (shortForm != null && argument.Length > 2 && argument.StartsWith(shortForm, StringComparison.Ordinal))
            {
                yield return argument[2..];
            }
        }
    }

    private static string? ResolveArgument(string value, string? rootDir, string? homeDir)
    {
        string? path = RetroArchConfig.ResolveRelative(value, rootDir, homeDir);
        if (path == null || Path.IsPathRooted(path))
            return path;

        return rootDir != null ? Path.Combine(rootDir, path) : null;
    }

    private static string? FirstExisting(params string?[] paths)
    {
        return paths.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr handle, ref uint length, StringBuilder familyName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder buffer, ref uint size);

    private static string? ResolvePath(Dictionary<string, string> config, string key, string? rootDir, string? homeDir)
    {
        if (!config.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;

        return RetroArchConfig.ResolveRelative(value, rootDir, homeDir);
    }
}

/// <summary>Parser for the <c>key = "value"</c> files RetroArch uses for both retroarch.cfg and core .info.</summary>
internal static class RetroArchConfig
{
    public static Dictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                int separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = trimmed[..separator].Trim();
                string value = trimmed[(separator + 1)..].Trim().Trim('"');

                if (key.Length > 0)
                    values[key] = value;
            }
        }
        catch
        {
            // A missing or locked file simply leaves the caller on defaults.
        }

        return values;
    }

    /// <summary>
    /// Expands RetroArch's prefixes: ":\" is the folder of the exe and "~\" the home folder, which is
    /// the package's LocalState for the Microsoft Store build and %HOME% otherwise.
    /// </summary>
    public static string? ResolveRelative(string value, string? rootDir, string? homeDir = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var (prefix, dir) in new[] { (':', rootDir), ('~', homeDir) })
        {
            if (value.Length >= 2 && value[0] == prefix && (value[1] == '\\' || value[1] == '/'))
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, value[2..].Replace('/', '\\'));

            if (value.Length == 1 && value[0] == prefix)
                return string.IsNullOrEmpty(dir) ? null : dir;
        }

        return value;
    }
}
