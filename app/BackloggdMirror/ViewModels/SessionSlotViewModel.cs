using System;
using System.Diagnostics;
using BackloggdMirror.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackloggdMirror.ViewModels;

/// <summary>One half of the Home: a detector with its own session, pause switch and play time.</summary>
public partial class SessionSlotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;
    private readonly Stopwatch _stopwatch = new();

    public SessionSlotViewModel(MainWindowViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    public int Index { get; }

    /// <summary>The game being timed; it keeps the PID that ends the session.</summary>
    internal DetectedGame? CurrentGame { get; private set; }

    /// <summary>The read of today's note and tags started on detection, for the confirmation to reuse.</summary>
    internal MainWindowViewModel.SessionExtrasPrefetch? ExtrasPrefetch { get; set; }

    internal TimeSpan Elapsed => _stopwatch.Elapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaitingAnimationVisible))]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _playTime = "00:00:00";

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _gameBackgroundImage;

    [ObservableProperty]
    private bool _isBackgroundImageVisible;

    /// <summary>Switches the prompt next to the link: a ROM is a game, so "Not a game?" does not fit it.</summary>
    [ObservableProperty]
    private bool _isRunningGameEmulated;

    /// <summary>The user's switch. Only changes while the slot is idle, and before an update.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionActive))]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionHeld))]
    [NotifyPropertyChangedFor(nameof(IsDetectionPausedTextVisible))]
    [NotifyPropertyChangedFor(nameof(IsWaitingAnimationVisible))]
    private bool _isUserPaused;

    /// <summary>Held while its finished session waits in the confirmation: a new game would overwrite it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionActive))]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionHeld))]
    [NotifyPropertyChangedFor(nameof(IsDetectionPausedTextVisible))]
    [NotifyPropertyChangedFor(nameof(IsWaitingAnimationVisible))]
    private bool _isAwaitingConfirmation;

    /// <summary>Held while its finished session is registered by the automatic mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionActive))]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionHeld))]
    [NotifyPropertyChangedFor(nameof(IsDetectionPausedTextVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetectionSwitchEnabled))]
    [NotifyPropertyChangedFor(nameof(IsWaitingAnimationVisible))]
    [NotifyPropertyChangedFor(nameof(IsAutoSaveSpinnerVisible))]
    private bool _isAutoSavingSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaitingAnimationVisible))]
    [NotifyPropertyChangedFor(nameof(IsAutoSaveSpinnerVisible))]
    private bool _isHomeVisible = true;

    /// <summary>Half the width of the Home, with the dual detection on.</summary>
    [ObservableProperty]
    private bool _isCompact;

    /// <summary>This half's entry in the tray menu.</summary>
    [ObservableProperty]
    private string _trayMenuText = string.Empty;

    /// <summary>"1" or "2" for the user: the halves are named by game, left first.</summary>
    public int Number => Index + 1;

    public bool IsGameDetectionActive => !IsUserPaused && !IsAwaitingConfirmation && !IsAutoSavingSession;

    public bool IsGameDetectionHeld => !IsGameDetectionActive;

    public bool IsDetectionPausedTextVisible => (IsUserPaused || IsAwaitingConfirmation) && !IsAutoSavingSession;

    public bool IsDetectionSwitchEnabled => !IsAutoSavingSession;

    public bool IsAutoSaveSpinnerVisible => IsHomeVisible && IsAutoSavingSession;

    /// <summary>Drives the waiting state's idle animations, which a collapsed ancestor alone would not stop.</summary>
    public bool IsWaitingAnimationVisible => IsHomeVisible && !IsGameRunning && IsGameDetectionActive;

    /// <summary>Free for the next game the detection finds.</summary>
    public bool IsDetecting => IsGameDetectionActive && !IsGameRunning;

    /// <summary>Busy with a session in any of its stages; the switch cannot change meanwhile.</summary>
    public bool IsBusy => IsGameRunning || IsAwaitingConfirmation || IsAutoSavingSession;

    [RelayCommand]
    private void ToggleGameDetection() => _owner.ToggleSlotDetection(this);

    [RelayCommand]
    private void BlacklistRunningGame() => _owner.BlacklistRunningGame(this);

    internal void Start(DetectedGame game)
    {
        CurrentGame = game;
        GameName = game.Name;
        IsRunningGameEmulated = game.Source == DetectionSource.Emulator;
        ExtrasPrefetch = null;
        _stopwatch.Restart();
        PlayTime = "00:00:00";
        IsGameRunning = true;
    }

    internal void RefreshPlayTime()
    {
        if (IsGameRunning) PlayTime = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
    }

    /// <summary>Ends the timing and clears the slot; what the session was is read before calling this.</summary>
    internal void Stop()
    {
        _stopwatch.Stop();
        IsGameRunning = false;
        CurrentGame = null;
        GameName = string.Empty;
        ExtrasPrefetch = null;
        IsBackgroundImageVisible = false;
    }

    /// <summary>Drops the session being timed, without registering it or asking the user.</summary>
    internal void Discard()
    {
        Stop();
        _stopwatch.Reset();
        PlayTime = "00:00:00";
    }

    /// <summary>For a late artwork download: only the session it was requested for.</summary>
    internal bool IsTiming(DetectedGame game) => IsGameRunning && ReferenceEquals(CurrentGame, game);

    /// <summary>The previous game's artwork stays until here, so it can fade out; then it is released.</summary>
    internal void ShowArtwork(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        var previous = GameBackgroundImage;
        GameBackgroundImage = bitmap;
        IsBackgroundImageVisible = true;
        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
    }

    /// <summary>For a half that is being removed.</summary>
    internal void ReleaseArtwork()
    {
        var previous = GameBackgroundImage;
        GameBackgroundImage = null;
        previous?.Dispose();
    }
}
