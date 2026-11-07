using Avalonia;
using Avalonia.Controls;

namespace BackloggdMirror.Views;

/// <summary>The pill that opens the Ko-fi page: a steaming mug that unfolds into "Buy me a Ko-fi".</summary>
public partial class KofiButton : UserControl
{
    /// <summary>Whether the steam runs; tie it to the button being on screen so it stops otherwise.</summary>
    public static readonly StyledProperty<bool> IsSteamActiveProperty =
        AvaloniaProperty.Register<KofiButton, bool>(nameof(IsSteamActive));

    /// <summary>Unfolds it as if hovered, to show what it is without a pointer.</summary>
    public static readonly StyledProperty<bool> IsPeekingProperty =
        AvaloniaProperty.Register<KofiButton, bool>(nameof(IsPeeking));

    public KofiButton()
    {
        InitializeComponent();
    }

    public bool IsSteamActive
    {
        get => GetValue(IsSteamActiveProperty);
        set => SetValue(IsSteamActiveProperty, value);
    }

    public bool IsPeeking
    {
        get => GetValue(IsPeekingProperty);
        set => SetValue(IsPeekingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Read on each change, so switching animations off in Windows applies the next time it shows.
        if (change.Property == IsSteamActiveProperty) Steam.IsActive = IsSteamActive && !ReducedMotion.IsOn;
        else if (change.Property == IsPeekingProperty) LinkButton.Classes.Set("peek", IsPeeking);
    }
}
