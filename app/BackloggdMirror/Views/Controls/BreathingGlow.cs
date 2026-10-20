using Avalonia;
using Avalonia.Media;
using System;

namespace BackloggdMirror.Views.Controls;

/// <summary>A soft glow along the inside of its rounded bounds that slowly fades in and out.</summary>
public class BreathingGlow : LoopingAnimationControl
{
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<BreathingGlow, double>(nameof(CornerRadius), 20);

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BreathingGlow, double>(nameof(BlurRadius), 70);

    /// <summary>Opacity of the glow at its strongest.</summary>
    public static readonly StyledProperty<double> IntensityProperty =
        AvaloniaProperty.Register<BreathingGlow, double>(nameof(Intensity), 0.05);

    static BreathingGlow()
    {
        CycleDurationProperty.OverrideDefaultValue<BreathingGlow>(TimeSpan.FromSeconds(6));
        AffectsRender<BreathingGlow>(CornerRadiusProperty, BlurRadiusProperty, IntensityProperty);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public double Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    protected override void RenderFrame(DrawingContext context)
    {
        // Sine in and out: 0 at the start and end of the cycle, strongest halfway.
        double strength = (1 - Math.Cos(2 * Math.PI * Phase)) / 2;
        double alpha = Math.Clamp(Intensity * strength, 0, 1);
        if (alpha <= 0) return;

        var shadow = new BoxShadow
        {
            IsInset = true,
            Blur = BlurRadius,
            Color = new Color((byte)Math.Round(alpha * 255), Color.R, Color.G, Color.B)
        };
        context.DrawRectangle(null, null, new RoundedRect(new Rect(Bounds.Size), CornerRadius), new BoxShadows(shadow));
    }
}
