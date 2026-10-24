using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BackloggdMirror.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackloggdMirror.ViewModels;

/// <summary>The note, its privacy and the tags Backloggd keeps per log; shared by the confirmation and the pending rows.</summary>
public partial class SessionExtrasViewModel : ViewModelBase
{
    public const int MaxTags = 8;

    public sealed record Snapshot(string Note, NotePrivacyOption Privacy, string[] Tags);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(NotePreview))]
    private string _note = string.Empty;

    [ObservableProperty]
    private NotePrivacyOption _privacy = NotePrivacyOption.All[0];

    public IReadOnlyList<NotePrivacyOption> PrivacyOptions => NotePrivacyOption.All;

    public ObservableCollection<string> Tags { get; } = new();

    [ObservableProperty]
    private string _tagInput = string.Empty;

    // Two flags so each turned-away tag restarts the counter's shake.
    [ObservableProperty]
    private bool _isRejectShakeA;

    [ObservableProperty]
    private bool _isRejectShakeB;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasTags => Tags.Count > 0;
    public bool IsEmpty => !HasNote && !HasTags;
    public bool IsTagsFull => Tags.Count >= MaxTags;
    public string TagCountText => $"{Tags.Count}/{MaxTags}";
    public string TagWatermark => LocalizationService.Instance[IsTagsFull ? "Session_TagsFullWatermark" : "Session_TagsWatermark"];

    /// <summary>The note on one paragraph, quoted, for the pending row's summary.</summary>
    public string NotePreview => $"“{string.Join(' ', Note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}”";

    /// <summary>Turns what was typed into a tag; a repeated one is dropped quietly.</summary>
    [RelayCommand]
    private void CommitTagInput()
    {
        string tag = string.Join(' ', TagInput.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (tag.Length == 0) return;

        if (Tags.Any(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)))
        {
            TagInput = string.Empty;
            return;
        }

        if (IsTagsFull)
        {
            bool wasA = IsRejectShakeA;
            IsRejectShakeA = !wasA;
            IsRejectShakeB = wasA;
            return;
        }

        Tags.Add(tag);
        TagInput = string.Empty;
        NotifyTagsChanged();
    }

    [RelayCommand]
    private void RemoveLastTag()
    {
        if (Tags.Count > 0) RemoveTag(Tags[^1]);
    }

    public void RemoveTag(string tag)
    {
        if (Tags.Remove(tag)) NotifyTagsChanged();
    }

    // A comma ends a tag, as in Backloggd; pasting "a, b, c" adds the three.
    partial void OnTagInputChanged(string value)
    {
        int comma = value.LastIndexOf(',');
        if (comma < 0) return;

        foreach (string part in value[..comma].Split(','))
        {
            TagInput = part;
            CommitTagInput();
        }
        TagInput = value[(comma + 1)..].TrimStart();
    }

    public void ClearNote()
    {
        Note = string.Empty;
        Privacy = NotePrivacyOption.All[0];
    }

    public void ClearTags()
    {
        TagInput = string.Empty;
        Tags.Clear();
        NotifyTagsChanged();
    }

    public Snapshot TakeSnapshot() => new(Note, Privacy, Tags.ToArray());

    public void Restore(Snapshot snapshot)
    {
        Note = snapshot.Note;
        Privacy = snapshot.Privacy;
        TagInput = string.Empty;
        Tags.Clear();
        foreach (string tag in snapshot.Tags) Tags.Add(tag);
        NotifyTagsChanged();
    }

    private void NotifyTagsChanged()
    {
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsTagsFull));
        OnPropertyChanged(nameof(TagCountText));
        OnPropertyChanged(nameof(TagWatermark));
    }
}
