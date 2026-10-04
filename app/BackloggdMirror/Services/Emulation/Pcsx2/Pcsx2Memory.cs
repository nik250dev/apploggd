using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation.Pcsx2;

/// <summary>Raised when PCSX2 cannot be opened for reading, which forces the window title mode.</summary>
internal sealed class Pcsx2AccessDeniedException : Exception
{
    public Pcsx2AccessDeniedException(string message) : base(message) { }
}

/// <summary>
/// The disc PCSX2 is running, as it identified it itself. <paramref name="Serial"/> is empty when the boot
/// file is not named like one; <paramref name="EnglishTitle"/> is empty unless its game database gives the
/// title in another language too.
/// </summary>
internal sealed record Pcsx2RunningGame(string Serial, string BootElf, string Title, string EnglishTitle);

/// <summary>Where the running game lives in one PCSX2 process, kept between readings.</summary>
internal sealed class Pcsx2MemoryLocation
{
    public long ModuleBase;
    public long ModuleEnd;
    public long Address;
}

/// <summary>
/// The running disc, read out of PCSX2's memory. VMManager keeps it in seven std::strings declared in a
/// row in the data sections of the executable: serial, boot ELF, version, title, the two halves of the
/// English title replacement and region. PCSX2 fills them on boot and on disc changes, keeps them while
/// paused and clears them on shutdown; booting the BIOS alone leaves the boot ELF empty. The scan finds
/// them by shape, so it only works while a game runs; after that the address is cached and each read is
/// 224 bytes. Nothing is written and no code is injected.
/// </summary>
internal static class Pcsx2Memory
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    // MSVC's std::string on x64: a 16-byte buffer (or a pointer once it outgrows it), the size and the capacity.
    private const int StringSize = 32;
    private const int InlineCapacity = 15;
    private const int StringCount = 7;
    private const int StateSize = StringCount * StringSize;
    private const int SerialIndex = 0, ElfIndex = 1, TitleIndex = 3, EnglishTitleIndex = 5;
    // Serial, boot ELF, version, title, English search, English title, region.
    private static readonly int[] MaxLengths = { 64, 4096, 64, 1024, 1024, 1024, 16 };

    // The data sections end in ~200 MB of zeroed arrays; the strings sit in the first kilobytes.
    private const long MaxBytesToScan = 4L * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;

    // "SLUS-21678", or whatever a boot file named like "????_???.??*" turns into.
    private static readonly Regex SerialShape = new(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]*$", RegexOptions.Compiled);
    // "cdrom0:\SLUS_216.78;1" on PS2 discs, "cdrom:\SCUS_949.00;1" on PlayStation ones.
    private static readonly Regex BootElfShape = new(@"^cdrom0?:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    /// Null while PCSX2 sits in its game list or runs the BIOS alone. The executable is scanned until the
    /// strings show up, and only if <paramref name="mayScan"/>; <paramref name="location"/> keeps where they are for the next readings.
    /// </summary>
    /// <exception cref="Pcsx2AccessDeniedException">The process cannot be opened or bounded.</exception>
    public static Pcsx2RunningGame? Read(Process process, Pcsx2MemoryLocation location, bool mayScan)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new Pcsx2AccessDeniedException(
                $"OpenProcess was denied for PCSX2 (PID {process.Id}), error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (location.Address != 0)
            {
                var buffer = new byte[StateSize];
                if (ReadAll(handle, location.Address, buffer) && TryDecodeState(handle, buffer, 0, out var game))
                    return game;

                // Not the strings anymore: scan again on the next reading.
                location.Address = 0;
                return null;
            }

            if (!mayScan)
                return null;

            if (location.ModuleEnd == 0)
                (location.ModuleBase, location.ModuleEnd) = MainModuleRange(process);

            var found = Scan(handle, location.ModuleBase, location.ModuleEnd, out long address);
            location.Address = address;
            return found;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static (long Base, long End) MainModuleRange(Process process)
    {
        try
        {
            var mainModule = process.MainModule
                ?? throw new Pcsx2AccessDeniedException($"PCSX2 (PID {process.Id}) exposes no main module.");
            long moduleBase = mainModule.BaseAddress.ToInt64();
            return (moduleBase, moduleBase + mainModule.ModuleMemorySize);
        }
        catch (Pcsx2AccessDeniedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Pcsx2AccessDeniedException($"The main module of PCSX2 (PID {process.Id}) could not be read: {ex.Message}");
        }
    }

    private static Pcsx2RunningGame? Scan(IntPtr handle, long moduleBase, long moduleEnd, out long found)
    {
        found = 0;
        long address = moduleBase;
        long totalRead = 0;
        int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        var buffer = new byte[ChunkSize];

        while (address < moduleEnd && totalRead < MaxBytesToScan)
        {
            if (VirtualQueryEx(handle, new IntPtr(address), out var region, new IntPtr(structSize)) == IntPtr.Zero)
                break;

            long regionBase = region.BaseAddress.ToInt64();
            long regionEnd = Math.Min(regionBase + region.RegionSize.ToInt64(), moduleEnd);
            if (regionEnd <= regionBase)
                break;

            if (IsWritableData(region))
            {
                // Chunks overlap by one state, so strings across a chunk boundary are not missed.
                for (long chunk = regionBase; chunk + StateSize <= regionEnd && totalRead < MaxBytesToScan; chunk += ChunkSize - StateSize)
                {
                    int wanted = (int)Math.Min(ChunkSize, regionEnd - chunk);
                    if (!ReadProcessMemory(handle, new IntPtr(chunk), buffer, new IntPtr(wanted), out IntPtr read))
                        break;

                    int length = (int)read.ToInt64();
                    totalRead += length;

                    for (int offset = 0; offset + StateSize <= length; offset += 8)
                    {
                        if (!LooksLikeState(buffer, offset) || !TryDecodeState(handle, buffer, offset, out var game) || game == null)
                            continue;

                        found = chunk + offset;
                        return game;
                    }

                    if (chunk + length >= regionEnd)
                        break;
                }
            }

            address = regionEnd;
        }

        return null;
    }

    private static bool IsWritableData(MEMORY_BASIC_INFORMATION region)
    {
        if (region.State != MEM_COMMIT)
            return false;

        if ((region.Protect & PAGE_GUARD) != 0 || (region.Protect & PAGE_NOACCESS) != 0)
            return false;

        return (region.Protect & (PAGE_READWRITE | PAGE_WRITECOPY)) != 0;
    }

    /// <summary>Cheap filter on the sizes alone before following any pointer.</summary>
    private static bool LooksLikeState(byte[] buffer, int offset)
    {
        for (int i = 0; i < StringCount; i++)
        {
            int at = offset + i * StringSize;
            ulong size = BitConverter.ToUInt64(buffer, at + 16);
            ulong capacity = BitConverter.ToUInt64(buffer, at + 24);
            if (capacity < InlineCapacity || size > capacity || size > (ulong)MaxLengths[i])
                return false;
        }

        // Only a booted disc has a boot file, and it is at least "cdrom:".
        ulong elfSize = BitConverter.ToUInt64(buffer, offset + ElfIndex * StringSize + 16);
        return elfSize >= 6;
    }

    /// <summary>False when the bytes are not the strings; true with a null game when they are and no disc runs.</summary>
    private static bool TryDecodeState(IntPtr handle, byte[] buffer, int offset, out Pcsx2RunningGame? game)
    {
        game = null;

        var values = new string[StringCount];
        for (int i = 0; i < StringCount; i++)
        {
            string? value = DecodeString(handle, buffer, offset + i * StringSize, MaxLengths[i]);
            if (value == null)
                return false;
            values[i] = value;
        }

        string serial = values[SerialIndex];
        string bootElf = values[ElfIndex];

        // Shut down, or the BIOS alone (its serial is then the BIOS build date).
        if (bootElf.Length == 0)
            return true;

        if (!BootElfShape.IsMatch(bootElf) || (serial.Length > 0 && !SerialShape.IsMatch(serial)))
            return false;

        game = new Pcsx2RunningGame(serial, bootElf, values[TitleIndex], values[EnglishTitleIndex]);
        return true;
    }

    private static string? DecodeString(IntPtr handle, byte[] buffer, int offset, int maxLength)
    {
        ulong size = BitConverter.ToUInt64(buffer, offset + 16);
        ulong capacity = BitConverter.ToUInt64(buffer, offset + 24);
        if (capacity < InlineCapacity || size > capacity || size > (ulong)maxLength)
            return null;

        int length = (int)size;
        byte[] text;
        int start;

        if (capacity == InlineCapacity)
        {
            text = buffer;
            start = offset;
        }
        else
        {
            text = new byte[length + 1];
            start = 0;
            if (!ReadAll(handle, BitConverter.ToInt64(buffer, offset), text))
                return null;
        }

        if (text[start + length] != 0)
            return null;

        try
        {
            return new UTF8Encoding(false, true).GetString(text, start, length);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ReadAll(IntPtr handle, long address, byte[] buffer) =>
        address != 0
        && ReadProcessMemory(handle, new IntPtr(address), buffer, new IntPtr(buffer.Length), out IntPtr read)
        && read.ToInt64() == buffer.Length;
}
