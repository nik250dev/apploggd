using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>
/// The PARAM.SFO of a PSP game image, read straight from the file. The format is told by its signature,
/// not its extension, because the path may be a /proc/&lt;pid&gt;/fd link. CHD (per-hunk codecs such as
/// zstd or LZMA, which .NET lacks) and the LZ4 flavours of CSO are not read.
/// </summary>
internal static class PspImageReader
{
    /// <summary>The images PPSSPP boots and keeps open while the game runs.</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".cso", ".chd", ".pbp", ".elf", ".prx", ".zip"
    };

    private const int SectorSize = 2048;
    private const int PrimaryVolumeSector = 16;
    private const int MaxParamSfoSize = 64 * 1024;

    // A ZIP can only be read forward; past this the PARAM.SFO is not worth the inflating.
    private const long MaxZipInflate = 512L * 1024 * 1024;

    private interface ISectorSource
    {
        byte[]? Read(long sector, int count);
    }

    /// <summary>Null when the format is not supported or holds no PARAM.SFO. Never throws.</summary>
    public static PspParamSfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) < 4)
                return null;
            stream.Position = 0;

            if (magic.AsSpan().SequenceEqual("\0PBP"u8))
                return ReadPbp(stream);

            if (magic.AsSpan().SequenceEqual("PK\x03\x04"u8))
                return ReadZip(stream);

            if (magic.AsSpan().SequenceEqual("CISO"u8))
                return CsoSource.Open(stream) is { } cso ? ReadIso(cso) : null;

            return ReadIso(new IsoSource(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PSN games and homebrew: the header lists eight sections, the first being the PARAM.SFO.</summary>
    private static PspParamSfo? ReadPbp(Stream stream)
    {
        var header = new byte[0x28];
        if (stream.Read(header, 0, header.Length) < header.Length)
            return null;

        uint start = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x08));
        uint end = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x0C));
        if (end <= start || end - start > MaxParamSfoSize)
            return null;

        var sfo = new byte[end - start];
        stream.Position = start;
        return stream.Read(sfo, 0, sfo.Length) == sfo.Length ? PspParamSfo.Parse(sfo) : null;
    }

    private static PspParamSfo? ReadZip(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return null;

        using var inner = entry.Open();
        return ReadIso(new ForwardSource(inner));
    }

    /// <summary>ISO 9660: the primary volume descriptor, then /PSP_GAME/PARAM.SFO.</summary>
    private static PspParamSfo? ReadIso(ISectorSource source)
    {
        var volume = source.Read(PrimaryVolumeSector, 1);
        if (volume == null || volume[0] != 1 || !volume.AsSpan(1, 5).SequenceEqual("CD001"u8))
            return null;

        var root = Record(volume, 156);
        if (root == null)
            return null;

        var game = Find(source, root.Value, "PSP_GAME", isDirectory: true);
        var sfo = game != null ? Find(source, game.Value, "PARAM.SFO", isDirectory: false) : null;
        if (sfo == null || sfo.Value.Size > MaxParamSfoSize)
            return null;

        var data = source.Read(sfo.Value.Sector, (int)((sfo.Value.Size + SectorSize - 1) / SectorSize));
        return data != null ? PspParamSfo.Parse(data.AsSpan(0, (int)sfo.Value.Size)) : null;
    }

    private readonly record struct DirectoryRecord(long Sector, long Size, bool IsDirectory, string Name);

    private static DirectoryRecord? Find(ISectorSource source, DirectoryRecord parent, string name, bool isDirectory)
    {
        int sectors = (int)Math.Min((parent.Size + SectorSize - 1) / SectorSize, 64);
        var data = source.Read(parent.Sector, sectors);
        if (data == null)
            return null;

        for (int sector = 0; sector < sectors; sector++)
        {
            int offset = sector * SectorSize;
            int end = offset + SectorSize;

            // Records never cross a sector; a zero length pads the rest of it.
            while (offset < end && data[offset] != 0)
            {
                var record = Record(data, offset);
                if (record == null)
                    break;

                if (record.Value.IsDirectory == isDirectory && record.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return record;

                offset += data[offset];
            }
        }

        return null;
    }

    private static DirectoryRecord? Record(byte[] data, int offset)
    {
        int length = data[offset];
        if (length < 34 || offset + length > data.Length)
            return null;

        int nameLength = data[offset + 32];
        if (33 + nameLength > length)
            return null;

        string name = Encoding.ASCII.GetString(data, offset + 33, nameLength);
        int version = name.IndexOf(';');
        if (version >= 0)
            name = name[..version];

        return new DirectoryRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 10)),
            (data[offset + 25] & 0x02) != 0,
            name);
    }

    private sealed class IsoSource : ISectorSource
    {
        private readonly Stream _stream;

        public IsoSource(Stream stream) => _stream = stream;

        public byte[]? Read(long sector, int count)
        {
            var buffer = new byte[count * SectorSize];
            _stream.Position = sector * SectorSize;
            return _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length ? buffer : null;
        }
    }

    /// <summary>
    /// CISO v0/v1: fixed-size blocks, each raw-deflated unless the top bit of its index entry says it is
    /// stored. Version 2 adds LZ4 blocks, which are not read.
    /// </summary>
    private sealed class CsoSource : ISectorSource
    {
        private const int HeaderSize = 0x18;

        private readonly Stream _stream;
        private readonly uint _blockSize;
        private readonly int _align;
        private readonly long _blocks;

        private CsoSource(Stream stream, uint blockSize, int align, long blocks)
        {
            _stream = stream;
            _blockSize = blockSize;
            _align = align;
            _blocks = blocks;
        }

        public static CsoSource? Open(Stream stream)
        {
            var header = new byte[HeaderSize];
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize)
                return null;

            ulong totalBytes = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            byte version = header[20];
            if (version > 1 || blockSize < SectorSize || blockSize % SectorSize != 0 || totalBytes == 0)
                return null;

            return new CsoSource(stream, blockSize, header[21], (long)((totalBytes + blockSize - 1) / blockSize));
        }

        public byte[]? Read(long sector, int count)
        {
            var result = new byte[count * SectorSize];
            long first = sector * SectorSize;

            for (int written = 0; written < result.Length;)
            {
                long position = first + written;
                long block = position / _blockSize;
                var data = ReadBlock(block);
                if (data == null)
                    return null;

                int inBlock = (int)(position % _blockSize);
                int take = Math.Min(data.Length - inBlock, result.Length - written);
                if (take <= 0)
                    return null;

                Buffer.BlockCopy(data, inBlock, result, written, take);
                written += take;
            }

            return result;
        }

        private byte[]? ReadBlock(long block)
        {
            if (block >= _blocks)
                return null;

            var index = new byte[8];
            _stream.Position = HeaderSize + block * 4;
            if (_stream.ReadAtLeast(index, 8, throwOnEndOfStream: false) < 8)
                return null;

            uint current = BinaryPrimitives.ReadUInt32LittleEndian(index);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(4));
            bool stored = (current & 0x80000000) != 0;
            long start = (long)(current & 0x7FFFFFFF) << _align;
            long end = (long)(next & 0x7FFFFFFF) << _align;
            if (end <= start)
                return null;

            var raw = new byte[end - start];
            _stream.Position = start;
            if (_stream.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) < raw.Length)
                return null;

            if (stored)
                return raw;

            var data = new byte[_blockSize];
            using var inflater = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
            int read = inflater.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            return read == data.Length ? data : null;
        }
    }

    /// <summary>A stream that can only go forward, such as a ZIP entry: earlier sectors are gone.</summary>
    private sealed class ForwardSource : ISectorSource
    {
        private readonly Stream _stream;
        private readonly byte[] _skip = new byte[64 * 1024];
        private long _position;

        public ForwardSource(Stream stream) => _stream = stream;

        public byte[]? Read(long sector, int count)
        {
            long target = sector * SectorSize;
            if (target < _position || target > MaxZipInflate)
                return null;

            while (_position < target)
            {
                int read = _stream.Read(_skip, 0, (int)Math.Min(_skip.Length, target - _position));
                if (read <= 0)
                    return null;
                _position += read;
            }

            var buffer = new byte[count * SectorSize];
            int got = _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            _position += got;
            return got == buffer.Length ? buffer : null;
        }
    }
}
