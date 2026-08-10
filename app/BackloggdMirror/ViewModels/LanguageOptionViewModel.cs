using CommunityToolkit.Mvvm.ComponentModel;
using BackloggdMirror.Services;

namespace BackloggdMirror.ViewModels;

/// <summary>
/// One entry of the language picker. The name is stored as a resource key, not as literal text, so
/// the list re-labels itself when the language changes — including the entry for the language being
/// switched away from.
/// </summary>
public class LanguageOptionViewModel : ObservableObject
{
    public string Code { get; }
    public string ResourceKey { get; }

    public string DisplayName => LocalizationService.Instance[ResourceKey];

    public LanguageOptionViewModel(string code, string resourceKey)
    {
        Code = code;
        ResourceKey = resourceKey;

        // Never unsubscribed, which is fine: the options are created once and live as long as the
        // singleton they subscribe to.
        LocalizationService.Instance.PropertyChanged += (s, e) =>
        {
            // "Item[]" is the indexer-wide change notification raised by SetLanguage.
            if (e.PropertyName == "Item[]")
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        };
    }
}
