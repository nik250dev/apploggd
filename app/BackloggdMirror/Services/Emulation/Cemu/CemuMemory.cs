using System;
using System.Runtime.InteropServices;

namespace BackloggdMirror.Services.Emulation.Cemu;

/// <summary>
/// Whether Cemu has a game loaded, from the state of its emulated address space. Cemu reserves 4 GB
/// for the Wii U at startup and keeps it for its whole life, commits MEM2 (1 GB at +0x10000000)
/// when a title boots and decommits it on "Stop emulation". Only the region state is queried, never
/// its contents, so PROCESS_QUERY_LIMITED_INFORMATION is enough, even for an elevated Cemu.
/// </summary>
internal static class CemuMemory
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_PRIVATE = 0x20000;

    private const long AddressSpaceSize = 0x100000000;

    // The last page of MEM2: the kernel sizes the region by walking forward page by page, which from
    // its first page means 1.25 GB of committed memory and ~9 ms, and from here one page.
    private const long Mem2LastPage = 0x10000000 + 0x40000000 - 0x1000;

    // Mapped by Cemu before any title boots, so they tell its reservation apart from any other 4 GB one.
    private const long CemuAreaOffset = 0x0E000000;
    private const long SharedAreaOffset = 0xF8000000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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

    /// <summary>
    /// Null when the answer is unknown: the process cannot be queried, or its Wii U address space is
    /// not there (Cemu still starting, or a build that lays it out differently). <paramref name="cachedBase"/>
    /// skips the address space walk; the reservation never moves while the process lives.
    /// </summary>
    public static bool? IsGameLoaded(int processId, ref long cachedBase)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            if (cachedBase != 0)
            {
                if (Query(handle, cachedBase + Mem2LastPage) is { } cached && cached.AllocationBase.ToInt64() == cachedBase)
                    return cached.State == MEM_COMMIT;

                cachedBase = 0;
            }

            cachedBase = FindAddressSpace(handle);
            if (cachedBase == 0)
                return null;

            return Query(handle, cachedBase + Mem2LastPage) is { State: MEM_COMMIT };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static long FindAddressSpace(IntPtr handle)
    {
        long address = 0;
        long allocationBase = -1;
        long allocationSize = 0;

        while (Query(handle, address) is { } region)
        {
            long regionBase = region.BaseAddress.ToInt64();
            long regionSize = region.RegionSize.ToInt64();
            if (regionSize <= 0)
                break;

            if (region.State != MEM_FREE)
            {
                long owner = region.AllocationBase.ToInt64();
                if (owner != allocationBase)
                {
                    if (allocationSize == AddressSpaceSize && IsWiiUAddressSpace(handle, allocationBase))
                        return allocationBase;

                    allocationBase = owner;
                    allocationSize = 0;
                }

                allocationSize += regionSize;
            }

            address = regionBase + regionSize;
            if (address <= regionBase)
                break;
        }

        return allocationSize == AddressSpaceSize && IsWiiUAddressSpace(handle, allocationBase) ? allocationBase : 0;
    }

    private static bool IsWiiUAddressSpace(IntPtr handle, long baseAddress)
    {
        return Query(handle, baseAddress) is { Type: MEM_PRIVATE } first
               && first.AllocationBase.ToInt64() == baseAddress
               && Query(handle, baseAddress + CemuAreaOffset) is { State: MEM_COMMIT } cemuArea
               && cemuArea.AllocationBase.ToInt64() == baseAddress
               && Query(handle, baseAddress + SharedAreaOffset) is { State: MEM_COMMIT } sharedArea
               && sharedArea.AllocationBase.ToInt64() == baseAddress;
    }

    private static MEMORY_BASIC_INFORMATION? Query(IntPtr handle, long address)
    {
        int size = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        return VirtualQueryEx(handle, new IntPtr(address), out var region, new IntPtr(size)) != IntPtr.Zero ? region : null;
    }
}
