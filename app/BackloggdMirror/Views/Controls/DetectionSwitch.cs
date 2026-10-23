using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Transformation;

namespace BackloggdMirror.Views.Controls;

/// <summary>
/// Two labelled halves with a thumb that slides between them. Shows <see cref="ToggleButton.IsChecked"/>
/// as bound and leaves the change to <see cref="Button.Command"/>. With the pointer, pressing the half
/// that is already selected does nothing; keyboard, controller and automation flip it.
/// </summary>
public class DetectionSwitch : ToggleButton
{
    public static readonly StyledProperty<string?> OnLabelProperty =
        AvaloniaProperty.Register<DetectionSwitch, string?>(nameof(OnLabel));

    public static readonly StyledProperty<string?> OffLabelProperty =
        AvaloniaProperty.Register<DetectionSwitch, string?>(nameof(OffLabel));

    private Border? _thumb;
    private bool? _pressedOnHalf;

    public string? OnLabel
    {
        get => GetValue(OnLabelProperty);
        set => SetValue(OnLabelProperty, value);
    }

    public string? OffLabel
    {
        get => GetValue(OffLabelProperty);
        set => SetValue(OffLabelProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_thumb != null) _thumb.SizeChanged -= OnThumbSizeChanged;
        _thumb = e.NameScope.Find<Border>("PART_Thumb");
        if (_thumb != null) _thumb.SizeChanged += OnThumbSizeChanged;

        UpdateThumb();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty)
        {
            // Only changes after the first load slide; the initial state is just placed.
            if (IsLoaded) PseudoClasses.Set(":animated", true);
            UpdateThumb();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressedOnHalf = e.GetPosition(this).X < Bounds.Width / 2;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pressedOnHalf = null;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressedOnHalf = null;
    }

    protected override void OnClick()
    {
        if (_pressedOnHalf is { } onHalf && onHalf == IsChecked) return;
        base.OnClick();
    }

    // The bound state is the source of truth: the command decides whether it changes.
    protected override void Toggle()
    {
    }

    private void OnThumbSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateThumb();

    private void UpdateThumb()
    {
        if (_thumb == null) return;

        var transform = TransformOperations.CreateBuilder(1);
        transform.AppendTranslate(IsChecked == true ? 0 : _thumb.Bounds.Width, 0);
        _thumb.RenderTransform = transform.Build();
    }
}
