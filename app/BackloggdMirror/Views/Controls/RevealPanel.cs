using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace BackloggdMirror.Views.Controls;

/// <summary>Grows its child in from zero height when opened and shrinks it away when closed, fading and sliding it a little.</summary>
public class RevealPanel : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<RevealPanel, bool>(nameof(IsOpen));

    // Avalonia cannot animate a height up to Auto, so the panel animates a fraction of its child's.
    private static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RevealPanel, double>("Progress");

    private const double Slide = 6;
    private readonly TranslateTransform _offset = new();

    static RevealPanel()
    {
        AffectsMeasure<RevealPanel>(ProgressProperty);
    }

    public RevealPanel()
    {
        Transitions = new Transitions
        {
            new DoubleTransition { Property = ProgressProperty, Duration = TimeSpan.FromMilliseconds(240), Easing = new CubicEaseOut() }
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
        // Hidden once shut, so its controls leave the tab order and the controller's reach.
        child.IsVisible = IsOpen || progress > 0;
        // Clipped only while it moves: open, the controller's edge around a field reaches past it.
        ClipToBounds = progress < 1;
        child.Opacity = Math.Clamp((progress - 0.3) / 0.7, 0, 1);
        _offset.Y = -Slide * (1 - progress);
        child.RenderTransform = _offset;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;

        child.Measure(availableSize.WithHeight(double.PositiveInfinity));
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * GetValue(ProgressProperty));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }
}
