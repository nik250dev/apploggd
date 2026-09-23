using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.Dolphin;

/// <summary>
/// The disc image Dolphin has open, found among its file handles. Only a fallback for naming a
/// game the title database does not know (hacks, homebrew): it walks every handle in the system,
/// so it runs once per identification and never on the polling path.
/// </summary>
internal static class DolphinOpenFiles
{
    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int MaxBufferSize = 256 * 1024 * 1024;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;
    private const uint FILE_TYPE_DISK = 0x1;

    private static readonly HashSet<string> DiscExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".gcm", ".tgc", ".rvz", ".wia", ".gcz", ".wbfs", ".ciso", ".nfs", ".dol", ".elf", ".wad"
    };

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static string? FindDiscImage(int processId)
    {
        IntPtr process = OpenProcess(PROCESS_DUP_HANDLE, false, processId);
        if (process == IntPtr.Zero)
            return null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = QueryHandles();
            if (buffer == IntPtr.Zero)
                return null;

            // SYSTEM_HANDLE_INFORMATION_EX: two pointer-sized counters, then the entries.
            int pointer = IntPtr.Size;
            int entrySize = pointer * 3 + 16;
            long count = Marshal.ReadIntPtr(buffer).ToInt64();
            IntPtr current = GetCurrentProcess();

            for (long i = 0; i < count; i++)
            {
                IntPtr entry = buffer + pointer * 2 + (int)(i * entrySize);
                if (Marshal.ReadIntPtr(entry, pointer).ToInt64() != processId)
                    continue;

                IntPtr value = Marshal.ReadIntPtr(entry, pointer * 2);
                if (!DuplicateHandle(process, value, current, out IntPtr duplicate, 0, false, DUPLICATE_SAME_ACCESS))
                    continue;

                try
                {
                    // Checked first because resolving the name of a pipe can block forever.
                    if (GetFileType(duplicate) != FILE_TYPE_DISK)
                        continue;

                    string? path = FinalPath(duplicate);
                    if (path != null && DiscExtensions.Contains(Path.GetExtension(path)))
                        return path;
                }
                finally
                {
                    CloseHandle(duplicate);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }

    private static IntPtr QueryHandles()
    {
        int length = 4 * 1024 * 1024;

        while (length <= MaxBufferSize)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out int needed);

            if (status == 0)
                return buffer;

            Marshal.FreeHGlobal(buffer);

            if (status != STATUS_INFO_LENGTH_MISMATCH)
                return IntPtr.Zero;

            // Handles keep being opened between the two calls, hence the margin.
            length = Math.Max(length * 2, needed + 1024 * 1024);
        }

        return IntPtr.Zero;
    }

    private static string? FinalPath(IntPtr handle)
    {
        var path = new StringBuilder(1024);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            return null;

        string value = path.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + value[8..];
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
            return value[4..];

        return value;
    }
}
