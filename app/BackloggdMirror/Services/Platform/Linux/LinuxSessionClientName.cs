using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BackloggdMirror.Services.Platform.Linux;

/// <summary>
/// Names the app to the X session manager. Avalonia registers with it (to hear about logouts and
/// shutdowns) but never sets the SmProgram property, so KDE and GNOME show an empty name in their
/// logout messages ("Logout canceled by ''").
///
/// Avalonia keeps its connection to itself, so it is read by reflection; if a future Avalonia
/// moves it, the name is simply left empty again.
/// </summary>
internal static class LinuxSessionClientName
{
    private const string LogTag = "[SessionClientName]";
    private const string Name = "Apploggd";

    // From X11/SM/SM.h.
    private const string SmProgram = "Program";
    private const string SmArray8 = "ARRAY8";

    [StructLayout(LayoutKind.Sequential)]
    private struct SmPropValue
    {
        public int Length;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmProp
    {
        public IntPtr Name;
        public IntPtr Type;
        public int NumVals;
        public IntPtr Vals;
    }

    [DllImport("libSM.so.6")]
    private static extern void SmcSetProperties(IntPtr smcConn, int numProps, IntPtr props);

    /// <summary>Must run after Avalonia's platform setup, which is when it opens the connection.</summary>
    public static void Apply(IAppLogger logger)
    {
        IntPtr name = IntPtr.Zero, type = IntPtr.Zero, value = IntPtr.Zero, vals = IntPtr.Zero, prop = IntPtr.Zero, props = IntPtr.Zero;
        try
        {
            IntPtr smcConn = FindAvaloniaConnection();
            if (smcConn == IntPtr.Zero)
            {
                logger.Info($"{LogTag} No session manager connection; nothing to name.");
                return;
            }

            byte[] nameBytes = Encoding.ASCII.GetBytes(Name);
            name = Marshal.StringToHGlobalAnsi(SmProgram);
            type = Marshal.StringToHGlobalAnsi(SmArray8);
            value = Marshal.AllocHGlobal(nameBytes.Length);
            Marshal.Copy(nameBytes, 0, value, nameBytes.Length);

            vals = Marshal.AllocHGlobal(Marshal.SizeOf<SmPropValue>());
            Marshal.StructureToPtr(new SmPropValue { Length = nameBytes.Length, Value = value }, vals, false);

            prop = Marshal.AllocHGlobal(Marshal.SizeOf<SmProp>());
            Marshal.StructureToPtr(new SmProp { Name = name, Type = type, NumVals = 1, Vals = vals }, prop, false);

            props = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(props, prop);

            // libSM writes the property out and flushes before returning, so the buffers can go.
            SmcSetProperties(smcConn, 1, props);

            logger.Info($"{LogTag} Registered as '{Name}' with the session manager.");
        }
        catch (Exception ex)
        {
            logger.Warning($"{LogTag} Could not name the app to the session manager: {ex.Message}. Logout messages show an empty name.");
        }
        finally
        {
            Marshal.FreeHGlobal(props);
            Marshal.FreeHGlobal(prop);
            Marshal.FreeHGlobal(vals);
            Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(type);
            Marshal.FreeHGlobal(name);
        }
    }

    private static IntPtr FindAvaloniaConnection()
    {
        // Keyed by the SmcConn of every connection Avalonia.X11 has open; there is one at most.
        var mapper = typeof(Avalonia.X11PlatformOptions).Assembly
            .GetType("Avalonia.X11.X11PlatformLifetimeEvents")
            ?.GetField("s_nativeToManagedMapper", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as IDictionary;

        if (mapper == null)
            throw new InvalidOperationException("Avalonia's session manager connection was not found where expected");

        return mapper.Keys.OfType<IntPtr>().FirstOrDefault();
    }
}
