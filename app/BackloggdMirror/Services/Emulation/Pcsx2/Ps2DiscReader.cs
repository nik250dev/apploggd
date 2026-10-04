using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BackloggdMirror.Services.Emulation.DuckStation;

namespace BackloggdMirror.Services.Emulation.Pcsx2;

/// <summary>
/// A disc image PCSX2 runs. <paramref name="BootElf"/> is null for images that cannot be read (CHD, CSO, ZSO, gzip)
/// and for discs without a boot line; <paramref name="Serial"/> is empty when the boot file is not named like one.
/// </summary>
internal sealed record Ps2Disc(string? Serial, string? BootElf)
{
    /// <summary>PCSX2 also boots PlayStation discs, whose boot file is on "cdrom:" instead of "cdrom0:".</summary>
    public bool IsPlayStation => BootElf != null && BootElf.StartsWith("cdrom:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The serial PCSX2 gives a disc, worked out the way it does (GetPS2ElfName and ExecutablePathToSerial): the
/// BOOT2 line of SYSTEM.CNF (BOOT on PlayStation discs), "cdrom0:\SLUS_216.78;1" becoming "SLUS-21678". The
/// format is told by its contents: the path may be a /proc/&lt;pid&gt;/fd link, and PCSX2's shader cache is a .bin too.
/// </summary>
internal static class Ps2DiscReader
{
    // "????_???.??*" or "????-???.??*", PCSX2's own check before taking a boot file name as a serial.
    private static readonly Regex SerialLike = new(@"^.{4}[_\-].{3}\..{2}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Null when the file is no disc image. Never throws.</summary>
    public static Ps2Disc? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var magic = new byte[16];
            if (stream.Read(magic, 0, magic.Length) < magic.Length)
                return null;

            if (IsCompressed(magic))
                return new Ps2Disc(null, null);

            if (!PsxDiscReader.TryReadSystemCnf(stream, magic, out string? systemCnf))
                return null;

            string? bootElf = systemCnf != null ? BootElfOf(systemCnf) : null;
            return new Ps2Disc(bootElf != null ? SerialOf(bootElf) : null, bootElf);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCompressed(byte[] magic) =>
        magic.AsSpan(0, 8).SequenceEqual("MComprHD"u8)
        || magic.AsSpan(0, 4).SequenceEqual("CISO"u8)
        || magic.AsSpan(0, 4).SequenceEqual("ZISO"u8)
        || (magic[0] == 0x1F && magic[1] == 0x8B);

    /// <summary>The value of the last BOOT2 or BOOT line, each line split at its first '=' and trimmed, as PCSX2 parses it.</summary>
    internal static string? BootElfOf(string systemCnf)
    {
        string? bootElf = null;

        foreach (string line in systemCnf.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator < 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (value.Length > 0 && (key == "BOOT2" || key == "BOOT"))
                bootElf = value;
        }

        return bootElf;
    }

    /// <summary>"cdrom0:\SLUS_216.78;1" → "SLUS-21678"; empty when the file is not named like a serial.</summary>
    internal static string SerialOf(string bootElf)
    {
        int folder = bootElf.LastIndexOf('\\');
        if (folder < 0)
            folder = bootElf.LastIndexOf(':');

        string serial = folder >= 0 ? bootElf[(folder + 1)..] : bootElf;

        int version = serial.LastIndexOf(';');
        if (version >= 0)
            serial = serial[..version];

        if (!SerialLike.IsMatch(serial))
            return string.Empty;

        var id = new StringBuilder(serial.Length);
        foreach (char c in serial)
        {
            if (c == '.')
                continue;

            id.Append(c == '_' ? '-' : char.ToUpperInvariant(c));
        }

        return id.ToString();
    }
}
