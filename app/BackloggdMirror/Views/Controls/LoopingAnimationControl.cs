using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace BackloggdMirror.Views.Controls;

/// <summary>
/// A looping animation drawn by code. Redraws at most 60 times a second, only while
/// <see cref="IsAnimationEnabled"/>, and fades in and out as <see cref="IsActive"/> changes.
/// </summary>
/// <remarks>
/// Style animations redraw at the display's refresh rate (240 times a second on some screens) and
/// cannot be throttled, which is why the idle animations of the Home are drawn this way.
/// </remarks>
public abstract class LoopingAnimationControl : Control
{
    private static readonly TimeSpan MinRedrawInterval = TimeSpan.FromSeconds(1.0 / 60);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LoopingAnimationControl, bool>(nameof(IsActive));

    /// <summary>Set from a style while the window is on screen; with it off no frames are requested.</summary>
    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<LoopingAnimationControl, bool>(nameof(IsAnimationEnabled));

    public static readonly StyledProperty<TimeSpan> CycleDurationProperty =
        AvaloniaProperty.Register<LoopingAnimationControl, TimeSpan>(nameof(CycleDuration), TimeSpan.FromSeconds(3));

    public static readonly StyledProperty<TimeSpan> FadeDurationProperty =
        AvaloniaProperty.Register<LoopingAnimationControl, TimeSpan>(nameof(FadeDuration), TimeSpan.FromMilliseconds(260));

    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<LoopingAnimationControl, Color>(nameof(Color), Colors.White);

    private TimeSpan? _lastFrame;
    private TimeSpan _lastRedraw;
    private bool _frameRequested;

    static LoopingAnimationControl()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<LoopingAnimationControl>(false);
        AffectsRender<LoopingAnimationControl>(ColorProperty);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public TimeSpan CycleDuration
    {
        get => GetValue(CycleDurationProperty);
        set => SetValue(CycleDurationProperty, value);
    }

    public TimeSpan FadeDuration
    {
        get => GetValue(FadeDurationProperty);
        set => SetValue(FadeDurationProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Position in the current cycle, from 0 to 1.</summary>
    protected double Phase { get; private set; }

    /// <summary>How faded in the animation is, from 0 to 1.</summary>
    protected double Visibility { get; private set; }

    /// <summary>Seconds the animation has been running, not counting pauses.</summary>
    protected double Time { get; private set; }

    protected abstract void RenderFrame(DrawingContext context);

    public sealed override void Render(DrawingContext context)
    {
        if (Visibility <= 0) return;

        using (context.PushOpacity(Visibility))
        {
            RenderFrame(context);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty || change.Property == IsAnimationEnabledProperty)
        {
            RequestFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _lastFrame = null;
    }

    private bool NeedsFrames => IsAnimationEnabled && (IsActive || Visibility > 0);

    private void RequestFrame()
    {
        if (_frameRequested || !NeedsFrames) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        _frameRequested = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;

        // A pause (hidden window, detached control) restarts the clock instead of jumping ahead.
        double elapsed = _lastFrame is { } last ? (now - last).TotalSeconds : 0;
        _lastFrame = NeedsFrames ? now : null;
        if (_lastFrame == null) return;

        Phase = (Phase + elapsed / Math.Max(CycleDuration.TotalSeconds, 0.1)) % 1;
        Time += elapsed;

        double fadeStep = elapsed / Math.Max(FadeDuration.TotalSeconds, 0.01);
        Visibility = IsActive ? Math.Min(1, Visibility + fadeStep) : Math.Max(0, Visibility - fadeStep);

        if (now - _lastRedraw >= MinRedrawInterval || Visibility == 0)
        {
            _lastRedraw = now;
            InvalidateVisual();
        }

        RequestFrame();
    }
}
