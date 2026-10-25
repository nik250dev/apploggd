using System.Collections.Generic;
using Avalonia.Media;
using BackloggdMirror.Models;
using BackloggdMirror.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackloggdMirror.ViewModels;

/// <summary>One entry of the note's privacy menu, the same four Backloggd offers.</summary>
public sealed class NotePrivacyOption : ObservableObject
{
    private readonly string _labelKey;
    private readonly string _descriptionKey;

    private NotePrivacyOption(NotePrivacy value, string labelKey, string descriptionKey, string icon)
    {
        Value = value;
        _labelKey = labelKey;
        _descriptionKey = descriptionKey;
        Icon = StreamGeometry.Parse(icon);

        LocalizationService.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Description));
        };
    }

    public NotePrivacy Value { get; }

    public bool IsDefault => Value == NotePrivacy.Default;

    /// <summary>A 24x24 stroke icon.</summary>
    public Geometry Icon { get; }

    public string Label => LocalizationService.Instance[_labelKey];

    public string Description => LocalizationService.Instance[_descriptionKey];

    public static IReadOnlyList<NotePrivacyOption> All { get; } = new[]
    {
        new NotePrivacyOption(NotePrivacy.Default, "Privacy_Default", "Privacy_DefaultDescription",
            "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12A3 3 0 1 1 9 12A3 3 0 1 1 15 12Z"),
        new NotePrivacyOption(NotePrivacy.Public, "Privacy_Public", "Privacy_PublicDescription",
            "M22 12A10 10 0 1 1 2 12A10 10 0 1 1 22 12Z M2 12H22 M12 2A15.3 15.3 0 0 1 16 12A15.3 15.3 0 0 1 12 22A15.3 15.3 0 0 1 8 12A15.3 15.3 0 0 1 12 2Z"),
        new NotePrivacyOption(NotePrivacy.Friends, "Privacy_Friends", "Privacy_FriendsDescription",
            "M16 21V19A4 4 0 0 0 12 15H6A4 4 0 0 0 2 19V21 M13 7A4 4 0 1 1 5 7A4 4 0 1 1 13 7Z M22 21V19A4 4 0 0 0 19 15.13 M16 3.13A4 4 0 0 1 16 10.88"),
        new NotePrivacyOption(NotePrivacy.Private, "Privacy_Private", "Privacy_PrivateDescription",
            "M7 11H17A2 2 0 0 1 19 13V19A2 2 0 0 1 17 21H7A2 2 0 0 1 5 19V13A2 2 0 0 1 7 11Z M8 11V7A4 4 0 0 1 16 7V11")
    };
}
