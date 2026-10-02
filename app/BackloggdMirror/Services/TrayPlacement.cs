using Avalonia;
using System;
using System.Linq;

namespace BackloggdMirror.Services;

public enum TrayEdge { Bottom, Top, Left, Right }

/// <summary>Screen edge of the panel holding the tray, and whether it was measured or assumed.</summary>
public readonly record struct TrayAnchor(TrayEdge Edge, bool IsMeasured)
{
    // Every common desktop keeps the tray at the far end of a horizontal panel and at the bottom of a vertical one.
    public bool AtRight => Edge != TrayEdge.Left;
    public bool AtBottom => Edge != TrayEdge.Top;
}

/// <summary>
/// Works out which edge the tray is on from the space panels take off the screen (Bounds minus
/// WorkingArea), so notices can appear next to it on any desktop layout.
/// </summary>
public static class TrayPlacement
{
    public static TrayAnchor Detect(PixelRect bounds, PixelRect workingArea) =>
        Detect(bounds, workingArea, Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));

    internal static TrayAnchor Detect(PixelRect bounds, PixelRect workingArea, string? desktop)
    {
        var insets = new[]
        {
            (Edge: TrayEdge.Top, Size: workingArea.Y - bounds.Y),
            (Edge: TrayEdge.Bottom, Size: bounds.Bottom - workingArea.Bottom),
            (Edge: TrayEdge.Left, Size: workingArea.X - bounds.X),
            (Edge: TrayEdge.Right, Size: bounds.Right - workingArea.Right),
        }.Where(i => i.Size > 0).ToArray();

        if (insets.Length == 1) return new TrayAnchor(insets[0].Edge, true);

        if (insets.Length > 1)
        {
            // A top bar next to a dock (Ubuntu, macOS-like layouts): the tray lives in the bar, never in the dock.
            if (!OperatingSystem.IsWindows() && insets.Any(i => i.Edge == TrayEdge.Top))
            {
                return new TrayAnchor(TrayEdge.Top, true);
            }

            return new TrayAnchor(insets.MaxBy(i => i.Size).Edge, true);
        }

        // Nothing reserved: an auto-hiding panel, or a desktop that does not publish its work area.
        return new TrayAnchor(DefaultEdge(desktop), false);
    }

    private static TrayEdge DefaultEdge(string? desktop)
    {
        if (OperatingSystem.IsWindows()) return TrayEdge.Bottom;
        if (OperatingSystem.IsMacOS()) return TrayEdge.Top;

        // XDG_CURRENT_DESKTOP is a colon-separated list, e.g. "ubuntu:GNOME" or "X-Cinnamon".
        var names = (desktop ?? string.Empty).ToUpperInvariant().Split(':');
        string[] bottomPanelDesktops = { "KDE", "X-CINNAMON", "CINNAMON", "LXQT", "LXDE", "DEEPIN", "DDE" };

        return names.Any(n => bottomPanelDesktops.Contains(n)) ? TrayEdge.Bottom : TrayEdge.Top;
    }
}
