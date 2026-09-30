using System;
using System.Collections.Generic;
using System.IO;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>What detection reads from /proc, including the Windows paths Wine gives its processes.</summary>
internal static class LinuxProcFs
{
    private static readonly char[] Separators = { '/', '\\' };

    /// <summary>argv[0] as the process set it; for a Wine process, the Windows path of its .exe.</summary>
    public static string? ReadArgv0(int pid)
    {
        string[]? args = ReadArgs(pid);
        return args is { Length: > 0 } && args[0].Length > 0 ? args[0] : null;
    }

    public static string[]? ReadArgs(int pid)
    {
        try
        {
            string cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
            return cmdline.Length > 0 ? cmdline.TrimEnd('\0').Split('\0') : null;
        }
        catch
        {
            // Kernel threads have no cmdline, and a process can exit mid-scan.
            return null;
        }
    }

    /// <summary>Every PID in /proc: cheaper than Process.GetProcesses, which reads far more than these scans need.</summary>
    public static List<int> ListProcessIds()
    {
        var pids = new List<int>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(dir), out int pid))
                    pids.Add(pid);
            }
        }
        catch
        {
            // An empty list only skips one scan.
        }

        return pids;
    }

    /// <summary>The kernel's process name, cut to 15 characters.</summary>
    public static string? ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").TrimEnd('\n');
        }
        catch
        {
            return null;
        }
    }

    public static int? ReadParentId(int pid)
    {
        try
        {
            // "pid (comm) S ppid ...": comm may hold spaces and parentheses, so the fields follow the last ')'.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.Parse(fields[1]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The file names of everything the process has mapped, libraries included ("libGL.so.1.7.0").</summary>
    public static HashSet<string>? ReadMappedFileNames(int pid)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            {
                // The path is the last column and may hold spaces; anonymous mappings have none.
                int start = line.IndexOf('/');
                if (start >= 0)
                    names.Add(FileNameOf(line[start..]));
            }

            return names;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where each file descriptor points: a path ("/home/.../game.iso"), a deleted file ("/dev/shm/x (deleted)")
    /// or a pseudo-file ("socket:[123]"). Paths are as the process sees them, which for a Flatpak is its own mount namespace.
    /// </summary>
    public static List<(int Fd, string Target)> ReadOpenFiles(int pid)
    {
        var files = new List<(int, string)>();
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out int fd))
                    continue;

                try
                {
                    string? target = new FileInfo(entry).LinkTarget;
                    if (target != null)
                        files.Add((fd, target));
                }
                catch
                {
                    // Closed mid-scan.
                }
            }
        }
        catch
        {
            // Gone, or not the user's.
        }

        return files;
    }

    /// <summary>The last segment of a Linux or a Windows path.</summary>
    public static string FileNameOf(string path) => path[(path.LastIndexOfAny(Separators) + 1)..];

    /// <summary>The real UID, as text: it is only ever compared.</summary>
    public static string? ReadUid(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
            }
        }
        catch
        {
            // Gone mid-scan.
        }

        return null;
    }

    /// <summary>False once the process is gone or is a zombie (state Z) or dead (X) waiting to be reaped.</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            // "pid (comm) S ...": comm may hold spaces and parentheses, so the state follows the last ')'.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int end = stat.LastIndexOf(')');
            char state = end >= 0 && end + 2 < stat.Length ? stat[end + 2] : 'X';
            return state != 'Z' && state != 'X';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The executable's path as a Linux path. A Wine process gives a Windows path, translated through
    /// its prefix's drive links; if that fails it keeps its Windows form with '/' separators, which
    /// still yields the file name and the parent folders.
    /// </summary>
    public static string? TryGetImagePath(int pid)
    {
        string? argv0 = ReadArgv0(pid);
        if (argv0 == null)
            return null;

        if (IsAbsoluteWindowsPath(argv0))
            return TranslateWinePath(pid, argv0) ?? argv0.Replace('\\', '/');

        // A relative Windows name: /proc/<pid>/exe would be the Wine loader, the wrong file to name or blacklist.
        if (argv0.Contains('\\') || argv0.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return argv0.Replace('\\', '/');

        if (argv0.StartsWith('/'))
            return argv0;

        // A relative argv[0] ("./game") says nothing about the folder; the kernel's link does.
        try
        {
            return new FileInfo($"/proc/{pid}/exe").LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAbsoluteWindowsPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static string? TranslateWinePath(int pid, string windowsPath)
    {
        try
        {
            string prefix = ReadEnvironmentVariable(pid, "WINEPREFIX")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");

            // dosdevices holds one link per drive: "c:" to ../drive_c, "z:" to "/".
            var drive = new DirectoryInfo(Path.Combine(prefix, "dosdevices", char.ToLowerInvariant(windowsPath[0]) + ":"));
            string? root = drive.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (root == null)
                return null;

            return Path.Combine(root, windowsPath[3..].Replace('\\', '/'));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The variable as the process started with it. Throws if the process is gone or not the user's.</summary>
    public static string? ReadEnvironmentVariable(int pid, string name)
    {
        string entryPrefix = name + "=";
        foreach (string entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
        {
            if (entry.StartsWith(entryPrefix, StringComparison.Ordinal))
                return entry[entryPrefix.Length..];
        }

        return null;
    }
}
