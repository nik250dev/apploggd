using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace BackloggdMirror.Views.Controls;

/// <summary>A played time as "1h 23m" with small units, counted up once when it first shows.</summary>
public class SessionTimeText : TextBlock
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<SessionTimeText, TimeSpan>(nameof(Duration));

    public static readonly StyledProperty<double> UnitFontSizeProperty =
        AvaloniaProperty.Register<SessionTimeText, double>(nameof(UnitFontSize), 17);

    public static readonly StyledProperty<IBrush?> UnitForegroundProperty =
        AvaloniaProperty.Register<SessionTimeText, IBrush?>(nameof(UnitForeground));

    // Starts at 70 %: the real figure is readable almost at once, and the count still shows.
    private const double CountFrom = 0.7;
    private static readonly TimeSpan CountDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CountLength = TimeSpan.FromMilliseconds(700);

    private DispatcherTimer? _timer;
    private DateTime _countStart;
    private bool _counted;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double UnitFontSize
    {
        get => GetValue(UnitFontSizeProperty);
        set => SetValue(UnitFontSizeProperty, value);
    }

    public IBrush? UnitForeground
    {
        get => GetValue(UnitForegroundProperty);
        set => SetValue(UnitForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DurationProperty || change.Property == UnitFontSizeProperty || change.Property == UnitForegroundProperty)
        {
            Show(TotalMinutes);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_counted || TotalMinutes < 2 || ReducedMotion.IsOn)
        {
            Show(TotalMinutes);
            return;
        }

        _counted = true;
        Show((int)Math.Round(TotalMinutes * CountFrom));
        _countStart = DateTime.UtcNow + CountDelay;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopCounting();
    }

    private int TotalMinutes => (int)Duration.TotalMinutes;

    private void OnTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _countStart).TotalMilliseconds / CountLength.TotalMilliseconds;
        if (t < 0) return;
        if (t >= 1)
        {
            StopCounting();
            return;
        }

        double eased = 1 - Math.Pow(1 - t, 3);
        int from = (int)Math.Round(TotalMinutes * CountFrom);
        Show((int)Math.Round(from + (TotalMinutes - from) * eased));
    }

    private void StopCounting()
    {
        _timer?.Stop();
        _timer = null;
        Show(TotalMinutes);
    }

    private void Show(int minutes)
    {
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        int hours = minutes / 60;
        if (hours > 0)
        {
            inlines.Add(new Run(hours.ToString()));
            inlines.Add(Unit("h"));
            inlines.Add(new Run(" " + (minutes % 60).ToString("00")));
        }
        else
        {
            inlines.Add(new Run(minutes.ToString()));
        }
        inlines.Add(Unit("m"));
    }

    private Run Unit(string text)
    {
        var run = new Run(text) { FontSize = UnitFontSize, FontWeight = FontWeight.SemiBold };
        if (UnitForeground != null) run.Foreground = UnitForeground;
        return run;
    }
}
