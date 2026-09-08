using Avalonia.Controls;
using System;

namespace BackloggdMirror.Views;

/// <summary>
/// Progress of an in-app update. A window of its own rather than an overlay in MainWindow: on a
/// silent start the update can be launched from the tray notice, with no main window on screen.
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
