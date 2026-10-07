using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// The Linux side of blacklisting a file by hand. Windows limits the picker to *.exe, but on Linux a native
/// game is an extensionless binary or a "Game.x86_64" while a Proton game is still its .exe, so the picker
/// offers every file and this checks it afterwards.
/// </summary>
internal static class LinuxBlacklistFile
{
    public enum Problem
    {
        None,

        /// <summary>A "start.sh": its process is the shell, never the game it starts.</summary>
        Script,

        NotExecutable
    }

    private static readonly Regex InstallDirPattern = new("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
    private static readonly Regex NamePattern = new("\"name\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);

    /// <summary>By content rather than permissions: Steam marks every file of a game executable, its .ogg included.</summary>
    public static Problem Check(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Problem.None;

        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            if (read >= 2 && head[0] == '#' && head[1] == '!')
                return Problem.Script;

            bool elf = read == 4 && head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
            return elf ? Problem.None : Problem.NotExecutable;
        }
        catch
        {
            // Unreadable here, but a process may still run it.
            return Problem.None;
        }
    }

    /// <summary>
    /// The path with every symlink resolved, so the one picked and the one a process reports compare equal:
    /// a Steam library is often reached through a link (~/.steam/steam). The path itself if it does not exist.
    /// </summary>
    public static string CanonicalPath(string path)
    {
        try
        {
            IntPtr resolved = realpath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero)
                return path;

            try
            {
                return Marshal.PtrToStringUTF8(resolved) ?? path;
            }
            finally
            {
                free(resolved);
            }
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// The store name of the Steam game the file belongs to, from its library's appmanifest. Linux files
    /// carry no version resource to name them by, and "Game.x86_64" or "Game.exe" say nothing.
    /// </summary>
    public static string? SteamNameOf(string path)
    {
        const string common = "/steamapps/common/";
        int at = path.IndexOf(common, StringComparison.Ordinal);
        if (at < 0)
            return null;

        string rest = path[(at + common.Length)..];
        int slash = rest.IndexOf('/');
        if (slash <= 0)
            return null;

        string installDir = rest[..slash];
        try
        {
            foreach (string manifest in Directory.EnumerateFiles(path[..at] + "/steamapps", "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(manifest);
                var dir = InstallDirPattern.Match(text);
                if (!dir.Success || !string.Equals(dir.Groups[1].Value, installDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = NamePattern.Match(text);
                return name.Success && !string.IsNullOrWhiteSpace(name.Groups[1].Value) ? name.Groups[1].Value.Trim() : null;
            }
        }
        catch
        {
            // No readable manifests: the file name will do.
        }

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

    [DllImport("libc")]
    private static extern void free(IntPtr ptr);
}
