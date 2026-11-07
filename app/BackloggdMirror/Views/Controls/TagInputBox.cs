using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BackloggdMirror.Views.Controls;

/// <summary>The line tags are typed on: Enter commits, Backspace on an empty line takes the last tag back, and the tag box around it lights up while typing.</summary>
public class TagInputBox : TextBox
{
    public static readonly StyledProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.Register<TagInputBox, ICommand?>(nameof(CommitCommand));

    public static readonly StyledProperty<ICommand?> RemoveLastCommandProperty =
        AvaloniaProperty.Register<TagInputBox, ICommand?>(nameof(RemoveLastCommand));

    // Styled as a TextBox: type selectors do not match subclasses.
    protected override Type StyleKeyOverride => typeof(TextBox);

    public ICommand? CommitCommand
    {
        get => GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    public ICommand? RemoveLastCommand
    {
        get => GetValue(RemoveLastCommandProperty);
        set => SetValue(RemoveLastCommandProperty, value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCommand?.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(Text))
        {
            RemoveLastCommand?.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        TagBox?.Classes.Set("typing", true);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        TagBox?.Classes.Set("typing", false);
    }

    // "typing" rather than :focus-within, which would light the box up for a chip's x too.
    private Border? TagBox => this.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tag-box"));
}
