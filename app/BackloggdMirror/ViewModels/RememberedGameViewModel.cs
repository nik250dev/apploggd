using Avalonia.Media.Imaging;
using BackloggdMirror.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackloggdMirror.ViewModels;

/// <summary>A row of the remembered games in Settings.</summary>
public partial class RememberedGameViewModel : ViewModelBase
{
    public RememberedGameViewModel(RememberedGame entry)
    {
        Entry = entry;
    }

    public RememberedGame Entry { get; }

    public string GameName => Entry.GameName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover != null;

    // Bumped per download, so a late one for the previous game cannot land.
    internal int CoverRequest { get; set; }

    /// <summary>The row owns its cover: the old one goes as soon as another replaces it.</summary>
    internal void ShowCover(Bitmap? bitmap)
    {
        var previous = Cover;
        Cover = bitmap;
        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
    }

    /// <summary>After the game was changed in place.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(GameName));
}
