using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;

namespace BackloggdMirror.Views.Controls;

/// <summary>A ring that grows out of its bounds and fades away, over and over: the app is listening.</summary>
public class ListeningRing : LoopingAnimationControl
{
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ListeningRing, double>(nameof(StrokeThickness), 1.5);

    /// <summary>How big the ring gets, relative to the bounds.</summary>
    public static readonly StyledProperty<double> MaxScaleProperty =
        AvaloniaProperty.Register<ListeningRing, double>(nameof(MaxScale), 1.9);

    public static readonly StyledProperty<double> StartOpacityProperty =
        AvaloniaProperty.Register<ListeningRing, double>(nameof(StartOpacity), 0.5);

    static ListeningRing()
    {
        AffectsRender<ListeningRing>(StrokeThicknessProperty, MaxScaleProperty, StartOpacityProperty);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double MaxScale
    {
        get => GetValue(MaxScaleProperty);
        set => SetValue(MaxScaleProperty, value);
    }

    public double StartOpacity
    {
        get => GetValue(StartOpacityProperty);
        set => SetValue(StartOpacityProperty, value);
    }

    protected override void RenderFrame(DrawingContext context)
    {
        // Cubic ease-out: quick at first, slowing down as it fades.
        double progress = 1 - Math.Pow(1 - Phase, 3);
        double scale = 1 + (MaxScale - 1) * progress;
        double opacity = StartOpacity * (1 - progress);
        if (opacity <= 0) return;

        double thickness = StrokeThickness * scale;
        double radius = Math.Min(Bounds.Width, Bounds.Height) / 2 * scale - thickness / 2;
        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color, opacity), thickness);
        context.DrawEllipse(null, pen, new Point(Bounds.Width / 2, Bounds.Height / 2), radius, radius);
    }
}
