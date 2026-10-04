using System;
using System.Buffers.Binary;
using System.Text;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>
/// The PARAM.SFO every PSP game carries, the same values PPSSPP shows: DISC_ID is the serial (ULES00026)
/// and TITLE the name in the disc's language.
/// </summary>
internal sealed record PspParamSfo(string? DiscId, string? Title)
{
    private const int HeaderSize = 20;
    private const int EntrySize = 16;
    private const int MaxEntries = 256;
    private const ushort Utf8Special = 0x0004;
    private const ushort Utf8 = 0x0204;

    public static PspParamSfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 0 || data[1] != 'P' || data[2] != 'S' || data[3] != 'F')
            return null;

        uint keyTable = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint dataTable = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (count > MaxEntries || HeaderSize + count * EntrySize > data.Length)
            return null;

        string? discId = null;
        string? title = null;

        for (int i = 0; i < count; i++)
        {
            var entry = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            if (format != Utf8 && format != Utf8Special)
                continue;

            string? key = ReadString(data, keyTable + BinaryPrimitives.ReadUInt16LittleEndian(entry), int.MaxValue);
            long valueStart = dataTable + BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]), int.MaxValue);

            if (key == "DISC_ID")
                discId = ReadString(data, valueStart, length);
            else if (key == "TITLE")
                title = ReadString(data, valueStart, length);
        }

        return discId == null && title == null ? null : new PspParamSfo(discId, title);
    }

    /// <summary>Up to the first NUL or <paramref name="maxLength"/> bytes; null when empty or out of bounds.</summary>
    private static string? ReadString(ReadOnlySpan<byte> data, long start, int maxLength)
    {
        if (start < 0 || start >= data.Length)
            return null;

        var bytes = data[(int)start..];
        if (bytes.Length > maxLength)
            bytes = bytes[..maxLength];

        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];

        string value = Encoding.UTF8.GetString(bytes).Trim();
        return value.Length > 0 ? value : null;
    }
}
