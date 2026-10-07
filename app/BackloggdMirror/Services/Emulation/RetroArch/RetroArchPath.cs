namespace BackloggdMirror.Services.Emulation.RetroArch;

/// <summary>
/// Content paths reach the app from two places — the process memory and the playlists — and the path
/// doubles as the session key, so both sides are normalized here to compare as the same string.
/// </summary>
internal static class RetroArchPath
{
    public static string Normalize(string path)
    {
        return path.Replace('/', '\\').Trim().TrimEnd('\'');
    }
}
