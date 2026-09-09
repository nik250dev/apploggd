using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BackloggdMirror.Services;
using System;

namespace BackloggdMirror.Views;

/// <summary>
/// The "still running in the tray" notice shown when the main window is closed with minimize-to-tray
/// on. A custom window rather than an OS notification, so it matches the app's styling and needs no
/// notification permissions.
/// </summary>
public partial class TrayNotificationWindow : Window
{
    private DispatcherTimer? _closeTimer;
    private readonly IAppLogger? _logger;
    private readonly TimeSpan _duration;
    private readonly Action? _action;

    /// <summary>Height with an action button; the XAML value is the one for a plain notice.</summary>
    private const double HeightWithAction = 112;

    /// <summary>
    /// Kept genuinely parameterless (rather than folded into the overload below with a default
    /// argument) because Avalonia's runtime XAML loader only accepts a public parameterless
    /// constructor.
    /// </summary>
    public TrayNotificationWindow() : this(null)
    {
    }

    /// <summary>
    /// Defaults to the "still running in the tray" notice. Pass <paramref name="message"/> and an
    /// action to reuse the same window, placement and fade for a different notice.
    /// </summary>
    public TrayNotificationWindow(IAppLogger? logger, string? message = null, TimeSpan? duration = null,
                                  string? actionText = null, Action? action = null)
    {
        _logger = logger;
        _duration = duration ?? TimeSpan.FromSeconds(4);
        _action = action;

        InitializeComponent();

        if (message != null)
        {
            MessageText.Text = message;
        }

        if (action != null)
        {
            ActionButton.Content = actionText ?? string.Empty;
            ActionButton.IsVisible = true;

            // Set before positioning: the placement is computed from Height.
            Height = HeightWithAction;
        }

        PositionWindow();
    }

    /// <summary>
    /// Pins the window to the bottom-right of the primary screen's working area, next to the tray
    /// icon it refers to. Positions are physical pixels while Width/Height are logical, so the
    /// scaling factor has to be applied by hand or the placement drifts on high-DPI displays.
    /// </summary>
    private void PositionWindow()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen != null)
            {
                double scaling = screen.Scaling;
                var workingArea = screen.WorkingArea;

                int windowWidthPhysical = (int)(Width * scaling);
                int windowHeightPhysical = (int)(Height * scaling);

                int margin = (int)(1 * scaling);

                int x = workingArea.X + workingArea.Width - windowWidthPhysical - margin;
                int y = workingArea.Y + workingArea.Height - windowHeightPhysical - margin;

                Position = new PixelPoint(x, y);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error positioning notification window: {ex.Message}");
            _logger?.Warning($"[TrayNotificationWindow] Could not position the notice next to the tray icon: {ex.Message}. It appears wherever the window manager puts it.");
        }
    }

    private DateTime _startTime;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Classes.Add("visible");
        _startTime = DateTime.Now;

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _closeTimer.Tick += OnTimerTick;
        _closeTimer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _startTime;
        var remaining = _duration - elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            _closeTimer?.Stop();
            NotificationProgress.Value = 0;
            StartCloseAnimation();
        }
        else
        {
            NotificationProgress.Value = (remaining.TotalMilliseconds / _duration.TotalMilliseconds) * 100;
        }
    }

    /// <summary>
    /// Drops the "visible" class to trigger the fade-out defined in XAML, then closes once it has
    /// played. The delay must stay in step with that animation's duration — closing sooner cuts it
    /// short and the window vanishes instead of fading.
    /// </summary>
    private void StartCloseAnimation()
    {
        Classes.Remove("visible");

        var closeTimer2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        closeTimer2.Tick += (s, ev) =>
        {
            closeTimer2.Stop();
            Close();
        };
        closeTimer2.Start();
    }

    public void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _closeTimer?.Stop();
        StartCloseAnimation();
    }

    /// <summary>Runs the action and closes: leaving the notice counting down after a click reads as a no-op.</summary>
    public void ActionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _action?.Invoke();
        CloseButton_Click(sender, e);
    }
}
