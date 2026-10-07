using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// The top-level windows of the X server, as the window manager lists them in _NET_CLIENT_LIST.
/// Under Wayland that is XWayland, which is where nearly every game runs: Proton, Unity and SDL 2
/// all default to X11. Native Wayland windows cannot be listed at all: Wayland gives no client a
/// view of another's windows.
///
/// Built on libxcb rather than Xlib because Xlib reports errors (a window closed mid-scan) through a
/// process-wide handler that Avalonia already owns, and whose default exits the process; xcb hands
/// each error back with its reply. The connection is this class's own, not Avalonia's.
/// </summary>
internal sealed class X11ClientWindows : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

    private const uint AtomAny = 0;
    private const uint AtomCardinal = 6;
    private const uint AtomWindow = 33;
    private const uint AtomWmName = 39;

    private IntPtr _connection;
    private uint _root;
    private DateTime _nextConnectAttempt;
    private uint _clientList, _wmPid, _netWmName, _utf8String, _wmState, _wmStateFullscreen;

    /// <summary>
    /// Every client window, with the process that owns it as the client declared it (0 when it did not),
    /// and whether it is fullscreen: as the window manager records it, or as large
    /// as the whole screen, since some games size a plain window to the screen instead of asking for it.
    /// Every request goes out before the first reply is read, so the pass waits on the X server once
    /// rather than twice per window.
    /// </summary>
    public List<(uint Id, int ProcessId, bool Fullscreen)> Windows()
    {
        var result = new List<(uint Id, int ProcessId, bool Fullscreen)>();
        if (!EnsureConnected())
            return result;

        uint[]? windows = ReadUInt32s(_root, _clientList, AtomWindow, 4096);
        if (windows == null)
            return result;

        uint screenCookie = xcb_get_geometry(_connection, _root);
        var pidCookies = new uint[windows.Length];
        var stateCookies = new uint[windows.Length];
        var sizeCookies = new uint[windows.Length];
        for (int i = 0; i < windows.Length; i++)
        {
            pidCookies[i] = xcb_get_property(_connection, 0, windows[i], _wmPid, AtomCardinal, 0, 1);
            stateCookies[i] = xcb_get_property(_connection, 0, windows[i], _wmState, AtomAny, 0, 64);
            sizeCookies[i] = xcb_get_geometry(_connection, windows[i]);
        }

        var screen = GeometryReply(screenCookie);
        for (int i = 0; i < windows.Length; i++)
        {
            // Every reply is read, even when the first answers the question, so none is left queued.
            uint[]? pid = ToUInt32s(PropertyReply(pidCookies[i]).Value);
            uint[]? states = ToUInt32s(PropertyReply(stateCookies[i]).Value);
            var size = GeometryReply(sizeCookies[i]);

            bool fullscreen = (states != null && Array.IndexOf(states, _wmStateFullscreen) >= 0)
                || (size != null && screen != null && size.Value.Width >= screen.Value.Width && size.Value.Height >= screen.Value.Height);
            result.Add((windows[i], pid is { Length: > 0 } ? (int)pid[0] : 0, fullscreen));
        }

        return result;
    }

    public string Title(uint window)
    {
        var (type, value) = ReadProperty(window, _netWmName, _utf8String, 1024);
        if (value is { Length: > 0 })
            return Encoding.UTF8.GetString(value);

        (type, value) = ReadProperty(window, AtomWmName, AtomAny, 1024);
        if (value is not { Length: > 0 })
            return string.Empty;

        return type == _utf8String ? Encoding.UTF8.GetString(value) : Encoding.Latin1.GetString(value);
    }

    public void Dispose() => Disconnect();

    private bool EnsureConnected()
    {
        if (_connection != IntPtr.Zero)
        {
            if (xcb_connection_has_error(_connection) == 0)
                return true;

            // The X server went away (XWayland restarts with the session); try a new one later.
            Disconnect();
        }

        if (DateTime.UtcNow < _nextConnectAttempt || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return false;

        _nextConnectAttempt = DateTime.UtcNow + ReconnectDelay;

        try
        {
            // Never null: a failed connection is an object in an error state, which must still be freed.
            _connection = xcb_connect(null, out int screenNumber);
            if (xcb_connection_has_error(_connection) != 0)
            {
                Disconnect();
                return false;
            }

            var screens = xcb_setup_roots_iterator(xcb_get_setup(_connection));
            for (int i = 0; i < screenNumber && screens.Rem > 0; i++)
                xcb_screen_next(ref screens);

            if (screens.Data == IntPtr.Zero)
            {
                Disconnect();
                return false;
            }

            // xcb_screen_t starts with the root window's id.
            _root = (uint)Marshal.ReadInt32(screens.Data);
            _clientList = InternAtom("_NET_CLIENT_LIST");
            _wmPid = InternAtom("_NET_WM_PID");
            _netWmName = InternAtom("_NET_WM_NAME");
            _utf8String = InternAtom("UTF8_STRING");
            _wmState = InternAtom("_NET_WM_STATE");
            _wmStateFullscreen = InternAtom("_NET_WM_STATE_FULLSCREEN");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libxcb: there is no X server to list either. Not retried for the rest of the run.
            _nextConnectAttempt = DateTime.MaxValue;
            Console.WriteLine($"[X11ClientWindows] libxcb is not available: {ex.Message}");
            return false;
        }
    }

    private void Disconnect()
    {
        if (_connection != IntPtr.Zero)
        {
            xcb_disconnect(_connection);
            _connection = IntPtr.Zero;
        }
    }

    private uint InternAtom(string name)
    {
        IntPtr reply = xcb_intern_atom_reply(_connection, xcb_intern_atom(_connection, 0, (ushort)name.Length, name), out IntPtr error);
        FreeIfSet(error);
        if (reply == IntPtr.Zero)
            return 0;

        // xcb_intern_atom_reply_t: type, pad, sequence, length, then the atom at byte 8.
        uint atom = (uint)Marshal.ReadInt32(reply, 8);
        Marshal.FreeHGlobal(reply);
        return atom;
    }

    private uint[]? ReadUInt32s(uint window, uint property, uint type, uint maxCount) =>
        ToUInt32s(ReadProperty(window, property, type, maxCount).Value);

    private static uint[]? ToUInt32s(byte[]? value)
    {
        if (value == null)
            return null;

        var values = new uint[value.Length / 4];
        Buffer.BlockCopy(value, 0, values, 0, values.Length * 4);
        return values;
    }

    /// <param name="maxLength">In 32-bit units, as X counts property lengths.</param>
    private (uint Type, byte[]? Value) ReadProperty(uint window, uint property, uint type, uint maxLength) =>
        PropertyReply(xcb_get_property(_connection, 0, window, property, type, 0, maxLength));

    private (uint Type, byte[]? Value) PropertyReply(uint cookie)
    {
        IntPtr reply = xcb_get_property_reply(_connection, cookie, out IntPtr error);
        FreeIfSet(error);
        if (reply == IntPtr.Zero)
            return (0, null);

        try
        {
            // xcb_get_property_reply_t: type, format, sequence, length, then the property's type at byte 8.
            uint actualType = (uint)Marshal.ReadInt32(reply, 8);
            int length = xcb_get_property_value_length(reply);
            if (length <= 0)
                return (actualType, null);

            var value = new byte[length];
            Marshal.Copy(xcb_get_property_value(reply), value, 0, length);
            return (actualType, value);
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
        }
    }

    private (int Width, int Height)? GeometryReply(uint cookie)
    {
        IntPtr reply = xcb_get_geometry_reply(_connection, cookie, out IntPtr error);
        FreeIfSet(error);
        if (reply == IntPtr.Zero)
            return null;

        // xcb_get_geometry_reply_t: ..., root (8), x (12), y (14), width (16), height (18).
        int width = (ushort)Marshal.ReadInt16(reply, 16);
        int height = (ushort)Marshal.ReadInt16(reply, 18);
        Marshal.FreeHGlobal(reply);
        return (width, height);
    }

    // On Linux FreeHGlobal is libc's free, which is what xcb's replies and errors need.
    private static void FreeIfSet(IntPtr error)
    {
        // A window that closed mid-scan answers BadWindow here; the caller just sees no reply.
        if (error != IntPtr.Zero)
            Marshal.FreeHGlobal(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator
    {
        public IntPtr Data;
        public int Rem;
        public int Index;
    }

    private const string LibXcb = "libxcb.so.1";

    // xcb's cookies are structs holding a single uint, which the ABI passes and returns as a uint.
    [DllImport(LibXcb)]
    private static extern IntPtr xcb_connect(string? displayName, out int screen);

    [DllImport(LibXcb)]
    private static extern int xcb_connection_has_error(IntPtr connection);

    [DllImport(LibXcb)]
    private static extern void xcb_disconnect(IntPtr connection);

    [DllImport(LibXcb)]
    private static extern IntPtr xcb_get_setup(IntPtr connection);

    [DllImport(LibXcb)]
    private static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);

    [DllImport(LibXcb)]
    private static extern void xcb_screen_next(ref ScreenIterator iterator);

    [DllImport(LibXcb)]
    private static extern uint xcb_intern_atom(IntPtr connection, byte onlyIfExists, ushort nameLength, string name);

    [DllImport(LibXcb)]
    private static extern IntPtr xcb_intern_atom_reply(IntPtr connection, uint cookie, out IntPtr error);

    [DllImport(LibXcb)]
    private static extern uint xcb_get_property(IntPtr connection, byte delete, uint window, uint property, uint type, uint longOffset, uint longLength);

    [DllImport(LibXcb)]
    private static extern IntPtr xcb_get_property_reply(IntPtr connection, uint cookie, out IntPtr error);

    [DllImport(LibXcb)]
    private static extern IntPtr xcb_get_property_value(IntPtr reply);

    [DllImport(LibXcb)]
    private static extern int xcb_get_property_value_length(IntPtr reply);

    [DllImport(LibXcb)]
    private static extern uint xcb_get_geometry(IntPtr connection, uint drawable);

    [DllImport(LibXcb)]
    private static extern IntPtr xcb_get_geometry_reply(IntPtr connection, uint cookie, out IntPtr error);
}
