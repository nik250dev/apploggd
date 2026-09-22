using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        string? configPath = null;
        if (!string.IsNullOrEmpty(rootDir))
        {
            string portable = Path.Combine(rootDir, "retroarch.cfg");
            if (File.Exists(portable))
                configPath = portable;
        }
        if (configPath == null)
        {
            string installed = Path.Combine(appDataDir, "retroarch.cfg");
            if (File.Exists(installed))
                configPath = installed;
        }

        var config = configPath != null ? RetroArchConfig.Parse(configPath) : new Dictionary<string, string>();

        string? infoDir = ResolvePath(config, "libretro_info_path", rootDir)
                          ?? (rootDir != null ? Path.Combine(rootDir, "info") : null);

        string? historyPath = ResolvePath(config, "content_history_path", rootDir);
        if (historyPath == null || !File.Exists(historyPath))
        {
            string? defaultHistory = rootDir != null
                ? Path.Combine(rootDir, "playlists", "builtin", "content_history.lpl")
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

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder buffer, ref uint size);

    private static string? ResolvePath(Dictionary<string, string> config, string key, string? rootDir)
    {
        if (!config.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;

        return RetroArchConfig.ResolveRelative(value, rootDir);
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

    /// <summary>Expands RetroArch's ":\" prefix, which means "relative to the folder of the exe".</summary>
    public static string? ResolveRelative(string value, string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith(":\\", StringComparison.Ordinal) || value.StartsWith(":/", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(rootDir))
                return null;

            return Path.Combine(rootDir, value[2..].Replace('/', '\\'));
        }

        if (value == ":")
            return rootDir;

        return value;
    }
}
