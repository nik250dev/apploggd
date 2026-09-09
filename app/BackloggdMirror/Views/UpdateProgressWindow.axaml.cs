using Avalonia.Controls;
using System;

namespace BackloggdMirror.Views;

/// <summary>
/// Progress of an in-app update. A window of its own rather than an overlay in MainWindow: it is
/// topmost and chromeless, and the update can be started from the tray with no window on screen.
/// The command surfaces MainWindow behind it first, so this never appears over a bare desktop.
/// Deliberately has no close button — the process is replaced when it finishes.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Same fade-in as TrayNotificationWindow; the class is what the XAML transition keys off.
        Classes.Add("visible");
    }
}
