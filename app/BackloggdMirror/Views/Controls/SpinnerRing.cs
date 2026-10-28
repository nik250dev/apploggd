using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;

namespace BackloggdMirror.Views.Controls;

/// <summary>An arc that runs around the edge of its bounds, stretching and shrinking: the app is busy.</summary>
public class SpinnerRing : LoopingAnimationControl
{
    private const double MinSweep = 40;
    private const double MaxSweep = 160;
    private const int TurnsPerCycle = 2;

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SpinnerRing, double>(nameof(StrokeThickness), 2.5);

    static SpinnerRing()
    {
        CycleDurationProperty.OverrideDefaultValue<SpinnerRing>(TimeSpan.FromSeconds(2));
        AffectsRender<SpinnerRing>(StrokeThicknessProperty);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void RenderFrame(DrawingContext context)
    {
        double sweep = MinSweep + (MaxSweep - MinSweep) * (0.5 - 0.5 * Math.Cos(Phase * 2 * Math.PI));
        double start = Phase * TurnsPerCycle * 360 - 90;

        double radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - StrokeThickness / 2;
        if (radius <= 0) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(center, radius, start), false);
            ctx.ArcTo(PointOnCircle(center, radius, start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color), StrokeThickness, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
