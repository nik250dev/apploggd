using CommunityToolkit.Mvvm.ComponentModel;

namespace BackloggdMirror.ViewModels;

/// <summary>
/// Common base for the app's view models. Empty by design: it exists to give
/// <see cref="BackloggdMirror.ViewLocator"/> a single type to match on.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
