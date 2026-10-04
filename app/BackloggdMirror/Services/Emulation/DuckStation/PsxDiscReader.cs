using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BackloggdMirror.Services.Emulation.Ppsspp;

namespace BackloggdMirror.Services.Emulation.DuckStation;

/// <summary>A PlayStation disc image. <paramref name="GameId"/> is null when it cannot be read (CHD, ECM) or the disc has no SYSTEM.CNF.</summary>
internal sealed record PsxDisc(string? GameId);

/// <summary>
/// The game ID DuckStation gives a disc, worked out the way it does: the executable named by the BOOT line
/// of SYSTEM.CNF, without folders or version, "SCUS_945.70" becoming "SCUS-94570". Raw (2352-byte) and
/// cooked (2048-byte) images are read; PBP gives the DISC_ID of its PARAM.SFO. The format is told by its
/// contents, not its extension, because the path may be a /proc/&lt;pid&gt;/fd link and other .bin files
/// (DuckStation's own pipeline cache) are no discs.
/// </summary>
internal static class PsxDiscReader
{
    private const int RawSectorSize = 2352;
    private const int UserDataSize = 2048;
    private const int PrimaryVolumeSector = 16;
    private const int MaxDirectorySectors = 64;
    private const int MaxSystemCnfSize = 64 * 1024;

    private static readonly byte[] SyncPattern = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
    private static readonly Regex PbpDiscId = new(@"^([A-Z]{4})(\d{5})$", RegexOptions.Compiled);

    /// <summary>Null when the file is no PlayStation disc image, an audio track included. Never throws.</summary>
    public static PsxDisc? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var magic = new byte[16];
            if (stream.Read(magic, 0, magic.Length) < magic.Length)
                return null;

            if (magic.AsSpan(0, 8).SequenceEqual("MComprHD"u8) || magic.AsSpan(0, 4).SequenceEqual("ECM\0"u8))
                return new PsxDisc(null);

            if (magic.AsSpan(0, 4).SequenceEqual("\0PBP"u8))
                return new PsxDisc(FromPbp(PspImageReader.Read(path)?.DiscId));

            var image = DiscImage.Open(stream, magic);
            return image == null ? null : new PsxDisc(GameIdOf(image));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"cdrom:\SCUS_945.70;1" → "SCUS-94570", as DuckStation's GetGameDetailsFromImage does it.</summary>
    internal static string? GameIdFromBootPath(string boot)
    {
        string code = boot;
        if (code.StartsWith("cdrom:", StringComparison.Ordinal))
            code = code[6..];

        code = code.TrimStart('/', '\\');

        int version = code.LastIndexOf(';');
        if (version >= 0)
            code = code[..version];

        int folder = code.LastIndexOf('\\');
        if (folder >= 0)
            code = code[(folder + 1)..];

        var id = new StringBuilder(code.Length);
        foreach (char c in code)
        {
            if (c == '.')
                continue;

            id.Append(c == '_' ? '-' : char.ToUpperInvariant(c));
        }

        return id.Length > 0 ? id.ToString() : null;
    }

    /// <summary>The value of the BOOT line, parsed as DuckStation does: blanks dropped, the first '=' splitting key and value.</summary>
    internal static string? BootPathOf(string systemCnf)
    {
        foreach (string line in systemCnf.Split('\r', '\n'))
        {
            var key = new StringBuilder();
            var value = new StringBuilder();
            bool readingValue = false;

            foreach (char c in line)
            {
                if (c == ' ' || (c >= '\t' && c <= '\r'))
                    continue;

                if (c == '=' && !readingValue)
                    readingValue = true;
                else if (readingValue)
                    value.Append(c);
                else
                    key.Append(c);
            }

            if (key.ToString().Equals("BOOT", StringComparison.OrdinalIgnoreCase))
                return value.ToString();
        }

        return null;
    }

    private static string? GameIdOf(DiscImage image)
    {
        byte[]? volume = image.ReadSector(PrimaryVolumeSector);
        if (volume == null || volume[0] != 1 || !volume.AsSpan(1, 5).SequenceEqual("CD001"u8))
            return null;

        // The root directory record sits at 156: its extent at +2 and its size at +10, little-endian.
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(156 + 2));
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(156 + 10));
        int sectors = (int)Math.Min((rootSize + UserDataSize - 1) / UserDataSize, MaxDirectorySectors);

        for (int i = 0; i < sectors; i++)
        {
            byte[]? directory = image.ReadSector(rootSector + i);
            if (directory == null)
                return null;

            for (int offset = 0; offset < UserDataSize;)
            {
                int length = directory[offset];
                if (length == 0 || offset + length > UserDataSize)
                    break;

                int nameLength = directory[offset + 32];
                string name = Encoding.ASCII.GetString(directory, offset + 33, Math.Min(nameLength, length - 33));
                if (name.Equals("SYSTEM.CNF;1", StringComparison.OrdinalIgnoreCase) || name.Equals("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase))
                {
                    uint fileSector = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 2));
                    uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 10));
                    string? systemCnf = image.ReadFile(fileSector, (int)Math.Min(fileSize, MaxSystemCnfSize));
                    string? boot = systemCnf != null ? BootPathOf(systemCnf) : null;
                    return boot != null ? GameIdFromBootPath(boot) : null;
                }

                offset += length;
            }
        }

        return null;
    }

    /// <summary>"SLUS00748" → "SLUS-00748", the form SYSTEM.CNF gives.</summary>
    private static string? FromPbp(string? discId)
    {
        if (discId == null)
            return null;

        var match = PbpDiscId.Match(discId);
        return match.Success ? $"{match.Groups[1].Value}-{match.Groups[2].Value}" : discId;
    }

    private sealed class DiscImage
    {
        private readonly Stream _stream;
        private readonly int _sectorSize;
        private readonly int _dataOffset;

        private DiscImage(Stream stream, int sectorSize, int dataOffset)
        {
            _stream = stream;
            _sectorSize = sectorSize;
            _dataOffset = dataOffset;
        }

        /// <summary>Raw sectors start with the sync pattern, and the mode byte says where the data begins; a cooked image has the volume descriptor at 32 KB.</summary>
        public static DiscImage? Open(Stream stream, byte[] firstBytes)
        {
            if (firstBytes.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern))
            {
                // Mode 2 (every PlayStation disc) has an 8-byte subheader after the 16-byte header.
                int dataOffset = firstBytes[15] == 2 ? 24 : 16;
                return new DiscImage(stream, RawSectorSize, dataOffset);
            }

            var cooked = new DiscImage(stream, UserDataSize, 0);
            byte[]? volume = cooked.ReadSector(PrimaryVolumeSector);
            return volume != null && volume.AsSpan(1, 5).SequenceEqual("CD001"u8) ? cooked : null;
        }

        public byte[]? ReadSector(long sector)
        {
            long position = sector * _sectorSize + _dataOffset;
            if (position + UserDataSize > _stream.Length)
                return null;

            var data = new byte[UserDataSize];
            _stream.Position = position;
            return _stream.Read(data, 0, data.Length) == data.Length ? data : null;
        }

        public string? ReadFile(long sector, int size)
        {
            var content = new byte[size];
            for (int read = 0, i = 0; read < size; i++)
            {
                byte[]? data = ReadSector(sector + i);
                if (data == null)
                    return null;

                int count = Math.Min(UserDataSize, size - read);
                Array.Copy(data, 0, content, read, count);
                read += count;
            }

            return Encoding.ASCII.GetString(content);
        }
    }
}
