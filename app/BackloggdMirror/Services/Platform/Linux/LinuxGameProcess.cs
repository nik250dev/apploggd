using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// Whether a process with a window looks like a game, from the libraries it has loaded
/// (/proc/&lt;pid&gt;/maps) and the files its executable ships with.
///
/// First, <em>game engines</em>: Unity, Unreal, GameMaker, Godot, Source... are used for practically
/// nothing but games, so one is enough on its own, even for a window that is not fullscreen. Each is
/// recognised by a library it loads or a file it leaves next to the executable; the files matter under
/// Wine, which does not map most of a game's DLLs (a GameMaker game shows only DXVK's).
///
/// Otherwise, for fullscreen windows only: it draws with a 3D API (OpenGL, EGL or Vulkan), which rules
/// out ordinary apps, and it is not built on a desktop toolkit (GTK, Qt Widgets) or a web engine, which
/// is what the apps that also draw in 3D are made of. A game library (SDL, GLFW, the Steam API...)
/// overrides the toolkit, since some games pull GTK in for their dialogs. None of that is required: a
/// statically linked game leaves no trace of its own (Factorio shows only libGL).
/// Programs that fit the rule without being games (mpv, a browser's own binary) are excluded by name.
/// </summary>
internal static class LinuxGameProcess
{
    private static readonly string[] GraphicsLibraries = { "libGL.so", "libGLX.so", "libEGL.so", "libOpenGL.so", "libGLESv2.so", "libvulkan.so" };

    /// <summary>Library name prefixes, native or loaded by Wine for a Windows build.</summary>
    private static readonly (string Prefix, string Engine)[] EngineLibraries =
    {
        ("UnityPlayer.so", "Unity"), ("UnityPlayer.dll", "Unity"),
        ("libUE4", "Unreal"), ("libUnreal", "Unreal"),
        ("libengine2", "Source 2"), ("engine2.dll", "Source 2"), ("libtier0", "Source"), ("tier0.dll", "Source"),
        ("librenpython", "Ren'Py"), ("liblove", "LÖVE"), ("love.dll", "LÖVE"),
        ("libFNA3D", "FNA"), ("FNA3D.dll", "FNA"), ("CrySystem.dll", "CryEngine"),
        ("liblwjgl", "LWJGL"), ("libhl.so", "HashLink"), ("libcocos2d", "Cocos2d-x")
    };

    /// <summary>Files or folders next to the executable, by the name the engine always gives them.</summary>
    private static readonly (string Path, string Engine)[] EngineFiles =
    {
        ("UnityPlayer.dll", "Unity"), ("UnityPlayer.so", "Unity"),
        ("data.win", "GameMaker"), ("game.unx", "GameMaker"), ("assets/game.unx", "GameMaker"),
        ("renpy", "Ren'Py"),
        ("engine2.dll", "Source 2"), ("libengine2.so", "Source 2"),
        ("bin/tier0.dll", "Source"), ("bin/libtier0.so", "Source"), ("tier0.dll", "Source"), ("libtier0.so", "Source"),
        ("Game.rgss3a", "RPG Maker"), ("Game.rgss2a", "RPG Maker"), ("Game.rgssad", "RPG Maker"),
        ("www/js/rpg_core.js", "RPG Maker MV"), ("js/rmmz_core.js", "RPG Maker MZ"),
        ("love.dll", "LÖVE"), ("FNA.dll", "FNA"), ("MonoGame.Framework.dll", "MonoGame"), ("CrySystem.dll", "CryEngine"),
        ("game.projectc", "Defold"), ("data.xp3", "Kirikiri"), ("Data.wolf", "WOLF RPG Editor"),
        ("acsetup.cfg", "Adventure Game Studio"), ("hlboot.dat", "HashLink"), ("libcocos2d.dll", "Cocos2d-x")
    };

    // Unreal links a game into one executable named "<Project>-<Platform>-Shipping".
    private static readonly Regex UnrealShippingName = new(@"-(Linux|LinuxArm64|Win64|Win32)-Shipping$", RegexOptions.IgnoreCase);

    // Libraries that games use but ordinary apps rarely do. Not enough on their own.
    private static readonly string[] GameLibraries =
    {
        "libgodot", "libSDL2", "libSDL3", "libSDL-1.2", "libglfw", "libmonobdwgc", "libsteam_api.so", "libEOSSDK",
        "discord_game_sdk.so", "libGalaxy", "libfmod", "libopenal", "libbgfx", "libraylib", "libsfml-graphics", "liballegro"
    };

    private static readonly string[] ToolkitLibraries = { "libgtk-3.so", "libgtk-4.so", "libgtk-x11-2.0.so", "libQt5Widgets.so", "libQt6Widgets.so", "libQt5Quick.so", "libQt6Quick.so", "libwx_gtk" };

    // Chromium-based (Electron, CEF, NW.js), Firefox and Qt WebEngine.
    private static readonly string[] WebEngineLibraries = { "libcef.so", "libnode.so", "libffmpeg.so", "libxul.so", "libQt5WebEngineCore.so", "libQt6WebEngineCore.so", "libwebkit2gtk", "libwebkitgtk" };

    /// <summary>
    /// Executable names, lowercased and without extension. Browsers and media players, which pass the
    /// library rule; stores and launchers, which are not the game they start; engine editors; the
    /// emulators that Windows handles in its own tier, whose menu would pass too; and Wine's own programs.
    /// </summary>
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "firefox-bin", "firefox-esr", "librewolf", "chrome", "google-chrome", "chromium", "chromium-browser",
        "brave", "vivaldi-bin", "opera", "msedge", "epiphany",
        "steam", "steamwebhelper", "lutris", "heroic", "itch", "bottles", "minigalaxy",
        "vlc", "mpv", "mplayer", "ffplay", "totem", "celluloid", "smplayer", "haruna", "kodi", "kodi.bin", "obs",
        "discord", "slack", "teams", "zoom", "telegram-desktop", "signal-desktop", "soffice", "soffice.bin", "blender",
        "code", "godot", "unityhub", "unity", "unrealeditor", "ue4editor", "hammer", "hammerplusplus",
        "retroarch", "dolphin-emu", "cemu",
        "wine", "wine64", "wine-preloader", "wine64-preloader", "explorer", "services", "winedevice", "plugplay",
        "svchost", "rpcss", "conhost", "start", "xalia", "rundll32", "winecfg", "control", "tabtip"
    };

    /// <param name="IsGame">Whether the process looks like a game at all.</param>
    /// <param name="Engine">The game engine it runs on, which a game that is not fullscreen needs.</param>
    /// <param name="Reason">For the log: what decided it.</param>
    public readonly record struct Verdict(bool IsGame, string? Engine, string Reason);

    /// <param name="executablePath">As <see cref="LinuxProcFs.TryGetImagePath"/> gives it.</param>
    public static Verdict Classify(int pid, string? executablePath)
    {
        string name = executablePath == null ? string.Empty : Path.GetFileNameWithoutExtension(LinuxProcFs.FileNameOf(executablePath));
        if (ExcludedNames.Contains(name))
            return new Verdict(false, null, "excluded by name");

        var mapped = LinuxProcFs.ReadMappedFileNames(pid);
        if (mapped == null)
            return new Verdict(false, null, "libraries unreadable");

        // Before the web engine: RPG Maker MV and MZ games are NW.js apps.
        string? engine = EngineOf(mapped, executablePath, name);
        if (engine != null)
            return new Verdict(true, engine, $"{engine} engine");

        string? web = FindAny(mapped, WebEngineLibraries);
        if (web != null)
            return new Verdict(false, null, $"web engine {web}");

        string? graphics = FindAny(mapped, GraphicsLibraries);
        if (graphics == null)
            return new Verdict(false, null, "no 3D API");

        string? library = FindAny(mapped, GameLibraries);
        if (library != null)
            return new Verdict(true, null, $"{graphics} + {library}");

        string? toolkit = FindAny(mapped, ToolkitLibraries);
        if (toolkit != null)
            return new Verdict(false, null, $"desktop toolkit {toolkit}");

        return new Verdict(true, null, $"{graphics}, no desktop toolkit");
    }

    private static string? EngineOf(HashSet<string> mapped, string? executablePath, string name)
    {
        foreach (var (prefix, engine) in EngineLibraries)
        {
            if (mapped.Any(file => file.StartsWith(prefix, StringComparison.Ordinal)))
                return engine;
        }

        if (UnrealShippingName.IsMatch(name))
            return "Unreal";

        // A Windows path Wine could not translate has no folder to look in.
        string? folder = executablePath != null && executablePath.StartsWith('/') ? Path.GetDirectoryName(executablePath) : null;
        if (folder == null)
            return null;

        // Godot exports the game's data as "<executable>.pck" unless it is embedded.
        if (File.Exists(Path.Combine(folder, name + ".pck")))
            return "Godot";

        foreach (var (file, engine) in EngineFiles)
        {
            string candidate = Path.Combine(folder, file);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return engine;
        }

        return null;
    }

    private static string? FindAny(HashSet<string> mapped, string[] prefixes) =>
        mapped.FirstOrDefault(name => prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
}
