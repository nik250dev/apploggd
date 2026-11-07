using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using BackloggdMirror.Models;
using BackloggdMirror.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackloggdMirror.ViewModels;

/// <summary>
/// The confirmation of one finished session: the game, its time, the marks, note and tags, and what to do with it.
/// It holds its slot until it closes, whatever the user chose.
/// </summary>
public partial class SessionConfirmationViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    // The session awaiting confirmation. It survives here rather than in the slot because the modal can
    // stay open indefinitely, and the user may still change the game via the picker.
    internal TimeSpan _pendingSessionDuration;
    internal string? _pendingGameName;
    internal string? _pendingIdIgdb;
    internal string? _pendingGameUrl;
    private string? _pendingCoverUrl;
    private readonly DateTime _pendingEndedAt;
    private bool _pendingSaveFailed;

    // The detection result behind the modal, kept whole: the blacklist needs its path or content key.
    private readonly DetectedGame? _pendingGame;
    private readonly string? _pendingGameDisplayName;

    private bool _closed;

    // The game chosen by hand for this executable or ROM, which replaces what detection identified.
    private RememberedGame? _remembered;

    internal SessionConfirmationViewModel(MainWindowViewModel owner, SessionSlotViewModel slot, DetectedGame? game,
        string displayName, string gameName, TimeSpan duration, DateTime endedAt, string playTime, RememberedGame? remembered = null)
    {
        _owner = owner;
        Slot = slot;
        _remembered = remembered;
        IsGameRemembered = remembered != null;
        _pendingGame = game;
        _pendingGameDisplayName = displayName;
        _pendingGameName = gameName;
        _pendingIdIgdb = game?.IdIgdb;
        _pendingSessionDuration = duration;
        _pendingEndedAt = endedAt;
        IsPendingGameEmulated = game?.Source == DetectionSource.Emulator;
        RememberTarget = RememberTargetOf(game);
        SessionGameTitle = gameName;
        SessionPlayTime = playTime;
        _isOffline = owner.IsOffline;
        IsSlotMarkVisible = owner.IsDualDetectionActive;
    }

    /// <summary>The half the session was played in; detection there waits for this modal.</summary>
    internal SessionSlotViewModel Slot { get; }

    /// <summary>The split-screen mark by the title, which says whose half this is. The setting cannot change while a modal is up.</summary>
    public bool IsSlotMarkVisible { get; }

    public bool IsFirstSlot => Slot.Index == 0;

    internal string? GameUrl => _pendingGameUrl;

    [ObservableProperty]
    private string _sessionGameTitle = string.Empty;

    [ObservableProperty]
    private string _sessionPlayTime = string.Empty;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _sessionGameCover;

    /// <summary>True when the game has no cover to show, so the slot renders a "No cover" text instead.</summary>
    [ObservableProperty]
    private bool _isNoCoverPlaceholderVisible;

    // A real cover always wins over the placeholder, whatever set it.
    partial void OnSessionGameCoverChanged(Avalonia.Media.Imaging.Bitmap? value)
    {
        if (value != null) IsNoCoverPlaceholderVisible = false;
    }

    // Only the cover this modal downloaded is its to release: a picked one belongs to the picker, a postponed one to its pending row.
    private bool _ownsCover;
    private bool _coverReleased;

    private void ShowOwnCover(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (_coverReleased)
        {
            bitmap?.Dispose();
            return;
        }

        var previous = _ownsCover ? SessionGameCover : null;
        SessionGameCover = bitmap;
        _ownsCover = bitmap != null;
        if (!ReferenceEquals(previous, bitmap)) DisposeAfterFade(previous);
    }

    // The cover cross-fades, so the old bitmap is still drawn for a moment after it is replaced.
    private static readonly TimeSpan CoverFadeOut = TimeSpan.FromMilliseconds(600);

    private static void DisposeAfterFade(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (bitmap == null) return;
        DispatcherTimer.RunOnce(bitmap.Dispose, CoverFadeOut);
    }

    /// <summary>Once the modal is gone: its native memory goes now, not whenever the finalizer runs.</summary>
    internal void ReleaseCover()
    {
        _coverReleased = true;
        if (!_ownsCover) return;

        var cover = SessionGameCover;
        _ownsCover = false;
        SessionGameCover = null;
        cover?.Dispose();
    }

    [ObservableProperty]
    private bool _isSessionCoverLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPostponeEnabled))]
    private bool _isSessionTitleLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreButtonsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(IsPostponeEnabled))]
    [NotifyPropertyChangedFor(nameof(IsGameSelectorEnabled))]
    [NotifyPropertyChangedFor(nameof(AreSessionExtrasEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveSpinnerVisible))]
    private bool _isSavingSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreButtonsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(SaveButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(IsSaveTooltipVisible))]
    private bool _isGameIdentified = true;

    /// <summary>Mirrors the app's offline mode, which disables saving and the picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(SaveButtonTooltip))]
    [NotifyPropertyChangedFor(nameof(IsSaveTooltipVisible))]
    [NotifyPropertyChangedFor(nameof(IsGameSelectorEnabled))]
    private bool _isOffline;

    public bool AreButtonsEnabled => !IsSavingSession;

    // The picker searches Backloggd, so it is useless offline.
    public bool IsGameSelectorEnabled => !IsSavingSession && !IsOffline;

    // Not while today's note and tags are read either: saving would write over them unseen.
    public bool IsSaveEnabled => !IsSavingSession && IsGameIdentified && !IsOffline && !IsSessionExtrasLoading;

    // Not while the title resolves: the pending entry would keep a half-identified game. Nor without the note and tags.
    public bool IsPostponeEnabled => !IsSavingSession && !IsSessionTitleLoading && !IsSessionExtrasLoading;

    public string? SaveButtonTooltip => IsOffline
        ? LocalizationService.Instance["Session_OfflineSaveTooltip"]
        : IsGameIdentified ? null : LocalizationService.Instance["Session_UnidentifiedGame"];

    public bool IsSaveTooltipVisible => IsOffline || !IsGameIdentified;

    /// <summary>Saved on Backloggd: the button shows its check for a moment before the modal closes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveSpinnerVisible))]
    private bool _isSessionSaved;

    public bool IsSaveSpinnerVisible => IsSavingSession && !IsSessionSaved;

    /// <summary>Played time, for the counter; SessionPlayTime is the same as text.</summary>
    public TimeSpan SessionDuration => _pendingSessionDuration;

    /// <summary>The line over the game: when the session ended, in the app's language.</summary>
    public string SessionEndedText
    {
        get
        {
            var loc = LocalizationService.Instance;
            var culture = loc.CurrentCulture;
            return string.Format(loc["Session_EndedAt"], _pendingEndedAt.ToString("t", culture)).ToUpper(culture);
        }
    }

    /// <summary>Switches the prompt next to the link: a ROM is a game, so "Not a game?" does not fit it.</summary>
    [ObservableProperty]
    private bool _isPendingGameEmulated;

    /// <summary>What a remembered game is tied to, as shown: the executable's file name, or the ROM's. Null when neither is known.</summary>
    public string? RememberTarget { get; }

    // The game detection gave, taken before the first pick, to tell a real change from picking it again.
    private bool _detectedGameTaken;
    private string? _detectedGameSlug;

    [ObservableProperty]
    private string _detectedGameTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRememberGameOffered))]
    private bool _isGameChangedByHand;

    /// <summary>The "always use this game" box, offered once the game was changed in the picker.</summary>
    public bool IsRememberGameOffered => IsGameChangedByHand && RememberTarget != null;

    [ObservableProperty]
    private bool _isRememberGameChosen;

    /// <summary>The game came from a remembered choice rather than from detection.</summary>
    [ObservableProperty]
    private bool _isGameRemembered;

    private static string? RememberTargetOf(DetectedGame? game)
    {
        string? key = game?.Source == DetectionSource.Emulator ? game.ContentKey : game?.ExecutablePath;
        return string.IsNullOrEmpty(key) ? null : RememberedGame.FileNameOf(key);
    }

    private static string? SlugOf(string? url) => url?.Trim('/').Split('/')[^1].ToLowerInvariant();

    /// <summary>Backloggd's "Started" and "Finished" marks, chosen in the confirmation.</summary>
    [ObservableProperty]
    private bool _isSessionMarkedStarted;

    [ObservableProperty]
    private bool _isSessionMarkedFinished;

    /// <summary>Backloggd's note and tags for the day's play session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionExtrasRowOpen))]
    private bool _isSessionNoteOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionExtrasRowOpen))]
    private bool _isSessionTagsOpen;

    /// <summary>The row of "Add note" / "Add tags" buttons, while either is still closed.</summary>
    public bool IsSessionExtrasRowOpen => !(IsSessionNoteOpen && IsSessionTagsOpen);

    /// <summary>The note and tags typed in the confirmation, starting from those today's play session already has.</summary>
    public SessionExtrasViewModel SessionExtras { get; } = new();

    /// <summary>While today's note and tags are read from Backloggd; they cannot be edited meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreSessionExtrasEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(IsPostponeEnabled))]
    private bool _isSessionExtrasLoading;

    public bool AreSessionExtrasEnabled => AreButtonsEnabled && !IsSessionExtrasLoading;

    /// <summary>Set while the fields open to show what was read, so the view does not move the focus into them.</summary>
    public bool IsFillingSessionExtras { get; private set; }

    // Bumped per read, and on close, so a late result cannot land on another game or session.
    private int _sessionExtrasRequest;

    // Whether today's note and tags were read; until then an untouched, empty pair must not wipe them.
    private bool _sessionExtrasKnown;

    /// <summary>Resolves the game behind the session: its canonical name, Backloggd link, cover, and today's note and tags.</summary>
    internal void Begin(MainWindowViewModel.SessionExtrasPrefetch? extrasPrefetch) => ResolveGame(extrasPrefetch);

    private void ResolveGame(MainWindowViewModel.SessionExtrasPrefetch? extrasPrefetch)
    {
        // Resolve game data from JSON using IGDB ID
        IsNoCoverPlaceholderVisible = false;
        IsSessionCoverLoading = true;
        IsSessionTitleLoading = true;

        if (_remembered != null)
        {
            ResolveRememberedGame(_remembered, extrasPrefetch);
            return;
        }

        var logger = _owner.Logger;
        string? currentIdIgdb = _pendingIdIgdb;

        if (!string.IsNullOrEmpty(currentIdIgdb))
        {
            var lookupResult = _owner.GameData.LookupByIgdbId(currentIdIgdb);
            if (lookupResult != null)
            {
                // Update game name from JSON
                SessionGameTitle = lookupResult.Name;
                _pendingGameName = lookupResult.Name;
                _pendingGameUrl = lookupResult.BackloggdGameUrl;
                _pendingCoverUrl = lookupResult.CoverUrl;
                IsGameIdentified = true;
                IsSessionTitleLoading = false;
                _ = LoadTodaySessionExtrasAsync(extrasPrefetch);

                // Download cover image
                if (!string.IsNullOrEmpty(lookupResult.CoverUrl))
                {
                    var coverUrl = lookupResult.CoverUrl;
                    _ = Task.Run(async () =>
                    {
                        var bitmap = await _owner.Browser.DownloadImageAsync(coverUrl);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (bitmap != null)
                            {
                                ShowOwnCover(bitmap);
                            }
                            else
                            {
                                // Failed to download cover, use placeholder
                                ShowNoCoverPlaceholder();
                            }
                            IsSessionCoverLoading = false;
                        });
                    });
                }
                else
                {
                    // No cover in JSON, use placeholder
                    ShowNoCoverPlaceholder();
                    IsSessionCoverLoading = false;
                }
            }
            else
            {
                // IGDB ID not found in local database — try API fallback
                // This happens when the game was identified via IgdbResolverService.TryMatchApi
                Console.WriteLine($"[StopRunningGame] IGDB ID '{currentIdIgdb}' not in local JSON. Trying API fallback...");
                logger.Info($"[StopRunningGame] IGDB ID '{currentIdIgdb}' is not in the local database. Falling back to the API for the cover and the Backloggd link.");
                var capturedIdIgdb = currentIdIgdb;
                _ = Task.Run(async () =>
                {
                    var apiResult = await _owner.GameData.LookupByIgdbIdFromApiAsync(capturedIdIgdb);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (apiResult != null)
                        {
                            // API resolved the game — mark as identified
                            _pendingGameUrl = apiResult.BackloggdGameUrl;
                            _pendingCoverUrl = apiResult.CoverUrl;
                            IsGameIdentified = true;
                            IsSessionTitleLoading = false;
                            _ = LoadTodaySessionExtrasAsync(extrasPrefetch);

                            // Download cover
                            if (!string.IsNullOrEmpty(apiResult.CoverUrl))
                            {
                                var coverUrl = apiResult.CoverUrl;
                                _ = Task.Run(async () =>
                                {
                                    var bitmap = await _owner.Browser.DownloadImageAsync(coverUrl);
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        ShowOwnCover(bitmap);
                                        if (bitmap == null) ShowNoCoverPlaceholder();
                                        IsSessionCoverLoading = false;
                                    });
                                });
                            }
                            else
                            {
                                ShowNoCoverPlaceholder();
                                IsSessionCoverLoading = false;
                            }
                        }
                        else
                        {
                            // API also failed — treat as truly unidentified
                            Console.WriteLine($"[StopRunningGame] API fallback also failed for IGDB ID '{capturedIdIgdb}'.");
                            logger.Warning($"[StopRunningGame] The API fallback resolved nothing for IGDB ID '{capturedIdIgdb}'. The session is offered as unidentified, so the user has to pick the game by hand.");
                            ShowNoCoverPlaceholder();
                            IsSessionCoverLoading = false;
                            IsSessionTitleLoading = false;
                            IsGameIdentified = false;
                            _pendingGameUrl = null;
                        }
                    });
                });
            }
        }
        else
        {
            // No IGDB ID — unidentified game
            Console.WriteLine($"[StopRunningGame] No IGDB ID for '{_pendingGameName}'. Game is unidentified.");
            logger.Info($"[StopRunningGame] Detection produced no IGDB ID for '{_pendingGameName}'. The session is offered as unidentified, so the user has to pick the game by hand.");
            ShowNoCoverPlaceholder();
            IsSessionCoverLoading = false;
            IsSessionTitleLoading = false;
            IsGameIdentified = false;
            _pendingGameUrl = null;
        }
    }

    private void ResolveRememberedGame(RememberedGame remembered, MainWindowViewModel.SessionExtrasPrefetch? extrasPrefetch)
    {
        // What detection would have given: "Detected as" names it, and picking it again is no change.
        var detected = string.IsNullOrEmpty(_pendingGame?.IdIgdb) ? null : _owner.GameData.LookupByIgdbId(_pendingGame.IdIgdb);
        _detectedGameTaken = true;
        _detectedGameSlug = SlugOf(detected?.BackloggdGameUrl);
        DetectedGameTitle = detected?.Name ?? _pendingGame?.Name ?? string.Empty;

        SessionGameTitle = remembered.GameName;
        _pendingGameName = remembered.GameName;
        _pendingGameUrl = remembered.GameUrl;
        _pendingIdIgdb = null;
        _pendingCoverUrl = remembered.CoverUrl;
        IsGameIdentified = !string.IsNullOrEmpty(remembered.GameUrl);
        IsSessionTitleLoading = false;
        _ = LoadTodaySessionExtrasAsync(extrasPrefetch);

        if (string.IsNullOrEmpty(remembered.CoverUrl))
        {
            ShowNoCoverPlaceholder();
            IsSessionCoverLoading = false;
            return;
        }

        var coverUrl = remembered.CoverUrl;
        _ = Task.Run(async () =>
        {
            var bitmap = await _owner.Browser.DownloadImageAsync(coverUrl);
            Dispatcher.UIThread.Post(() =>
            {
                // Forgotten meanwhile: this cover is no longer the game shown.
                if (!ReferenceEquals(_remembered, remembered))
                {
                    bitmap?.Dispose();
                    return;
                }

                ShowOwnCover(bitmap);
                if (bitmap == null) ShowNoCoverPlaceholder();
                IsSessionCoverLoading = false;
            });
        });
    }

    /// <summary>Shows the "No cover" text placeholder in the session cover slot.</summary>
    private void ShowNoCoverPlaceholder()
    {
        ShowOwnCover(null);
        IsNoCoverPlaceholderVisible = true;
    }

    [RelayCommand]
    private void OpenGameSelector()
    {
        if (IsOffline) return;
        _owner.OpenGameSelectorFor(this);
    }

    /// <summary>The game chosen in the picker, which fixes the title and the link and allows saving.</summary>
    internal void ApplyPickedGame(GameSearchResult selectedGame)
    {
        if (!_detectedGameTaken)
        {
            _detectedGameTaken = true;
            DetectedGameTitle = SessionGameTitle;
            _detectedGameSlug = IsGameIdentified ? SlugOf(_pendingGameUrl) : null;
        }

        // Changing a remembered game offers to remember the new one in its place; left unticked, it is just this once.
        if (IsGameRemembered)
        {
            _remembered = null;
            IsGameRemembered = false;
            IsRememberGameChosen = true;
        }

        IsGameChangedByHand = SlugOf(selectedGame.RedirectLink) != _detectedGameSlug;
        if (!IsGameChangedByHand) IsRememberGameChosen = false;

        SessionGameTitle = selectedGame.Title;
        var downloaded = _ownsCover ? SessionGameCover : null;
        SessionGameCover = selectedGame.CoverBitmap;
        _ownsCover = false;
        DisposeAfterFade(downloaded);
        _pendingGameName = selectedGame.Title;
        _pendingGameUrl = selectedGame.RedirectLink;
        _pendingIdIgdb = null;
        _pendingCoverUrl = string.IsNullOrEmpty(selectedGame.CoverUrl) ? null : selectedGame.CoverUrl;
        IsGameIdentified = true;

        // What was read so far belongs to the other game's day.
        _ = LoadTodaySessionExtrasAsync();
    }

    [RelayCommand]
    private void BlacklistPendingGame()
    {
        var game = _pendingGame;
        if (game == null) return;

        var entry = BlacklistService.ForDetectedGame(game, _pendingGameDisplayName ?? game.Name);
        _owner.Logger.Info($"[MainWindowViewModel] User action: blacklisted '{entry.DisplayName}' from the confirmation modal. The session is discarded.");

        DiscardSession();
        _owner.AddToBlacklist(entry);
    }

    /// <summary>Drops the remembered game and goes back to what detection found.</summary>
    [RelayCommand]
    private void ForgetRememberedGame()
    {
        var remembered = _remembered;
        if (remembered == null || _closed) return;

        _owner.Logger.Info($"[SessionConfirmation] User action: forgot '{remembered.GameName}' for '{remembered.Details}' from the confirmation modal.");
        _owner.ForgetRememberedGame(remembered);
        // Already replaced from another modal: nothing was removed, but this one still goes back.
        OnRememberedGameRemoved(remembered);
    }

    /// <summary>The entry this modal shows was dropped, here or elsewhere: back to what detection found.</summary>
    internal void OnRememberedGameRemoved(RememberedGame entry)
    {
        if (_closed || IsSavingSession || !ReferenceEquals(_remembered, entry)) return;

        _remembered = null;
        IsGameRemembered = false;
        _detectedGameTaken = false;
        IsGameChangedByHand = false;
        IsRememberGameChosen = false;

        _pendingGameName = _pendingGame?.Name ?? _pendingGameName;
        _pendingIdIgdb = _pendingGame?.IdIgdb;
        _pendingGameUrl = null;
        _pendingCoverUrl = null;
        SessionGameTitle = _pendingGameName ?? string.Empty;
        ResolveGame(null);
    }

    /// <summary>Keeps the game picked for this executable or ROM, if the box asks for it. Only once the session is saved or postponed.</summary>
    private void ApplyRememberChoice()
    {
        if (!IsRememberGameOffered || !IsRememberGameChosen || _pendingGame == null || string.IsNullOrEmpty(_pendingGameName)) return;

        var entry = RememberedGameService.ForDetectedGame(_pendingGame, _pendingGameName, _pendingGameUrl, _pendingCoverUrl);
        if (entry != null) _owner.RememberGame(entry);
    }

    /// <summary>
    /// Fills the confirmation with the note and tags today's play session of the game already has, so they show up to be
    /// kept or edited. Takes them from the read started on detection when it is about the same game and day and worked.
    /// </summary>
    private async Task LoadTodaySessionExtrasAsync(MainWindowViewModel.SessionExtrasPrefetch? prefetch = null)
    {
        int request = ++_sessionExtrasRequest;
        _sessionExtrasKnown = false;
        _sessionExtrasBaseline = null;
        ResetSessionExtras();

        string? gameName = _pendingGameName;
        string? gameUrl = _pendingGameUrl;
        if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(gameUrl)) return;

        bool usable = prefetch != null && prefetch.GameUrl == gameUrl && prefetch.Day == DateTime.Today
            && !(prefetch.Read.IsCompleted && prefetch.Read.Result == null);
        if (!usable && IsOffline) return;

        var read = usable ? prefetch!.Read : _owner.ReadTodaySessionExtrasAsync(gameName, gameUrl);

        IsSessionExtrasLoading = !read.IsCompleted;
        var extras = await read;
        if (request != _sessionExtrasRequest) return;
        IsSessionExtrasLoading = false;

        if (extras == null) return;

        _sessionExtrasKnown = true;
        SessionExtras.Load(extras);
        _sessionExtrasBaseline = SessionExtras.TakeSnapshot();
        IsFillingSessionExtras = true;
        IsSessionNoteOpen = !string.IsNullOrWhiteSpace(extras.Note);
        IsSessionTagsOpen = extras.Tags.Count > 0;
        IsFillingSessionExtras = false;
    }

    // What the fields held once filled, to tell whether the user has touched them since.
    private SessionExtrasViewModel.Snapshot? _sessionExtrasBaseline;

    /// <summary>
    /// The other modal saved the same game's day, so what this one read is stale and saving it would undo that.
    /// Reread unless the user already edited the fields: then their version stands.
    /// </summary>
    internal void OnSameDaySaved()
    {
        if (_closed || IsSavingSession) return;
        if (_sessionExtrasBaseline != null ? !SessionExtras.Matches(_sessionExtrasBaseline) : !SessionExtras.IsEmpty) return;

        _owner.Logger.Info($"[SessionConfirmation] The other modal saved '{_pendingGameName}' today; rereading the note and tags this one shows.");
        _ = LoadTodaySessionExtrasAsync();
    }

    /// <summary>What the save writes as the day's note and tags; null leaves Backloggd's untouched.</summary>
    private PlaySessionExtras? SessionExtrasToSave() =>
        _sessionExtrasKnown || !SessionExtras.IsEmpty ? SessionExtras.ToExtras() : null;

    [RelayCommand]
    private void OpenSessionNote() => IsSessionNoteOpen = true;

    [RelayCommand]
    private void CloseSessionNote()
    {
        IsSessionNoteOpen = false;
        SessionExtras.ClearNote();
    }

    [RelayCommand]
    private void OpenSessionTags() => IsSessionTagsOpen = true;

    [RelayCommand]
    private void CloseSessionTags()
    {
        IsSessionTagsOpen = false;
        SessionExtras.ClearTags();
    }

    private void ResetSessionExtras()
    {
        CloseSessionNote();
        CloseSessionTags();
    }

    [RelayCommand]
    private void DiscardSession() => Close();

    /// <summary>The clock button: keeps the session in the pending list instead of saving or discarding it.</summary>
    [RelayCommand]
    private void PostponeSession()
    {
        if (_closed || !IsPostponeEnabled) return;

        var session = new PendingSession
        {
            Username = _owner.Auth.Username ?? string.Empty,
            GameName = _pendingGameName ?? SessionGameTitle,
            IdIgdb = _pendingIdIgdb,
            GameUrl = string.IsNullOrEmpty(_pendingGameUrl) ? null : _pendingGameUrl,
            CoverUrl = _pendingCoverUrl,
            IsIdentified = IsGameIdentified,
            SaveFailed = _pendingSaveFailed,
            MarkedStarted = IsSessionMarkedStarted,
            MarkedFinished = IsSessionMarkedFinished,
            Extras = SessionExtras.ToExtras(),
            StartedAt = _pendingEndedAt - _pendingSessionDuration,
            EndedAt = _pendingEndedAt,
            Duration = _pendingSessionDuration
        };

        _owner.Logger.Info($"[MainWindowViewModel] User action: left '{session.GameName}' ({PendingSessionViewModel.FormatDuration(session.Duration)}) for later.");
        ApplyRememberChoice();
        _owner.AddPendingSession(session, SessionGameCover);
        _ownsCover = false;

        Close();

        var loc = LocalizationService.Instance;
        _owner.ShowToast(string.Format(loc["Toast_SessionPostponed"], session.GameName), ToastType.Warning, MainWindowViewModel.UndoToastDuration, loc["Toast_ViewPending"], _owner.NavigateToPending);
    }

    /// <summary>The action of the save error toast; only for this session, if it is still waiting.</summary>
    private void PostponeAfterSaveFailure()
    {
        if (_closed || IsSavingSession) return;
        PostponeSession();
    }

    /// <summary>Closes the modal and forgets the session behind it, whatever the user chose.</summary>
    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _sessionExtrasRequest++;
        IsSessionExtrasLoading = false;
        _owner.OnConfirmationClosed(this);
    }

    [RelayCommand]
    private async Task SaveSession()
    {
        if (IsOffline || IsSessionExtrasLoading) return;

        IsSavingSession = true;

        if (!string.IsNullOrEmpty(_pendingGameName))
        {
            var logger = _owner.Logger;
            bool saved = false;
            await _owner.RegistrationGate.WaitAsync();
            try
            {
                await _owner.Browser.RegisterGame(_pendingGameName, _owner.Auth.Cookies, _pendingSessionDuration.Hours, _pendingSessionDuration.Minutes, _pendingGameUrl, markStarted: IsSessionMarkedStarted, markFinished: IsSessionMarkedFinished, extras: SessionExtrasToSave());

                Console.WriteLine($"[SaveSession] Game registered successfully. Reloading data for user: '{_owner.Auth.Username}'");
                logger.Info($"[SaveSession] Session registered on Backloggd: '{_pendingGameName}', {_pendingSessionDuration.Hours}h {_pendingSessionDuration.Minutes}m.");
                ApplyRememberChoice();
                saved = true;
                IsSessionSaved = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error registering game '{_pendingGameName}': {ex.Message}");
                logger.Error($"[SaveSession] Registering '{_pendingGameName}' ({_pendingSessionDuration.Hours}h {_pendingSessionDuration.Minutes}m) on Backloggd failed. The play time was not recorded.", ex);

                _pendingSaveFailed = true;
                await _owner.EnterOfflineModeIfUnreachableAsync("the session could not be saved");
                _owner.ShowToast(_owner.FriendlySaveError(ex), ToastType.Error, MainWindowViewModel.UndoToastDuration, LocalizationService.Instance["Toast_AddToPending"], PostponeAfterSaveFailure);
            }
            finally
            {
                _owner.RegistrationGate.Release();
                if (!saved) IsSavingSession = false;
            }

            if (!saved) return;

            _owner.ReloadJournal();

            // Long enough to see the check; the buttons stay off meanwhile.
            await Task.Delay(SavedCheckHold);
            Close();

            _owner.ShowToast(string.Format(LocalizationService.Instance["Toast_SessionSavedGame"], _pendingGameName), ToastType.Success);
            _owner.OnConfirmationSaved(this);
        }
    }

    private static readonly TimeSpan SavedCheckHold = TimeSpan.FromMilliseconds(500);
}
