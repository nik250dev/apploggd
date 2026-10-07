using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Emulation.Dolphin;

/// <summary>Raised when Dolphin cannot be opened for reading, which forces the degraded mode.</summary>
internal sealed class DolphinAccessDeniedException : Exception
{
    public DolphinAccessDeniedException(string message) : base(message) { }
}

/// <summary>The disc Dolphin is running. A null <paramref name="IsWii"/> means the console is unknown.</summary>
internal sealed record DolphinDisc(string GameId, bool? IsWii);

/// <summary>
/// The disc Dolphin is running, read out of the emulated RAM: the boot process copies the disc
/// header to the start of MEM1, so its first bytes are the game ID and the Wii or GameCube magic.
/// MEM1 is a file mapping Dolphin creates on boot and unmaps on Stop, so a valid header is also
/// proof that a game is loaded. Nothing is written and no code is injected.
/// </summary>
internal static class DolphinMemoryReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    // The smallest MEM1 there is (24 MB). Not an exact size: Dolphin's RAM override setting changes it.
    private const long MinMem1Size = 0x1800000;
    private const int HeaderSize = 0x20;
    private const uint WiiMagic = 0x5D1C9EA3;
    private const uint GameCubeMagic = 0xC2339F3D;

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

    /// <summary>
    /// Null when no game is loaded. <paramref name="cachedBase"/> skips the address space walk
    /// while the same MEM1 stays mapped; it is reset once the header there stops being valid.
    /// </summary>
    /// <exception cref="DolphinAccessDeniedException">The process cannot be opened for reading.</exception>
    public static DolphinDisc? Read(int processId, ref long cachedBase)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, processId);
        if (handle == IntPtr.Zero)
        {
            throw new DolphinAccessDeniedException(
                $"OpenProcess was denied for Dolphin (PID {processId}), error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (cachedBase != 0)
            {
                var cached = ReadHeader(handle, cachedBase);
                if (cached != null)
                    return cached;

                cachedBase = 0;
            }

            long address = 0;
            int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

            while (VirtualQueryEx(handle, new IntPtr(address), out var region, new IntPtr(structSize)) != IntPtr.Zero)
            {
                long regionBase = region.BaseAddress.ToInt64();
                long regionSize = region.RegionSize.ToInt64();
                if (regionSize <= 0)
                    break;

                // Dolphin maps several views of the same section; any of them holds the header.
                if (IsMem1Candidate(region))
                {
                    var disc = ReadHeader(handle, regionBase);
                    if (disc != null)
                    {
                        cachedBase = regionBase;
                        return disc;
                    }
                }

                address = regionBase + regionSize;
                if (address <= regionBase)
                    break;
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsMem1Candidate(MEMORY_BASIC_INFORMATION region)
    {
        return region.State == MEM_COMMIT
               && region.Type == MEM_MAPPED
               && region.RegionSize.ToInt64() >= MinMem1Size
               && (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
    }

    private static DolphinDisc? ReadHeader(IntPtr handle, long address)
    {
        var header = new byte[HeaderSize];

        try
        {
            if (!ReadProcessMemory(handle, new IntPtr(address), header, new IntPtr(HeaderSize), out IntPtr read)
                || read.ToInt64() != HeaderSize)
                return null;
        }
        catch
        {
            return null;
        }

        bool isWii = BigEndian(header, 0x18) == WiiMagic;
        if (!isWii && BigEndian(header, 0x1C) != GameCubeMagic)
            return null;

        string? gameId = ParseGameId(header);
        return gameId != null ? new DolphinDisc(gameId, isWii) : null;
    }

    /// <summary>Six upper-case letters or digits, the GameTDB form; anything else is not a disc header.</summary>
    private static string? ParseGameId(byte[] header)
    {
        for (int i = 0; i < 6; i++)
        {
            byte c = header[i];
            if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'0' and <= (byte)'9'))
                return null;
        }

        return Encoding.ASCII.GetString(header, 0, 6);
    }

    private static uint BigEndian(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);
    }
}
