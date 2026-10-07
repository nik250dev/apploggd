using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>Raised when RetroArch cannot be opened for reading, which forces the degraded mode.</summary>
internal sealed class RetroArchAccessDeniedException : Exception
{
    public RetroArchAccessDeniedException(string message) : base(message) { }
}

/// <summary>
/// A ROM path as RetroArch holds it, with the "archive.zip#inner.gba" form split apart.
/// <paramref name="Embedded"/> means it was carved out of a larger string — in practice the stored
/// command line, which names the ROM for as long as the process lives whether it is loaded or not.
/// </summary>
internal sealed record RetroArchContentPath(string FullPath, string ArchivePath, string? InnerName, string Extension, bool Embedded = false);

internal sealed record RetroArchContentScan(IReadOnlyList<RetroArchContentPath> Paths, long BytesRead, long ElapsedMilliseconds);

/// <summary>
/// The path of the content RetroArch has loaded, read out of its memory. It lives in a global
/// (<c>path_content</c>) inside the writable data sections of retroarch.exe, so the scan stays
/// bounded to the main module instead of the hundreds of megabytes the cores put on the heap.
/// Nothing is written and no code is injected. The path disappears on "Close Content".
/// </summary>
internal static class RetroArchContentReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    private const int MinPathLength = 8;
    private const int MaxPathLength = 4096;
    private const long MaxBytesToScan = 32L * 1024 * 1024;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION buffer, IntPtr length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <exception cref="RetroArchAccessDeniedException">The process cannot be opened or bounded.</exception>
    public static RetroArchContentScan ReadContentPaths(Process process)
    {
        long moduleBase;
        long moduleEnd;
        try
        {
            var mainModule = process.MainModule
                ?? throw new RetroArchAccessDeniedException($"RetroArch (PID {process.Id}) exposes no main module.");
            moduleBase = mainModule.BaseAddress.ToInt64();
            moduleEnd = moduleBase + mainModule.ModuleMemorySize;
        }
        catch (RetroArchAccessDeniedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RetroArchAccessDeniedException($"The main module of RetroArch (PID {process.Id}) could not be read: {ex.Message}");
        }

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new RetroArchAccessDeniedException(
                $"OpenProcess was denied for RetroArch (PID {process.Id}), error {Marshal.GetLastWin32Error()}.");
        }

        var stopwatch = Stopwatch.StartNew();
        long totalRead = 0;
        var candidates = new List<string>();

        try
        {
            long address = moduleBase;
            int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            while (address < moduleEnd && totalRead < MaxBytesToScan)
            {
                if (VirtualQueryEx(handle, new IntPtr(address), out var region, new IntPtr(structSize)) == IntPtr.Zero)
                    break;

                long regionBase = region.BaseAddress.ToInt64();
                long regionSize = region.RegionSize.ToInt64();
                if (regionSize <= 0)
                    break;

                if (IsReadableData(region))
                {
                    long readable = Math.Min(regionSize, moduleEnd - regionBase);
                    if (readable > 0)
                        totalRead += ReadRegion(handle, regionBase, (int)Math.Min(readable, int.MaxValue), candidates);
                }

                address = regionBase + regionSize;
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        stopwatch.Stop();
        return new RetroArchContentScan(BuildContentPaths(candidates), totalRead, stopwatch.ElapsedMilliseconds);
    }

    private static bool IsReadableData(MEMORY_BASIC_INFORMATION region)
    {
        if (region.State != MEM_COMMIT)
            return false;

        if ((region.Protect & PAGE_GUARD) != 0 || (region.Protect & PAGE_NOACCESS) != 0)
            return false;

        // .data/.bss start out copy-on-write and become read-write once touched.
        return (region.Protect & (PAGE_READWRITE | PAGE_WRITECOPY)) != 0;
    }

    private static long ReadRegion(IntPtr handle, long regionBase, int size, List<string> candidates)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            if (!ReadProcessMemory(handle, new IntPtr(regionBase), buffer, new IntPtr(size), out IntPtr read))
                return 0;

            int length = (int)read.ToInt64();
            if (length <= 0)
                return 0;

            ScanBuffer(buffer, length, candidates);
            return length;
        }
        catch
        {
            // A region can be unmapped between the query and the read; the next one still counts.
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>NUL-terminated drive-letter runs in the raw bytes, decoded as UTF-8: Latin-1 would mangle any accent.</summary>
    private static void ScanBuffer(byte[] buffer, int length, List<string> candidates)
    {
        int i = 0;

        while (i + 3 < length)
        {
            if (!IsDriveLetter(buffer[i]) || buffer[i + 1] != (byte)':' || !IsSeparator(buffer[i + 2]))
            {
                i++;
                continue;
            }

            int end = i;
            int limit = Math.Min(length, i + MaxPathLength);
            while (end < limit && buffer[end] != 0)
                end++;

            if (end < limit)
            {
                int candidateLength = end - i;
                if (candidateLength >= MinPathLength)
                {
                    try
                    {
                        candidates.Add(Encoding.UTF8.GetString(buffer, i, candidateLength));
                    }
                    catch
                    {
                        // Not valid UTF-8: not a path RetroArch wrote.
                    }
                }
            }

            i = end + 1;
        }
    }

    private static bool IsDriveLetter(byte value)
    {
        return (value >= (byte)'A' && value <= (byte)'Z') || (value >= (byte)'a' && value <= (byte)'z');
    }

    private static bool IsSeparator(byte value)
    {
        return value == (byte)'\\' || value == (byte)'/';
    }

    private static List<RetroArchContentPath> BuildContentPaths(List<string> candidates)
    {
        var result = new List<RetroArchContentPath>();
        var indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in candidates)
        {
            string? path = NormalizeCandidate(candidate, out bool embedded);
            if (path == null)
                continue;

            if (indexByPath.TryGetValue(path, out int existing))
            {
                // The loaded ROM and the command line hold the same path, in no guaranteed order.
                if (!embedded && result[existing].Embedded)
                    result[existing] = result[existing] with { Embedded = false };

                continue;
            }

            string archivePath = path;
            string? innerName = null;

            int hash = path.IndexOf('#');
            if (hash > 0)
            {
                archivePath = path[..hash];
                innerName = path[(hash + 1)..];
                if (innerName.Length == 0)
                    innerName = null;
            }

            string extension = GetExtension(innerName ?? archivePath);
            if (!EmulatedPlatformResolver.IsContentExtension(extension))
                continue;

            indexByPath[path] = result.Count;
            result.Add(new RetroArchContentPath(path, archivePath, innerName, extension, embedded));
        }

        return result;
    }

    /// <summary>
    /// Keeps the last "X:\" of a candidate, which is what turns the stored command line
    /// (<c>retroarch.exe -L core.dll "rom.zip"</c>) into just the ROM path. Having had to cut
    /// anything away is also what marks the result as <see cref="RetroArchContentPath.Embedded"/>:
    /// a path RetroArch holds on its own sits alone between two NULs. Quotes cannot appear in a
    /// Windows file name, so one is the same signal.
    /// </summary>
    private static string? NormalizeCandidate(string candidate, out bool embedded)
    {
        embedded = false;
        string value = candidate;

        for (int i = value.Length - 2; i > 0; i--)
        {
            if (value[i] == ':' && (value[i + 1] == '\\' || value[i + 1] == '/') && char.IsLetter(value[i - 1]))
            {
                embedded = i > 1;
                value = value[(i - 1)..];
                break;
            }
        }

        int quote = value.IndexOf('"');
        if (quote >= 0)
        {
            value = value[..quote];
            embedded = true;
        }

        value = RetroArchPath.Normalize(value);

        return value.Length >= MinPathLength ? value : null;
    }

    private static string GetExtension(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
