using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;

namespace BackloggdMirror.Views.Controls;

/// <summary>
/// Three wavy wisps rising from the Ko-fi mug: a puff every few seconds, or nonstop while <see cref="IsContinuous"/>.
/// Drawn on a 14×11 grid scaled to the bounds.
/// </summary>
public class KofiSteam : LoopingAnimationControl
{
    public static readonly StyledProperty<bool> IsContinuousProperty =
        AvaloniaProperty.Register<KofiSteam, bool>(nameof(IsContinuous));

    private const double GridWidth = 14;
    private const double Flight = 2.1;
    private const double IdlePeriod = 5;
    private const double Thickness = 1.4;
    private const int Samples = 12;

    // The middle wisp sways the other way, so the three do not look like copies.
    private static readonly (double X, double Sway, double Delay)[] Wisps = [(3.5, 1, 0), (7, -1, 0.35), (10.5, 1, 0.7)];

    private static readonly (double At, double X, double Y)[] DriftKeys = [(0, 0, 1), (0.52, 0.8, -1), (0.81, -0.8, -2), (1, 0, -3)];

    private readonly double[] _launch = new double[3];
    private readonly double[] _next = new double[3];
    private bool _scheduled;

    public bool IsContinuous
    {
        get => GetValue(IsContinuousProperty);
        set => SetValue(IsContinuousProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty && IsActive) _scheduled = false;
        else if (change.Property == IsContinuousProperty && _scheduled) Reschedule();
    }

    // A wisp already in the air finishes its flight; only the next launch moves.
    private void Reschedule()
    {
        for (int i = 0; i < Wisps.Length; i++)
        {
            bool flying = Time - _launch[i] < Flight;
            if (IsContinuous) _next[i] = flying ? _launch[i] + Flight : Time + Wisps[i].Delay;
            else if (double.IsNegativeInfinity(_launch[i])) _next[i] = Time + IdlePeriod - Flight + Wisps[i].Delay;
            else _next[i] = _launch[i] + IdlePeriod;
        }
    }

    protected override void RenderFrame(DrawingContext context)
    {
        if (!_scheduled)
        {
            for (int i = 0; i < Wisps.Length; i++)
            {
                _launch[i] = double.NegativeInfinity;
                _next[i] = Time + (IsContinuous ? 0 : IdlePeriod - Flight) + Wisps[i].Delay;
            }
            _scheduled = true;
        }

        double scale = Bounds.Width / GridWidth;

        for (int i = 0; i < Wisps.Length; i++)
        {
            if (Time >= _next[i] && Time - _launch[i] >= Flight)
            {
                // After a long gap between frames it starts now rather than halfway through.
                _launch[i] = Time - _next[i] < Flight ? _next[i] : Time;
                _next[i] = _launch[i] + (IsContinuous ? Flight : IdlePeriod);
            }

            double progress = (Time - _launch[i]) / Flight;
            if (progress is >= 0 and < 1) DrawWisp(context, Wisps[i], progress, scale);
        }
    }

    private void DrawWisp(DrawingContext context, (double X, double Sway, double Delay) wisp, double progress, double scale)
    {
        // A stretch as long as the wisp travels up its path: drawn in from the bottom, then out at the top.
        double head = Math.Min(1, 2 * progress);
        double tail = Math.Max(0, 2 * progress - 1);
        if (head - tail < 0.02) return;

        double opacity = progress < 0.24 ? 0.9 * Ease(progress / 0.24) : 0.9 * (1 - Ease((progress - 0.24) / 0.76));
        if (opacity <= 0) return;

        var (dx, dy) = Drift(progress);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int s = 0; s <= Samples; s++)
            {
                double u = tail + (head - tail) * s / Samples;
                var point = new Point(
                    (wisp.X - wisp.Sway * 0.9 * Math.Sin(2 * Math.PI * u) + dx) * scale,
                    (10.5 - 10.4 * u + dy) * scale);

                if (s == 0) ctx.BeginFigure(point, false);
                else ctx.LineTo(point);
            }
            ctx.EndFigure(false);
        }

        var pen = new ImmutablePen(new ImmutableSolidColorBrush(Color, opacity), Thickness * scale,
                                   lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, pen, geometry);
    }

    private static (double X, double Y) Drift(double progress)
    {
        for (int k = 1; k < DriftKeys.Length; k++)
        {
            var (from, to) = (DriftKeys[k - 1], DriftKeys[k]);
            if (progress > to.At) continue;

            double t = Ease((progress - from.At) / (to.At - from.At));
            return (from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }

        return (DriftKeys[^1].X, DriftKeys[^1].Y);
    }

    private static double Ease(double t) => t * t * (3 - 2 * t);
}
