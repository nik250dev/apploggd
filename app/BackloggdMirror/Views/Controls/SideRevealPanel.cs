using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace BackloggdMirror.Views.Controls;

/// <summary>Grows its child in sideways from zero width when opened and shrinks it away when closed, fading it.</summary>
public class SideRevealPanel : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SideRevealPanel, bool>(nameof(IsOpen));

    // Avalonia cannot animate a width up to Auto, so the panel animates a fraction of its child's.
    private static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SideRevealPanel, double>("Progress");

    static SideRevealPanel()
    {
        AffectsMeasure<SideRevealPanel>(ProgressProperty);
        ClipToBoundsProperty.OverrideDefaultValue<SideRevealPanel>(true);
    }

    public SideRevealPanel()
    {
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ProgressProperty, Duration = TimeSpan.FromMilliseconds(350), Easing = new CubicEaseOut() }
        };
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsOpenProperty)
        {
            UpdateChild();
            SetValue(ProgressProperty, IsOpen ? 1.0 : 0.0);
        }
        else if (change.Property == ProgressProperty || change.Property == ChildProperty)
        {
            UpdateChild();
        }
    }

    private void UpdateChild()
    {
        if (Child is not { } child) return;

        double progress = GetValue(ProgressProperty);
        child.IsVisible = IsOpen || progress > 0;
        child.Opacity = Math.Clamp((progress - 0.3) / 0.7, 0, 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;

        child.Measure(availableSize.WithWidth(double.PositiveInfinity));
        return new Size(child.DesiredSize.Width * GetValue(ProgressProperty), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, Child.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
