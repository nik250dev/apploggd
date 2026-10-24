using System;
using Avalonia;
using Avalonia.Controls;

namespace BackloggdMirror.Views.Controls;

/// <summary>
/// A pending row's summary: the note (first child) takes the width it needs up to two thirds of the row,
/// and the tags (second child) wrap in whatever is left; without a note they get the whole row.
/// </summary>
public class NoteTagsRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<NoteTagsRow, double>(nameof(Spacing), 12);

    private const double NoteShare = 2.0 / 3;

    static NoteTagsRow()
    {
        AffectsMeasure<NoteTagsRow>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private Control? Note => Children.Count > 0 && Children[0].IsVisible ? Children[0] : null;
    private Control? Tags => Children.Count > 1 && Children[1].IsVisible ? Children[1] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double noteWidth = 0, used = 0, height = 0;

        if (Note is { } note)
        {
            note.Measure(new Size(width * NoteShare, double.PositiveInfinity));
            noteWidth = note.DesiredSize.Width;
            used = noteWidth;
            height = note.DesiredSize.Height;
        }

        if (Tags is { } tags)
        {
            double offset = TagsOffset(noteWidth);
            tags.Measure(new Size(Math.Max(0, width - offset), double.PositiveInfinity));
            used = offset + tags.DesiredSize.Width;
            height = Math.Max(height, tags.DesiredSize.Height);
        }

        // Only what the content needs: the button around it stretches to the whole row anyway.
        return new Size(Math.Min(used, width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double noteWidth = Note?.DesiredSize.Width ?? 0;
        Note?.Arrange(new Rect(0, 0, noteWidth, Note.DesiredSize.Height));

        double offset = TagsOffset(noteWidth);
        Tags?.Arrange(new Rect(offset, 0, Math.Max(0, finalSize.Width - offset), Tags.DesiredSize.Height));
        return finalSize;
    }

    private double TagsOffset(double noteWidth) => Note == null ? 0 : noteWidth + Spacing;
}
