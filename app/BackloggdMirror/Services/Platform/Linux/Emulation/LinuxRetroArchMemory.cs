using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BackloggdMirror.Services.Emulation;

namespace BackloggdMirror.Services.Platform.Linux.Emulation;

/// <param name="Readable">False when the kernel denies reading the memory; the paths are then empty.</param>
internal sealed record LinuxRetroArchMemoryScan(bool Readable, IReadOnlyList<string> Paths, long BytesRead, long ElapsedMilliseconds);

/// <summary>
/// The content RetroArch has loaded, read out of its memory as the Windows detector does: the path
/// (<c>path_content</c>) lives in the data and bss of the retroarch executable, alone between NULs, and
/// is emptied on "Close Content". The command line RetroArch keeps is on the heap, outside this range.
///
/// Reading /proc/&lt;pid&gt;/mem is only allowed where Yama lets it: a Flatpak runs in a user namespace its user
/// created, whose owner may read it, while a native process under ptrace_scope 1 (Ubuntu) is denied.
/// Nothing is written and the process is not stopped.
/// </summary>
internal static class LinuxRetroArchMemory
{
    private const int MinPathLength = 8;
    private const int MaxPathLength = 4096;
    private const long MaxBytesToScan = 32L * 1024 * 1024;

    public static LinuxRetroArchMemoryScan Read(int pid)
    {
        var stopwatch = Stopwatch.StartNew();
        var regions = DataRegions(pid);
        if (regions.Count == 0)
            return new LinuxRetroArchMemoryScan(true, Array.Empty<string>(), 0, 0);

        Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new LinuxRetroArchMemoryScan(false, Array.Empty<string>(), 0, 0);
        }

        var paths = new List<string>();
        long total = 0;

        using (handle)
        {
            foreach (var (start, end) in regions)
            {
                int size = (int)Math.Min(end - start, MaxBytesToScan - total);
                if (size <= 0)
                    break;

                byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    int read = RandomAccess.Read(handle, buffer.AsSpan(0, size), start);
                    total += read;
                    Scan(buffer, read, paths);
                }
                catch (IOException)
                {
                    // Unmapped between the maps read and this one.
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        return new LinuxRetroArchMemoryScan(true, paths, total, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>The writable mappings of the executable (.data) and the anonymous one right after them (.bss).</summary>
    private static List<(long Start, long End)> DataRegions(int pid)
    {
        var regions = new List<(long, long)>();

        try
        {
            string? exe = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (exe == null)
                return regions;

            long lastExeEnd = -1;
            foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            {
                // "start-end perms offset dev inode path", the path from the first '/' and possibly with spaces.
                int dash = line.IndexOf('-');
                int space = line.IndexOf(' ');
                long start = long.Parse(line.AsSpan(0, dash), NumberStyles.HexNumber);
                long end = long.Parse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber);
                bool writable = line[space + 2] == 'w';
                int slash = line.IndexOf('/');
                string path = slash >= 0 ? line[slash..] : string.Empty;
                bool anonymous = slash < 0 && line.IndexOf('[') < 0;

                if (path == exe)
                {
                    if (writable)
                        regions.Add((start, end));
                    lastExeEnd = end;
                }
                else if (anonymous && writable && start == lastExeEnd)
                {
                    regions.Add((start, end));
                    lastExeEnd = -1;
                }
                else
                {
                    lastExeEnd = -1;
                }
            }
        }
        catch
        {
            // Gone mid-read: no regions, no content.
        }

        return regions;
    }

    /// <summary>Absolute paths standing alone between NULs whose extension (or the one inside an archive) is a ROM's.</summary>
    private static void Scan(byte[] buffer, int length, List<string> paths)
    {
        int i = 0;
        while (i < length)
        {
            if (buffer[i] != (byte)'/' || (i > 0 && buffer[i - 1] != 0))
            {
                i++;
                continue;
            }

            int end = i;
            int limit = Math.Min(length, i + MaxPathLength);
            while (end < limit && buffer[end] != 0)
                end++;

            if (end < limit && end - i >= MinPathLength)
            {
                string? path = Decode(buffer, i, end - i);
                if (path != null && IsContent(path) && !paths.Contains(path))
                    paths.Add(path);
            }

            i = end + 1;
        }
    }

    private static string? Decode(byte[] buffer, int start, int count)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(buffer, start, count);
        }
        catch
        {
            // Not valid UTF-8: not a path RetroArch wrote.
            return null;
        }
    }

    private static bool IsContent(string path)
    {
        var content = LinuxRetroArchContent.From(path);
        return EmulatedPlatformResolver.IsContentExtension(content.Extension);
    }
}
