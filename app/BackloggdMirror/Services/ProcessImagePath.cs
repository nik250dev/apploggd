using System;
using System.Runtime.InteropServices;
using System.Text;
using BackloggdMirror.Services.Platform.Linux;

namespace BackloggdMirror.Services;

/// <summary>
/// The full path of a process's executable. Unlike <c>Process.MainModule</c>, it only needs
/// PROCESS_QUERY_LIMITED_INFORMATION, which crosses integrity levels, so it also works for elevated processes.
/// On Linux it comes from /proc instead (see <see cref="LinuxProcFs"/>).
/// </summary>
internal static class ProcessImagePath
{
    public static string? TryGet(int processId)
    {
        if (OperatingSystem.IsLinux())
            return LinuxProcFs.TryGetImagePath(processId);

        // kernel32 exists only on Windows.
        if (!OperatingSystem.IsWindows())
            return null;

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
}
