using System;
using System.Runtime.InteropServices;

namespace BackloggdMirror.Services.Input;

/// <summary>The few SDL2 calls the controller support needs; natives from Ultz.Native.SDL.</summary>
internal static class Sdl2
{
    private const string Lib = "SDL2";

    public const uint InitGameController = 0x00002000;

    public const uint EventControllerButtonDown = 0x651;
    public const uint EventControllerDeviceAdded = 0x653;
    public const uint EventControllerDeviceRemoved = 0x654;

    public const int ButtonA = 0;
    public const int ButtonB = 1;
    public const int ButtonY = 3;
    public const int ButtonDpadUp = 11;
    public const int ButtonDpadDown = 12;
    public const int ButtonDpadLeft = 13;
    public const int ButtonDpadRight = 14;

    public const int AxisLeftX = 0;
    public const int AxisLeftY = 1;

    public const int TypePs3 = 3;
    public const int TypePs4 = 4;
    public const int TypeSwitchPro = 5;
    public const int TypePs5 = 7;
    public const int TypeJoyConLeft = 11;
    public const int TypeJoyConPair = 13;

    private static bool _resolverRegistered;

    /// <summary>Must run before the first call into SDL. A resolver can only be set once per assembly.</summary>
    public static void RegisterResolver()
    {
        if (_resolverRegistered) return;
        _resolverRegistered = true;
        NativeLibrary.SetDllImportResolver(typeof(Sdl2).Assembly, Resolve);
    }

    // Default probing never tries the packaged libSDL2-2.0.so; the system's .so.0 is the fallback.
    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib || !OperatingSystem.IsLinux()) return IntPtr.Zero;

        foreach (var candidate in new[] { "libSDL2-2.0.so", "libSDL2-2.0.so.0", "libSDL2.so" })
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>SDL_Event is a 56-byte union; only the fields shared by the controller events are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Which;
        [FieldOffset(12)] public byte Button;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Quit();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_GetVersion(out Version version);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetError();

    public static string GetError() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? string.Empty;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_PollEvent(out Event e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_IsGameController(int deviceIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GameControllerOpen(int deviceIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_GameControllerClose(IntPtr controller);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GameControllerGetJoystick(IntPtr controller);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_JoystickInstanceID(IntPtr joystick);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GameControllerName(IntPtr controller);

    public static string GetName(IntPtr controller) => Marshal.PtrToStringUTF8(SDL_GameControllerName(controller)) ?? "?";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GameControllerGetType(IntPtr controller);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte SDL_GameControllerGetButton(IntPtr controller, int button);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern short SDL_GameControllerGetAxis(IntPtr controller, int axis);
}
