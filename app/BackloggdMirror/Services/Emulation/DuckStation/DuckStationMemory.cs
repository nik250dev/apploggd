using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Emulation.DuckStation;

/// <summary>Raised when DuckStation cannot be opened for reading, which forces the window title mode.</summary>
internal sealed class DuckStationAccessDeniedException : Exception
{
    public DuckStationAccessDeniedException(string message) : base(message) { }
}

/// <summary>The game DuckStation is running, as it identified it itself. <paramref name="Serial"/> is empty for discs without one.</summary>
internal sealed record DuckStationRunningGame(string Path, string Serial, string Title);

/// <summary>Where the running game lives in one DuckStation process, kept between readings.</summary>
internal sealed class DuckStationMemoryLocation
{
    public long ModuleBase;
    public long ModuleEnd;
    public long Address;
}

/// <summary>
/// The running game, read out of DuckStation's memory. Its system state is a global in the data
/// sections of the executable holding three std::strings in a row: path, serial and title. DuckStation
/// fills them on boot and on disc changes, keeps them while paused and clears them on shutdown.
/// The scan finds them by shape, so it only works while a game runs; after that the address is cached
/// and each read is 96 bytes. Nothing is written and no code is injected.
/// </summary>
internal static class DuckStationMemory
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
    private const int TripleSize = 3 * StringSize;
    private const int MaxPathLength = 4096;
    private const int MaxSerialLength = 64;
    private const int MaxTitleLength = 1024;
    private const long MaxBytesToScan = 64L * 1024 * 1024;

    // A disc in a real drive: "\\.\D:".
    private const string PhysicalDrivePrefix = @"\\.\";

    private static readonly Regex AbsolutePath = new(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);
    // "SCUS-94900", or the "HASH-..." DuckStation makes up for executables.
    private static readonly Regex SerialShape = new(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]*$", RegexOptions.Compiled);

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
    /// Null while DuckStation sits in its game list or runs the BIOS alone. The executable is scanned
    /// until the strings show up; <paramref name="location"/> keeps where they are for the next readings.
    /// </summary>
    /// <exception cref="DuckStationAccessDeniedException">The process cannot be opened or bounded.</exception>
    public static DuckStationRunningGame? Read(Process process, DuckStationMemoryLocation location)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new DuckStationAccessDeniedException(
                $"OpenProcess was denied for DuckStation (PID {process.Id}), error {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (location.Address != 0)
            {
                var buffer = new byte[TripleSize];
                if (ReadAll(handle, location.Address, buffer) && TryDecodeState(handle, buffer, 0, out var game))
                    return game;

                // Not the strings anymore: scan again on the next reading.
                location.Address = 0;
                return null;
            }

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
                ?? throw new DuckStationAccessDeniedException($"DuckStation (PID {process.Id}) exposes no main module.");
            long moduleBase = mainModule.BaseAddress.ToInt64();
            return (moduleBase, moduleBase + mainModule.ModuleMemorySize);
        }
        catch (DuckStationAccessDeniedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DuckStationAccessDeniedException($"The main module of DuckStation (PID {process.Id}) could not be read: {ex.Message}");
        }
    }

    private static DuckStationRunningGame? Scan(IntPtr handle, long moduleBase, long moduleEnd, out long found)
    {
        found = 0;
        long address = moduleBase;
        long totalRead = 0;
        int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (address < moduleEnd && totalRead < MaxBytesToScan)
        {
            if (VirtualQueryEx(handle, new IntPtr(address), out var region, new IntPtr(structSize)) == IntPtr.Zero)
                break;

            long regionBase = region.BaseAddress.ToInt64();
            long regionSize = region.RegionSize.ToInt64();
            if (regionSize <= 0)
                break;

            long readable = Math.Min(regionSize, moduleEnd - regionBase);
            if (IsWritableData(region) && readable >= TripleSize && readable <= int.MaxValue)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)readable);
                try
                {
                    if (ReadProcessMemory(handle, new IntPtr(regionBase), buffer, new IntPtr(readable), out IntPtr read))
                    {
                        int length = (int)read.ToInt64();
                        totalRead += length;

                        for (int offset = 0; offset + TripleSize <= length; offset += 8)
                        {
                            if (!LooksLikeState(buffer, offset) || !TryDecodeState(handle, buffer, offset, out var game) || game == null)
                                continue;

                            // Its folders are static strings in a row too, but those name directories.
                            if (!File.Exists(game.Path) && !game.Path.StartsWith(PhysicalDrivePrefix, StringComparison.Ordinal))
                                continue;

                            found = regionBase + offset;
                            return game;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            address = regionBase + regionSize;
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
        ulong pathSize = BitConverter.ToUInt64(buffer, offset + 16);
        ulong serialSize = BitConverter.ToUInt64(buffer, offset + StringSize + 16);
        return pathSize >= 3 && pathSize <= MaxPathLength && serialSize <= MaxSerialLength
               && BitConverter.ToUInt64(buffer, offset + 24) >= InlineCapacity
               && BitConverter.ToUInt64(buffer, offset + StringSize + 24) >= InlineCapacity
               && BitConverter.ToUInt64(buffer, offset + 2 * StringSize + 24) >= InlineCapacity;
    }

    /// <summary>False when the bytes are not the three strings; true with a null game when they are, empty.</summary>
    private static bool TryDecodeState(IntPtr handle, byte[] buffer, int offset, out DuckStationRunningGame? game)
    {
        game = null;

        string? path = DecodeString(handle, buffer, offset, MaxPathLength);
        string? serial = DecodeString(handle, buffer, offset + StringSize, MaxSerialLength);
        string? title = DecodeString(handle, buffer, offset + 2 * StringSize, MaxTitleLength);
        if (path == null || serial == null || title == null)
            return false;

        if (path.Length == 0)
            return serial.Length == 0 && title.Length == 0;

        if (!AbsolutePath.IsMatch(path) || (serial.Length > 0 && !SerialShape.IsMatch(serial)))
            return false;

        game = new DuckStationRunningGame(path, serial, title);
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
