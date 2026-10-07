using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BackloggdMirror.Models;
using BackloggdMirror.Services;
using System;

namespace BackloggdMirror.Views;

/// <summary>
/// Notices shown next to the tray while the main window is hidden: what the app is doing in the
/// background, and new versions on a silent start. A custom window rather than an OS notification,
/// so it matches the app's styling and needs no notification permissions.
/// </summary>
public partial class TrayNotificationWindow : Window
{
    // On top of the card's 10px shadow margin: 12px between the card and the panel.
    private const double ScreenGap = 2;

    // Avalonia on X11/XWayland gets no leave when the pointer exits the window, so a stale hover expires.
    private static readonly TimeSpan LinuxHoverTimeout = TimeSpan.FromSeconds(3);

    private readonly IAppLogger? _logger;
    private readonly TrayNotice _notice;
    private readonly TrayAnchor _anchor;

    private DispatcherTimer? _timer;
    private TimeSpan _elapsed;
    private DateTime _lastTick;
    private bool _isHovered;
    private DateTime _lastPointerActivity;
    private bool _isClosing;
    private bool _isClosed;
    private double _placedHeight = double.NaN;

    /// <summary>The user clicked the notice itself, not its buttons.</summary>
    public event Action? BodyClicked;

    /// <summary>
    /// Kept genuinely parameterless (rather than folded into the overload below with a default
    /// argument) because Avalonia's runtime XAML loader only accepts a public parameterless
    /// constructor.
    /// </summary>
    public TrayNotificationWindow() : this(new TrayNotice(TrayNoticeKind.Detecting, string.Empty, string.Empty), null)
    {
    }

    public TrayNotificationWindow(TrayNotice notice, IAppLogger? logger)
    {
        _notice = notice;
        _logger = logger;

        InitializeComponent();

        _anchor = DetectAnchor();
        Classes.Add(_anchor.Edge switch
        {
            TrayEdge.Top => "from-top",
            TrayEdge.Left => "from-left",
            TrayEdge.Right => "from-right",
            _ => "from-bottom"
        });

        ApplyNotice();

        // The height is only known after layout, which Show() runs before mapping the window.
        LayoutUpdated += OnLayoutUpdated;
    }

    private TrayAnchor DetectAnchor()
    {
        var screen = Screens.Primary;
        if (screen == null) return new TrayAnchor(OperatingSystem.IsWindows() ? TrayEdge.Bottom : TrayEdge.Top, false);

        var anchor = TrayPlacement.Detect(screen.Bounds, screen.WorkingArea);
        _logger?.Info($"[TrayNotificationWindow] Tray assumed on the {anchor.Edge} edge ({(anchor.IsMeasured ? "measured" : "default for this desktop")}; bounds {screen.Bounds}, working area {screen.WorkingArea}).");
        return anchor;
    }

    private void ApplyNotice()
    {
        TitleText.Text = _notice.Title;
        BodyText.Text = _notice.Body;
        KickerText.Text = _notice.LiveKicker?.Invoke() ?? _notice.Kicker;

        switch (_notice.Kind)
        {
            case TrayNoticeKind.Intro:
                IntroDrawing.IsVisible = true;
                KickerRow.IsVisible = false;
                TitleRow.Margin = new Thickness(0, 0, 0, 3);
                LayoutIntroDrawing();
                break;

            case TrayNoticeKind.Update:
                KickerLogo.IsVisible = true;
                TitleRow.Margin = new Thickness(0, 10, 0, 3);
                ContentGrid.Margin = new Thickness(14, 12, 40, 12);
                ActionButton.Margin = new Thickness(14, 0, 14, 16);
                if (_notice.Version != null)
                {
                    VersionText.Text = _notice.Version;
                    VersionPill.IsVisible = true;
                }
                break;

            default:
                StatusLogo.IsVisible = true;
                if (_notice.Kind == TrayNoticeKind.Paused)
                {
                    KickerPause.IsVisible = true;
                }
                else
                {
                    KickerDot.IsVisible = true;
                    KickerDot.Fill = (IBrush?)this.FindResource(_notice.Kind switch
                    {
                        TrayNoticeKind.Playing => "BrandPrimaryBrush",
                        TrayNoticeKind.PendingSession => "PendingDotBrush",
                        _ => "DetectingDotBrush"
                    });

                    // A session waiting on the user is a standstill, not activity.
                    if (_notice.Kind == TrayNoticeKind.PendingSession) KickerDot.Classes.Remove("pulse");
                }
                break;
        }

        if (_notice.Action != null)
        {
            ActionButton.Content = _notice.ActionText ?? string.Empty;
            ActionButton.IsVisible = true;
            var margin = ContentGrid.Margin;
            ContentGrid.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 12);
        }

        NotificationProgress.IsVisible = _notice.Duration != null;
    }

    /// <summary>The strip is the panel and the dot the tray at its end; the arrow points the same way.</summary>
    private void LayoutIntroDrawing()
    {
        bool horizontal = _anchor.Edge is TrayEdge.Top or TrayEdge.Bottom;

        MiniPanel.Width = horizontal ? double.NaN : 8;
        MiniPanel.Height = horizontal ? (_anchor.Edge == TrayEdge.Top ? 6 : 8) : double.NaN;
        MiniPanel.HorizontalAlignment = _anchor.Edge switch
        {
            TrayEdge.Left => HorizontalAlignment.Left,
            TrayEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Stretch
        };
        MiniPanel.VerticalAlignment = _anchor.Edge switch
        {
            TrayEdge.Top => VerticalAlignment.Top,
            TrayEdge.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Stretch
        };

        MiniTray.HorizontalAlignment = _anchor.AtRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        MiniTray.VerticalAlignment = _anchor.AtBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        MiniTray.Margin = horizontal
            ? new Thickness(_anchor.AtRight ? 0 : 5, 0, _anchor.AtRight ? 5 : 0, _anchor.AtBottom ? 1 : 0)
            : new Thickness(_anchor.AtRight ? 0 : 1, 0, _anchor.AtRight ? 1 : 0, 5);

        // The path points down-right; clockwise rotation turns it towards the other corners.
        double angle = (_anchor.AtBottom, _anchor.AtRight) switch
        {
            (true, true) => 0,
            (true, false) => 90,
            (false, true) => -90,
            _ => 180
        };
        IntroArrow.RenderTransform = new RotateTransform(angle);
        Grid.SetColumn(IntroArrow, _anchor.AtRight ? 2 : 0);
        IntroArrow.Margin = _anchor.AtRight ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (Bounds.Height <= 0 || Bounds.Height == _placedHeight) return;

        _placedHeight = Bounds.Height;
        PositionWindow();
    }

    /// <summary>
    /// Pins the window to the tray's corner of the primary screen's working area, which already
    /// leaves out every panel. Positions are physical pixels while sizes are logical, so the
    /// scaling factor has to be applied by hand or the placement drifts on high-DPI displays.
    /// </summary>
    private void PositionWindow()
    {
        try
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            double scaling = screen.Scaling;
            var area = screen.WorkingArea;

            int width = (int)(Bounds.Width * scaling);
            int height = (int)(Bounds.Height * scaling);
            int gap = (int)(ScreenGap * scaling);

            int x = _anchor.AtRight ? area.Right - width - gap : area.X + gap;
            int y = _anchor.AtBottom ? area.Bottom - height - gap : area.Y + gap;

            Position = new PixelPoint(x, y);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error positioning notification window: {ex.Message}");
            _logger?.Warning($"[TrayNotificationWindow] Could not position the notice next to the tray icon: {ex.Message}. It appears wherever the window manager puts it.");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        Classes.Add("visible");

        if (_notice.Duration == null) return;

        _lastTick = DateTime.UtcNow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        bool paused = _isHovered && (!OperatingSystem.IsLinux() || now - _lastPointerActivity < LinuxHoverTimeout);
        if (!paused) _elapsed += now - _lastTick;
        _lastTick = now;

        if (_notice.LiveKicker != null) KickerText.Text = _notice.LiveKicker();

        var duration = _notice.Duration!.Value;
        var remaining = duration - _elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            NotificationProgress.Value = 0;
            StartCloseAnimation();
        }
        else
        {
            NotificationProgress.Value = remaining.TotalMilliseconds / duration.TotalMilliseconds * 100;
        }
    }

    private void Card_PointerEntered(object? sender, PointerEventArgs e)
    {
        _isHovered = true;
        _lastPointerActivity = DateTime.UtcNow;
    }

    private void Card_PointerMoved(object? sender, PointerEventArgs e) => _lastPointerActivity = DateTime.UtcNow;

    private void Card_PointerExited(object? sender, PointerEventArgs e) => _isHovered = false;

    /// <summary>Buttons mark their release as handled, so this only fires for the rest of the card.</summary>
    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || _isClosing) return;

        BodyClicked?.Invoke();
    }

    /// <summary>
    /// Drops the "visible" class to trigger the fade and slide defined in XAML, then closes once
    /// they have played. The delay must stay in step with those transitions, or the window
    /// vanishes instead of fading.
    /// </summary>
    private void StartCloseAnimation()
    {
        if (_isClosing) return;
        _isClosing = true;

        _timer?.Stop();
        Classes.Remove("visible");

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        closeTimer.Tick += (s, ev) =>
        {
            closeTimer.Stop();
            if (!_isClosed) Close();
        };
        closeTimer.Start();
    }

    public void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _notice.Dismissed?.Invoke();
        StartCloseAnimation();
    }

    /// <summary>Runs the action and closes: leaving the notice up after a click reads as a no-op.</summary>
    public void ActionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _notice.Action?.Invoke();
        if (!_isClosed) StartCloseAnimation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _timer?.Stop();
        LayoutUpdated -= OnLayoutUpdated;
        base.OnClosed(e);
    }
}
