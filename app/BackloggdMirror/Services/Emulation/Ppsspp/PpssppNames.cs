using System;
using System.IO;

namespace BackloggdMirror.Services.Emulation.Ppsspp;

/// <summary>Search names for a PSP game from its PARAM.SFO title and its image file, shared by every platform's PPSSPP detector.</summary>
internal static class PpssppNames
{
    /// <param name="fallback">Cleaned as a file name when neither the title nor the image names anything.</param>
    public static RomNames For(string? title, string? imagePath, string fallback)
    {
        // Installed PSN games and homebrew are all EBOOT.PBP, which names nothing.
        if (imagePath != null && Path.GetFileNameWithoutExtension(imagePath).Equals("EBOOT", StringComparison.OrdinalIgnoreCase))
            imagePath = null;

        // Japanese discs keep their title in Japanese, which neither IGDB nor the search API know; dumps are named in romaji.
        var names = title != null && !RomNameCleaner.IsLatinScript(title)
            ? RomNameCleaner.Clean(string.Empty, null, imagePath != null ? Path.GetFileNameWithoutExtension(imagePath) : null, title)
            : RomNameCleaner.Clean(imagePath ?? string.Empty, null, title);

        return names.Names.Count > 0 ? names : RomNameCleaner.Clean(fallback, null);
    }
}
