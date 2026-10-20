using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System;
using System.Collections.Generic;

namespace BackloggdMirror.Views.Controls;

/// <summary>A light trail that travels along the rounded border of its bounds at constant speed.</summary>
public class AmbientEdge : LoopingAnimationControl
{
    // Consecutive pieces of varying opacity; dashed pens were recomputed by Skia on every frame.
    private const int Pieces = 24;
    private const double ArcStepDegrees = 6;
    private const double GlowThicknessFactor = 4;
    private const double GlowOpacity = 0.12;

    // Where along the trail, from its tail, it is brightest; it fades out again towards the head.
    private const double PeakPosition = 0.8;

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<AmbientEdge, double>(nameof(CornerRadius), 20);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<AmbientEdge, double>(nameof(StrokeThickness), 2);

    /// <summary>Opacity of the brightest part of the trail.</summary>
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<AmbientEdge, double>(nameof(Intensity), 0.6);

    /// <summary>Length of the trail as a fraction of the perimeter.</summary>
    public static readonly StyledProperty<double> TrailLengthProperty =
        AvaloniaProperty.Register<AmbientEdge, double>(nameof(TrailLength), 0.39);

    // The outline as vertices with their distance along it, from 0 to the perimeter (same point at both ends).
    private Point[] _vertices = [];
    private double[] _distances = [];
    private (Rect Rect, double Radius) _outlineKey;

    static AmbientEdge()
    {
        CycleDurationProperty.OverrideDefaultValue<AmbientEdge>(TimeSpan.FromSeconds(6));
        FadeDurationProperty.OverrideDefaultValue<AmbientEdge>(TimeSpan.FromMilliseconds(500));
        AffectsRender<AmbientEdge>(CornerRadiusProperty, StrokeThicknessProperty, IntensityProperty, TrailLengthProperty);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    public double TrailLength
    {
        get => GetValue(TrailLengthProperty);
        set => SetValue(TrailLengthProperty, value);
    }

    protected override void RenderFrame(DrawingContext context)
    {
        double thickness = StrokeThickness;
        var rect = new Rect(Bounds.Size).Deflate(thickness / 2);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        double radius = Math.Min(Math.Max(CornerRadius - thickness / 2, 0), Math.Min(rect.Width, rect.Height) / 2);
        EnsureOutline(rect, radius);

        double perimeter = _distances[^1];
        double head = Phase * perimeter;
        double pieceLength = Math.Clamp(TrailLength, 0, 1) * perimeter / Pieces;
        double intensity = Math.Clamp(Intensity, 0, 1);

        for (int i = 0; i < Pieces; i++)
        {
            double start = head - (Pieces - i) * pieceLength;
            var piece = CreatePiece(start, start + pieceLength, perimeter);

            double opacity = intensity * Brightness((i + 0.5) / Pieces);
            context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(Color, opacity * GlowOpacity), thickness * GlowThicknessFactor, lineCap: PenLineCap.Round), piece);
            context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(Color, opacity), thickness), piece);
        }
    }

    // From 0 at the tail up to 1 at the peak, and back down to 0 at the head.
    private static double Brightness(double position) =>
        position < PeakPosition
            ? Math.Pow(position / PeakPosition, 1.6)
            : Math.Pow((1 - position) / (1 - PeakPosition), 1.2);

    private StreamGeometry CreatePiece(double from, double to, double perimeter)
    {
        // Distances may go below zero or past the perimeter: the trail wraps around the start.
        double offset = Math.Floor(from / perimeter) * perimeter;
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        ctx.BeginFigure(PointAt(from - offset), false);
        int index = Array.BinarySearch(_distances, from - offset);
        index = index >= 0 ? index + 1 : ~index;

        while (true)
        {
            if (index == _distances.Length)
            {
                index = 1;
                offset += perimeter;
            }

            if (_distances[index] + offset >= to) break;
            ctx.LineTo(_vertices[index]);
            index++;
        }

        ctx.LineTo(PointAt(to - offset));
        ctx.EndFigure(false);
        return geometry;
    }

    private Point PointAt(double distance)
    {
        int index = Array.BinarySearch(_distances, distance);
        if (index >= 0) return _vertices[index];

        index = Math.Clamp(~index, 1, _distances.Length - 1);
        double span = _distances[index] - _distances[index - 1];
        double t = span > 0 ? (distance - _distances[index - 1]) / span : 0;
        var a = _vertices[index - 1];
        var b = _vertices[index];
        return new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    // Clockwise from the end of the top-left corner.
    private void EnsureOutline(Rect rect, double radius)
    {
        if (_vertices.Length > 0 && _outlineKey == (rect, radius)) return;
        _outlineKey = (rect, radius);

        var points = new List<Point> { new(rect.Left + radius, rect.Top), new(rect.Right - radius, rect.Top) };
        AddArc(points, new Point(rect.Right - radius, rect.Top + radius), radius, -90);
        points.Add(new Point(rect.Right, rect.Bottom - radius));
        AddArc(points, new Point(rect.Right - radius, rect.Bottom - radius), radius, 0);
        points.Add(new Point(rect.Left + radius, rect.Bottom));
        AddArc(points, new Point(rect.Left + radius, rect.Bottom - radius), radius, 90);
        points.Add(new Point(rect.Left, rect.Top + radius));
        AddArc(points, new Point(rect.Left + radius, rect.Top + radius), radius, 180);

        _vertices = points.ToArray();
        _distances = new double[_vertices.Length];
        for (int i = 1; i < _vertices.Length; i++)
        {
            var delta = _vertices[i] - _vertices[i - 1];
            _distances[i] = _distances[i - 1] + Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        }
    }

    // A quarter turn clockwise from startDegrees, ending on the next straight side.
    private static void AddArc(List<Point> points, Point center, double radius, double startDegrees)
    {
        for (double angle = ArcStepDegrees; angle <= 90 + 1e-9; angle += ArcStepDegrees)
        {
            double radians = (startDegrees + angle) * Math.PI / 180;
            points.Add(new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians)));
        }
    }
}
