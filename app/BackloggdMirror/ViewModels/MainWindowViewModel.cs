using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using BackloggdMirror.Views;
using BackloggdMirror.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Diagnostics;
using System;

using BackloggdMirror.Models;

namespace BackloggdMirror.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<ToastNotificationViewModel> ToastNotifications { get; } = new();

    /// <summary>
    /// Queues a transient toast. Marshalled to the UI thread because callers include background
    /// tasks (registration, database update) that have no idea which thread they are on.
    /// Each toast expires on its own and calls <see cref="RemoveToast"/>.
    /// </summary>
    public void ShowToast(string message, ToastType type, TimeSpan? duration = null)
    {
        Console.WriteLine($"[MainWindowViewModel] ShowToast called: {message}");
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastNotificationViewModel(message, type, RemoveToast, duration);
            ToastNotifications.Add(toast);
        });
    }

    private void RemoveToast(ToastNotificationViewModel toast)
    {
        Console.WriteLine($"[MainWindowViewModel] Removing Toast: {toast.Message}");
        Dispatcher.UIThread.Post(() => ToastNotifications.Remove(toast));
    }

    // Cancels the pending auto-hide when a new message replaces the current one; without it, the
    // earlier timer would hide the newer message partway through.
    private System.Threading.CancellationTokenSource? _bottomMessageCts;

    /// <summary>
    /// Shows the bottom bar, which unlike a toast reports ongoing work. With no
    /// <paramref name="duration"/> the message stays until something replaces or hides it — that is
    /// what lets the database update hold "downloading..." for as long as it takes.
    /// </summary>
    public void ShowBottomMessage(string message, BottomMessageType type = BottomMessageType.None, TimeSpan? duration = null)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            BottomMessageText = message;
            BottomMessageType = type;
            IsBottomMessageVisible = true;

            _bottomMessageCts?.Cancel();

            if (duration.HasValue)
            {
                _bottomMessageCts = new System.Threading.CancellationTokenSource();
                var token = _bottomMessageCts.Token;

                try
                {
                    await Task.Delay(duration.Value, token);
                    if (!token.IsCancellationRequested)
                    {
                        IsBottomMessageVisible = false;
                    }
                }
                catch (TaskCanceledException)
                {
                    // Superseded by a newer message, which now owns the bar.
                }
            }
        });
    }

    public void HideBottomMessage()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _bottomMessageCts?.Cancel();
            IsBottomMessageVisible = false;
        });
    }
    public event Action? RequestFlashWindow;
    public event Action? RequestShowMainWindow;
    public event Action? RequestCloseApplication;


    private readonly IBackloggdAuthService _authService;
    private readonly IBackloggdBrowserService _browserService;
    private readonly IGameDetectionService _gameDetectionService;
    private readonly SettingsService _settingsService;
    private readonly ICredentialStorageService _credentialStorageService;
    private readonly IAppLogger _logger;
    private readonly GameDataService _gameDataService;
    private readonly AutostartService _autostartService;

    [ObservableProperty]
    private bool _isHomeVisible = true;

    [ObservableProperty]
    private bool _isSettingsVisible = false;

    [ObservableProperty]
    private bool _isBottomMessageVisible = false;

    [ObservableProperty]
    private string _bottomMessageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottomMessageLoading))]
    [NotifyPropertyChangedFor(nameof(IsBottomMessageIconVisible))]
    private BottomMessageType _bottomMessageType = BottomMessageType.None;

    public bool IsBottomMessageLoading => BottomMessageType == BottomMessageType.Loading;
    public bool IsBottomMessageIconVisible => BottomMessageType == BottomMessageType.Success || BottomMessageType == BottomMessageType.Warning || BottomMessageType == BottomMessageType.Error;

    // The settings toggles write straight through to SettingsService and persist on every change:
    // there is no "Apply" button, so an unsaved change would be lost silently.
    public bool MinimizeToTray
    {
        get => _settingsService.MinimizeToTray;
        set
        {
            if (_settingsService.MinimizeToTray != value)
            {
                _settingsService.MinimizeToTray = value;
                _settingsService.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _settingsService.StartWithWindows;
        set
        {
            if (_settingsService.StartWithWindows == value) return;

            // The OS is changed first, and the setting only follows if that worked. The other order
            // leaves settings.json claiming an autostart the registry knows nothing about, which is
            // exactly what makes the toggle sit there showing "on" while nothing happens at boot.
            if (!_autostartService.SetEnabled(value))
            {
                ShowToast(LocalizationService.Instance["Toast_StartWithWindowsFailed"], ToastType.Error);

                // The switch has already moved itself; this makes the binding re-read the setting,
                // which never changed, and snap back.
                OnPropertyChanged();
                return;
            }

            _settingsService.StartWithWindows = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }

    // Two properties for one setting: the ComboBox binds to the option object, while the code
    // (and settings.json) work with the language code. OnSelectedLanguageOptionChanged and the
    // SelectedLanguageCode setter keep them in sync in both directions.
    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; } = new()
    {
        new LanguageOptionViewModel("System", "Settings_SyncSystem"),
        new LanguageOptionViewModel("es", "Settings_Spanish"),
        new LanguageOptionViewModel("en", "Settings_English")
    };

    [ObservableProperty]
    private LanguageOptionViewModel? _selectedLanguageOption;

    partial void OnSelectedLanguageOptionChanged(LanguageOptionViewModel? value)
    {
        if (value != null && value.Code != SelectedLanguageCode)
        {
            SelectedLanguageCode = value.Code;
        }
    }

    public string SelectedLanguageCode
    {
        get => _settingsService.Language;
        set
        {
            if (_settingsService.Language != value)
            {
                _settingsService.Language = value;
                _settingsService.Save();
                LocalizationService.Instance.SetLanguage(value);

                // The native tray menu and the update notice are not XAML bindings to [Key], so the
                // language change does not reach them on its own.
                UpdateTrayMenuText();
                RefreshAppUpdateTexts();
                OnPropertyChanged();

                var option = System.Linq.Enumerable.FirstOrDefault(LanguageOptions, x => x.Code == value);
                if (option != null && !object.ReferenceEquals(SelectedLanguageOption, option))
                {
                    SelectedLanguageOption = option;
                }
            }
        }
    }

    // Internal state string, not shown to the user, so it is not localized.
    [ObservableProperty]
    private string _gameStatus = "No Game Running";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterGameCommand))]
    private bool _isLoggedIn = false;

    [ObservableProperty]
    private bool _isGameRunning = false;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _playTime = "00:00:00";

    [ObservableProperty]
    private string _trayMenuActionText = LocalizationService.Instance["Home_PauseSearch"];

    // #### Session Confirmation Properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    private bool _isSessionConfirmationVisible = false;

    partial void OnIsSessionConfirmationVisibleChanged(bool value)
    {
        UpdateTrayMenuText();
    }

    [ObservableProperty]
    private string _sessionGameTitle = string.Empty;

    [ObservableProperty]
    private string _sessionPlayTime = string.Empty;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _sessionGameCover;

    /// <summary>
    /// True when the game has no cover to show, so the slot renders a "No cover" text instead.
    /// </summary>
    [ObservableProperty]
    private bool _isNoCoverPlaceholderVisible;

    // A real cover always wins over the placeholder, whatever set it.
    partial void OnSessionGameCoverChanged(Avalonia.Media.Imaging.Bitmap? value)
    {
        if (value != null) IsNoCoverPlaceholderVisible = false;
    }


    // ############################

    [ObservableProperty]
    private bool _isSessionCoverLoading;

    [ObservableProperty]
    private bool _isSessionTitleLoading;

    // The session awaiting confirmation. It survives here rather than in the detection state because
    // the modal can stay open indefinitely, and the user may still change the game via the picker.
    internal TimeSpan _pendingSessionDuration;
    internal string? _pendingGameName;
    internal string? _pendingIdIgdb;
    internal string? _pendingGameUrl;

    /// <summary>Below this, a session is discarded rather than offered for confirmation.</summary>
    internal TimeSpan _minAllowedSession = TimeSpan.FromMinutes(1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreButtonsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    private bool _isSavingSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreButtonsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(SaveButtonTooltip))]
    private bool _isGameIdentified = true;

    public bool AreButtonsEnabled => !IsSavingSession;

    public bool IsSaveEnabled => !IsSavingSession && IsGameIdentified;

    public string? SaveButtonTooltip => IsGameIdentified
        ? null
        : LocalizationService.Instance["Session_UnidentifiedGame"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesListVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesErrorVisible))]
    private bool _isLastPlayedGamesLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTooltipVisible))]
    private bool _isSessionWarningForcedVisible = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTooltipVisible))]
    [NotifyPropertyChangedFor(nameof(IsRecycleOverlayVisible))]
    private bool _isInfoIconHovered = false;

    public bool IsTooltipVisible => IsSessionWarningForcedVisible || IsInfoIconHovered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecycleOverlayVisible))]
    private bool _isCoverHovered = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecycleOverlayVisible))]
    private bool _isSaveButtonHovered = false;

    public bool IsRecycleOverlayVisible => IsInfoIconHovered || IsCoverHovered || (IsSaveButtonHovered && !IsGameIdentified);

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _gameBackgroundImage;

    [ObservableProperty]
    private bool _isBackgroundImageVisible = false;

    // #### Game Selector Properties
    [ObservableProperty]
    private bool _isGameSelectorVisible = false;

    [ObservableProperty]
    private string _gameSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isGameSearchLoading = false;

    public ObservableCollection<BackloggdMirror.Models.GameSearchResult> GameSearchResults { get; } = new();

    [RelayCommand]
    private void OpenGameSelector()
    {
        IsGameSelectorVisible = true;
        GameSearchQuery = SessionGameTitle; // Default to current pending title
        GameSearchResults.Clear();
        if (!string.IsNullOrWhiteSpace(GameSearchQuery))
        {
            SearchGamesCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void CloseGameSelector()
    {
        IsGameSelectorVisible = false;
    }

    [RelayCommand]
    private async Task SearchGames()
    {
        if (string.IsNullOrWhiteSpace(GameSearchQuery)) return;

        IsGameSearchLoading = true;
        GameSearchResults.Clear();

        try
        {
            var results = await _browserService.SearchGamesAsync(GameSearchQuery);

            foreach (var result in results)
            {
                // Results appear immediately and each cover streams in behind it, so up to 20 image
                // downloads never hold up the grid.
                GameSearchResults.Add(result);
                _ = Task.Run(async () =>
                {
                    if (!string.IsNullOrEmpty(result.CoverUrl))
                    {
                        try
                        {
                            var bitmap = await _browserService.DownloadImageAsync(result.CoverUrl);
                            Dispatcher.UIThread.Post(() =>
                            {
                                result.CoverBitmap = bitmap;

                                // GameSearchResult is a plain model with no change notification, so
                                // assigning the bitmap alone would never reach the view. Reassigning
                                // the slot raises a collection change, which is what redraws the item.
                                var index = GameSearchResults.IndexOf(result);
                                if (index != -1)
                                {
                                    GameSearchResults[index] = result;
                                }
                            });
                        }
                        catch { }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindowViewModel] SearchGames Error: {ex.Message}");
            _logger?.Error("[MainWindowViewModel] The manual game search failed. The picker is left with whatever it had already shown.", ex);
        }
        finally
        {
            IsGameSearchLoading = false;
        }
    }

    [RelayCommand]
    private void SelectRefinedGame(BackloggdMirror.Models.GameSearchResult selectedGame)
    {
        if (selectedGame == null) return;

        SessionGameTitle = selectedGame.Title;
        SessionGameCover = selectedGame.CoverBitmap;
        _pendingGameName = selectedGame.Title;
        _pendingGameUrl = selectedGame.RedirectLink;
        IsGameIdentified = true;

        IsGameSelectorVisible = false;
    }
    // ############################

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesListVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesErrorVisible))]
    private bool _hasLastPlayedGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesListVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesErrorVisible))]
    private bool _hasLastPlayedGamesError;

    public bool IsLastPlayedGamesListVisible => !IsLastPlayedGamesLoading && HasLastPlayedGames && !HasLastPlayedGamesError;

    public bool IsLastPlayedGamesEmptyVisible => !IsLastPlayedGamesLoading && !HasLastPlayedGames && !HasLastPlayedGamesError;

    public bool IsLastPlayedGamesErrorVisible => !IsLastPlayedGamesLoading && HasLastPlayedGamesError;

    public ObservableCollection<BackloggdMirror.Models.JournalEntry> LastPlayedGames { get; } = new();

    internal string? _currentGameName;
    internal uint _currentProcessId;
    internal string? _currentIdIgdb;
    private readonly DispatcherTimer _pollingTimer;
    private readonly DispatcherTimer _displayTimer;
    private readonly Stopwatch _stopwatch;


    public MainWindowViewModel(IGameDetectionService gameDetectionService, IBackloggdAuthService authService, IBackloggdBrowserService browserService, SettingsService settingsService, ICredentialStorageService credentialStorageService, IAppLogger logger, GameDataService? gameDataService = null, AutostartService? autostartService = null)
    {
        _authService = authService;
        _browserService = browserService;
        _gameDetectionService = gameDetectionService;
        _settingsService = settingsService;
        _credentialStorageService = credentialStorageService;
        _logger = logger;
        _gameDataService = gameDataService ?? new GameDataService(logger);
        _autostartService = autostartService ?? new AutostartService(logger);
        _changelogService = new ChangelogService(logger);

        _stopwatch = new Stopwatch();

        _pollingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pollingTimer.Tick += OnPollingTick;
        _pollingTimer.Start();

        _displayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _displayTimer.Tick += OnDisplayTick;

        // Initialize SelectedLanguageOption based on loaded settings
        _selectedLanguageOption = System.Linq.Enumerable.FirstOrDefault(LanguageOptions, x => x.Code == _settingsService.Language) ?? LanguageOptions[0];

        // Fire-and-forget: attempt to download an updated detectable_processed.json
        _ = TryUpdateDetectableGamesAsync();

        // Fire-and-forget check for a newer Apploggd version. It deliberately avoids the bottom bar,
        // which at this very moment is busy with the games database: the notice lives in the sidebar
        // badge and in Settings > About.
        _ = CheckForAppUpdateAsync();
    }



    public MainWindowViewModel() : this(new BackloggdMirror.Services.GameDetectionService(), new BackloggdAuthService(), new BackloggdBrowserService(), new SettingsService(), new CredentialStorageService(new AppLogger()), new AppLogger())
    {
        // Design-time constructor or fallback
    }

    /// <summary>
    /// Attempts to download the latest detectable_processed.json from the remote repository.
    /// This runs silently in the background; connectivity failures are simply logged.
    /// </summary>
    private async Task TryUpdateDetectableGamesAsync()
    {
        try
        {
            var updateService = new DetectableGamesUpdateService(_logger);

            // Show persistent message while updating
            ShowBottomMessage(LocalizationService.Instance["Update_Checking"], BottomMessageType.Loading);

            // Small delay so the user has time to read the initial message
            await Task.Delay(3000);

            var result = await updateService.TryUpdateAsync(msg => ShowBottomMessage(msg, BottomMessageType.Loading));

            string resultMessage = "";

            switch (result)
            {
                case DetectableGamesUpdateResult.Success:
                    Console.WriteLine("[MainWindowViewModel] detectable_processed.json updated successfully. Reloading services...");
                    // Only the reload is logged here: DetectableGamesUpdateService already records
                    // the outcome of every other branch of this switch.
                    _logger?.Info("[MainWindowViewModel] Games database updated. Reloading the in-memory detection and lookup indexes.");
                    resultMessage = LocalizationService.Instance["Update_Success"];
                    _gameDataService.ReloadDatabase();
                    _gameDetectionService.ReloadDatabase();
                    break;
                case DetectableGamesUpdateResult.NotModified:
                    Console.WriteLine("[MainWindowViewModel] detectable_processed.json is already up to date. No reload needed.");
                    resultMessage = LocalizationService.Instance["Update_NotModified"];
                    break;
                case DetectableGamesUpdateResult.NetworkError:
                    Console.WriteLine("[MainWindowViewModel] Could not update detectable_processed.json (network issue). Using existing local copy.");
                    resultMessage = LocalizationService.Instance["Update_NetworkError"];
                    break;
                case DetectableGamesUpdateResult.InvalidContent:
                    Console.WriteLine("[MainWindowViewModel] Downloaded detectable_processed.json was invalid. Using existing local copy.");
                    resultMessage = LocalizationService.Instance["Update_InvalidContent"];
                    break;
                case DetectableGamesUpdateResult.UnexpectedError:
                    Console.WriteLine("[MainWindowViewModel] Unexpected error updating detectable_processed.json. Using existing local copy.");
                    resultMessage = LocalizationService.Instance["Update_UnexpectedError"];
                    break;
            }

            BottomMessageType msgType = result == DetectableGamesUpdateResult.Success || result == DetectableGamesUpdateResult.NotModified
                ? BottomMessageType.Success
                : BottomMessageType.Error;

            if (msgType == BottomMessageType.Error)
            {
                ShowBottomMessage(resultMessage, msgType);
            }
            else
            {
                ShowBottomMessage(resultMessage, msgType, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            // Final safety net - this should never happen as the service handles all exceptions,
            // but we don't want to crash the app over a background update.
            Console.WriteLine($"[MainWindowViewModel] Critical error in TryUpdateDetectableGamesAsync: {ex.Message}");
            _logger?.Error("[MainWindowViewModel] The background games-database update threw past the service's own handling. The app carries on with the local copy.", ex);
            ShowBottomMessage(LocalizationService.Instance["Update_UnexpectedError"], BottomMessageType.Error);
        }
    }

    [RelayCommand]
    private void NavigateToHome()
    {
        IsHomeVisible = true;
        IsSettingsVisible = false;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        IsHomeVisible = false;
        IsSettingsVisible = true;
    }

    #region About / Changelog

    // Assigned in the constructor rather than in the field initializer because it needs _logger:
    // without it, a failure to read the changelog would be silent.
    private readonly ChangelogService _changelogService;

    private string? _appVersion;

    /// <summary>Version shown in Settings &gt; About.</summary>
    public string AppVersion => _appVersion ??= _changelogService.GetAppVersion();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    private bool _isChangelogVisible = false;

    /// <summary>Blurs the main content while any modal is open.</summary>
    public bool IsAnyOverlayVisible => IsSessionConfirmationVisible || IsChangelogVisible || IsClearDataConfirmationVisible || IsNoBrowserWarningVisible;

    public ObservableCollection<BackloggdMirror.Models.ChangelogBlock> ChangelogBlocks { get; } = new();

    /// <summary>True when the changelog could not be read, so the modal can show its error state.</summary>
    [ObservableProperty]
    private bool _isChangelogEmpty = false;

    [RelayCommand]
    private void OpenChangelog()
    {
        // Loaded on first open: the file is embedded and cannot change at runtime.
        if (ChangelogBlocks.Count == 0)
        {
            foreach (var block in _changelogService.LoadBlocks())
            {
                ChangelogBlocks.Add(block);
            }

            IsChangelogEmpty = ChangelogBlocks.Count == 0;
        }

        IsChangelogVisible = true;
    }

    [RelayCommand]
    private void CloseChangelog()
    {
        IsChangelogVisible = false;
    }

    #endregion

    #region Application update

    // Like _changelogService and _appDataResetService: created here instead of injected, to avoid
    // changing the constructor signature.
    private AppUpdateService? _appUpdateService;
    private ExternalLinkService? _externalLinkService;

    private AppUpdateService AppUpdateService => _appUpdateService ??= new AppUpdateService(_logger);
    private ExternalLinkService ExternalLinkService => _externalLinkService ??= new ExternalLinkService(_logger);

    /// <summary>Available release, or null when up to date. Holds the URL "Download" navigates to.</summary>
    private AppUpdateInfo? _availableUpdate;

    /// <summary>Drives both the sidebar badge and the notice in Settings &gt; About.</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable = false;

    [ObservableProperty]
    private string _updateAvailableText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdatePublishedDate))]
    private string _updatePublishedText = string.Empty;

    /// <summary>Hides the date line when GitHub did not supply one, to avoid an empty gap.</summary>
    public bool HasUpdatePublishedDate => !string.IsNullOrEmpty(UpdatePublishedText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    private bool _isNoBrowserWarningVisible = false;

    /// <summary>URL shown in the "no browser" modal so the user can copy it by hand.</summary>
    [ObservableProperty]
    private string _noBrowserUrl = BackloggdMirror.Services.AppUpdateService.ReleasesPageUrl;

    /// <summary>
    /// Checks for a newer version in the background. Started when the ViewModel is built and never
    /// reports failures: with no network, or GitHub not answering, the notice simply stays hidden.
    /// </summary>
    private async Task CheckForAppUpdateAsync()
    {
        var update = await AppUpdateService.CheckForUpdateAsync(AppVersion);

        if (update is null) return;

        // The check can finish on any thread; everything below touches the UI.
        Dispatcher.UIThread.Post(() =>
        {
            _availableUpdate = update;
            NoBrowserUrl = update.ReleaseUrl;
            RefreshAppUpdateTexts();
            IsUpdateAvailable = true;
        });
    }

    /// <summary>
    /// Rebuilds the notice texts. Must be called on a language change: being composed with
    /// string.Format, they do not refresh themselves the way [Key] bindings do.
    /// </summary>
    private void RefreshAppUpdateTexts()
    {
        if (_availableUpdate is null) return;

        var loc = LocalizationService.Instance;

        UpdateAvailableText = string.Format(loc["AppUpdate_Available"], _availableUpdate.Version);

        UpdatePublishedText = _availableUpdate.PublishedAt is { } publishedAt
            ? string.Format(loc["AppUpdate_PublishedOn"],
                            publishedAt.ToLocalTime().ToString(loc["AppUpdate_DateFormat"], loc.CurrentCulture))
            : string.Empty;
    }

    /// <summary>
    /// Opens the release page in the user's browser. When none is available a modal says so, the
    /// same way the data wipe does.
    /// </summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        var url = _availableUpdate?.ReleaseUrl ?? AppUpdateService.ReleasesPageUrl;

        if (!ExternalLinkService.TryOpen(url))
        {
            NoBrowserUrl = url;
            IsNoBrowserWarningVisible = true;
        }
    }

    [RelayCommand]
    private void CloseNoBrowserWarning()
    {
        IsNoBrowserWarningVisible = false;
    }

    #endregion

    #region Data wipe

    // Created here rather than injected via the constructor so existing callers keep working, same
    // as _changelogService.
    private AppDataResetService? _appDataResetService;

    private AppDataResetService AppDataResetService => _appDataResetService ??= new AppDataResetService(_logger);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    private bool _isClearDataConfirmationVisible = false;

    [RelayCommand]
    private void OpenClearDataConfirmation()
    {
        IsClearDataConfirmationVisible = true;
    }

    [RelayCommand]
    private void CloseClearDataConfirmation()
    {
        IsClearDataConfirmationVisible = false;
    }

    /// <summary>
    /// Wipes the stored application data (logs aside) and signs out. On a partial failure the
    /// session is deliberately kept open, so the warning stays visible and the user can retry.
    /// </summary>
    [RelayCommand]
    private void ClearAppData()
    {
        IsClearDataConfirmationVisible = false;

        if (!AppDataResetService.ClearAll())
        {
            ShowToast(LocalizationService.Instance["Toast_ClearDataFailed"], ToastType.Error);
            return;
        }

        // The autostart registry entry is application data too, and it lives outside the data
        // folder: left alone, the app would keep starting with the system even though the setting
        // has gone back to false.
        _autostartService.SetEnabled(false);

        // The settings instance outlives the logout, so it has to be returned to its defaults or it
        // would write the old settings straight back out.
        _settingsService.Reset();
        LocalizationService.Instance.SetLanguage(_settingsService.Language);
        SelectedLanguageOption = System.Linq.Enumerable.FirstOrDefault(LanguageOptions, x => x.Code == _settingsService.Language) ?? LanguageOptions[0];

        // Reset() bypasses the properties the UI is bound to, so the toggles need telling by hand.
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(SelectedLanguageCode));

        Logout();
    }

    #endregion

    // Read by MainWindow.OnClosing/OnClosed to tell a logout apart from a real quit: during a logout
    // the window closes without shutting the app down.
    public bool IsLoggingOut { get; set; } = false;

    /// <summary>
    /// Drops the session and returns to the login window. The service graph is rebuilt from scratch
    /// rather than reused, so no cookie or username from the previous account can leak into the next.
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        IsLoggingOut = true;
        StopTimers();
        _authService.Logout();
        _credentialStorageService.ClearCookies();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Deferred so the click that triggered this finishes processing first: closing the window
            // that is handling the current input event throws "PlatformImpl is null".
            Dispatcher.UIThread.Post(() =>
            {
                var loginWindow = new LoginWindow();

                var newLogger = new AppLogger();
                var newAuthService = new BackloggdAuthService(newLogger);
                var newBrowserService = new BackloggdBrowserService(newLogger);
                var newCredentialStorageService = new CredentialStorageService(newLogger);
                var newInstallService = new PlaywrightInstallService(newLogger);
                var loginVm = new LoginViewModel(newAuthService, newBrowserService, newCredentialStorageService, newLogger, newInstallService);

                // Attach handler for successful login to navigate back to MainWindow
                loginVm.LoginSuccessful += () =>
                {
                    var mainWindowVm = new MainWindowViewModel(_gameDetectionService, newAuthService, newBrowserService, _settingsService, newCredentialStorageService, newLogger);

                    mainWindowVm.IsLoggedIn = true;

                    if (!string.IsNullOrEmpty(loginVm.ResolvedUsername))
                    {
                        _ = mainWindowVm.LoadData(loginVm.ResolvedUsername);
                    }

                    var mainWindow = new MainWindow(newLogger)
                    {
                        DataContext = mainWindowVm
                    };

                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    loginWindow.Close();
                };

                loginWindow.DataContext = loginVm;
                loginWindow.Show();
                desktop.MainWindow?.Close();
                desktop.MainWindow = loginWindow;
            });
        }
    }

    [RelayCommand]
    private void CheckGameStatus()
    {
        if (_gameDetectionService.IsGameRunning(out string gameName, out uint processId, out string? idIgdb))
        {
            _currentGameName = gameName;
            _currentProcessId = processId;
            _currentIdIgdb = idIgdb;
            GameStatus = $"Game Running: {gameName}";
        }
        else
        {
            _currentGameName = null;
            _currentProcessId = 0;
            _currentIdIgdb = null;
            GameStatus = "No Game Running";
        }
        RegisterGameCommand.NotifyCanExecuteChanged();
    }

    private bool CanRegisterGame()
    {
        return IsLoggedIn && !string.IsNullOrEmpty(_currentGameName);
    }

    [RelayCommand]
    private void DiscardSession()
    {
        IsSessionConfirmationVisible = false;
        IsSessionWarningForcedVisible = false;
        SessionGameCover = null;
        IsNoCoverPlaceholderVisible = false;
        IsSessionCoverLoading = false;
        _pendingGameName = null;
        _pendingIdIgdb = null;
        _pendingGameUrl = null;
        _pendingSessionDuration = TimeSpan.Zero;
        IsBackgroundImageVisible = false;
        IsGameIdentified = true;

        // Return to "Waiting for game" state if no game is currently running
        if (!IsGameRunning)
        {
            GameStatus = "No Game Running";
        }

        IsGameDetectionPaused = false;
    }



    [RelayCommand]
    private async Task SaveSession()
    {
        IsSavingSession = true;
        IsSessionWarningForcedVisible = false;

        if (!string.IsNullOrEmpty(_pendingGameName))
        {
            try
            {
                await _browserService.RegisterGame(_pendingGameName, _authService.Cookies, _pendingSessionDuration.Hours, _pendingSessionDuration.Minutes, _pendingGameUrl);

                Console.WriteLine($"[SaveSession] Game registered successfully. Reloading data for user: '{_authService.Username}'");
                _logger?.Info($"[SaveSession] Session registered on Backloggd: '{_pendingGameName}', {_pendingSessionDuration.Hours}h {_pendingSessionDuration.Minutes}m.");

                // Refresh the list after saving
                if (_authService.Username != null)
                {
                    LoadData(_authService.Username);
                }
                else
                {
                    Console.WriteLine($"[SaveSession] WARNING: Username is null, cannot reload data.");
                    _logger?.Warning("[SaveSession] The session was registered but the username is null, so the recently played list could not be refreshed.");
                }

                // Success - Close panel and cleanup
                IsSessionConfirmationVisible = false;
                SessionGameCover = null;
                IsNoCoverPlaceholderVisible = false;
                _pendingGameName = null;
                _pendingIdIgdb = null;
                _pendingGameUrl = null;
                _pendingSessionDuration = TimeSpan.Zero;
                IsBackgroundImageVisible = false;
                IsGameIdentified = true;

                IsGameDetectionPaused = false;
                if (!IsGameRunning)
                {
                    GameStatus = "No Game Running";
                }

                ShowToast(LocalizationService.Instance["Toast_SessionSaved"], ToastType.Success);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error registering game '{_pendingGameName}': {ex.Message}");
                _logger?.Error($"[SaveSession] Registering '{_pendingGameName}' ({_pendingSessionDuration.Hours}h {_pendingSessionDuration.Minutes}m) on Backloggd failed. The play time was not recorded.", ex);

                string friendlyMessage = LocalizationService.Instance["Toast_ErrorSaving"];

                if (ex.Message.Contains("ERR_INTERNET_DISCONNECTED") ||
                    ex.Message.Contains("ERR_NAME_NOT_RESOLVED") ||
                    ex.Message.Contains("ERR_CONNECTION_REFUSED"))
                {
                    friendlyMessage = LocalizationService.Instance["Toast_ConnectionError"];
                }
                else if (ex.Message.Contains("Timeout"))
                {
                    friendlyMessage = LocalizationService.Instance["Toast_TimeoutError"];
                }
                else
                {
                    friendlyMessage = string.Format(LocalizationService.Instance["Toast_UnexpectedError"], ex.Message);
                }

                ShowToast(friendlyMessage, ToastType.Error);
            }
            finally
            {
                IsSavingSession = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegisterGame))]
    private async Task RegisterGame()
    {
        if (!string.IsNullOrEmpty(_currentGameName))
        {
            // Manual registration if needed, though mostly handled by session flow now
            // await _browserService.RegisterGame(_currentGameName, _authService.Cookies);
        }
    }

    public async Task LoadData(string username)
    {
        IsLastPlayedGamesLoading = true;
        HasLastPlayedGamesError = false;
        try
        {
            var games = await _browserService.GetLastPlayedGames(username, _authService.Cookies);
            LastPlayedGames.Clear();
            if (games == null)
            {
                HasLastPlayedGamesError = true;
                HasLastPlayedGames = false;
            }
            else
            {
                foreach (var game in games)
                {
                    if (!string.IsNullOrEmpty(game.CoverImage))
                    {
                        try
                        {
                            game.CoverBitmap = await _browserService.DownloadImageAsync(game.CoverImage);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MainWindowViewModel] Error downloading image for {game.GameName}: {ex.Message}");
                            _logger?.Warning($"[MainWindowViewModel] Could not download the cover for '{game.GameName}' in the recently played list: {ex.Message}. The entry is shown without an image.");
                        }
                    }
                    LastPlayedGames.Add(game);
                }
                HasLastPlayedGames = LastPlayedGames.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindowViewModel] Error loading last played games: {ex.Message}");
            _logger?.Error($"[MainWindowViewModel] Could not load the journal of '{username}'. The recently played list shows its error state.", ex);
            HasLastPlayedGamesError = true;
            HasLastPlayedGames = false;
        }
        finally
        {
            IsLastPlayedGamesLoading = false;
        }
    }



    [RelayCommand]
    private void ReloadRecentlyPlayedGames()
    {
        _logger?.Info("[MainWindowViewModel] User action: reloaded recently played games.");
        if (!string.IsNullOrEmpty(_authService.Username))
        {
            _ = LoadData(_authService.Username);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDetectionActive))]
    private bool _isGameDetectionPaused = false;

    public bool IsGameDetectionActive => !IsGameDetectionPaused;

    [RelayCommand]
    private void RestoreMainWindow()
    {
        RequestShowMainWindow?.Invoke();
    }

    [RelayCommand]
    private void ExitApplication()
    {
        RequestCloseApplication?.Invoke();
    }

    [RelayCommand]
    private void ToggleGameDetection()
    {
        if (IsSessionConfirmationVisible || IsGameRunning) return;

        IsGameDetectionPaused = !IsGameDetectionPaused;

        if (!IsGameDetectionPaused)
        {
            GameStatus = "Resume Search...";
        }
        else
        {
            GameStatus = "Search Paused";
        }
        UpdateTrayMenuText();
    }

    private void UpdateTrayMenuText()
    {
        if (IsSessionConfirmationVisible)
        {
            TrayMenuActionText = LocalizationService.Instance["Tray_WaitingConfirmation"];
        }
        else if (IsGameRunning)
        {
            TrayMenuActionText = string.Format(LocalizationService.Instance["Tray_Playing"], GameName, PlayTime);
        }
        else if (IsGameDetectionPaused)
        {
            TrayMenuActionText = LocalizationService.Instance["Home_ResumeSearch"];
        }
        else
        {
            TrayMenuActionText = LocalizationService.Instance["Home_PauseSearch"];
        }
    }

    /// <summary>
    /// The detection heartbeat, every three seconds. Deliberately asymmetric: with no session running it
    /// scans for a game, but once one is tracked it only checks that specific PID. Re-scanning would
    /// be both wasteful and wrong — a second game launched mid-session must not hijack the timer.
    /// </summary>
    internal void OnPollingTick(object? sender, EventArgs e)
    {
        if (IsGameDetectionPaused) return;

        if (IsGameRunning)
        {
            bool isStillRunning = false;
            if (_currentProcessId != 0)
            {
                try
                {
                    using (var process = Process.GetProcessById((int)_currentProcessId))
                    {
                        if (!process.HasExited)
                        {
                            isStillRunning = true;
                        }
                    }
                }
                catch
                {
                    // GetProcessById throws once the PID is gone, which is the normal way a session
                    // ends: the game was closed.
                }
            }

            if (!isStillRunning)
            {
                StopRunningGame();
            }
        }
        else
        {
            if (_gameDetectionService.IsGameRunning(out string detectedGame, out uint processId, out string? idIgdb))
            {
                StartNewGame(detectedGame, processId, idIgdb);
            }
            else
            {
                // Ensure text is correct when nothing is happening
                string defaultText = IsGameDetectionPaused ? LocalizationService.Instance["Home_ResumeSearch"] : LocalizationService.Instance["Home_PauseSearch"];
                if (TrayMenuActionText != defaultText)
                {
                    TrayMenuActionText = defaultText;
                }
            }
        }
    }

    internal void StartNewGame(string gameName, uint processId, string? idIgdb = null)
    {
        _currentGameName = gameName;
        _currentProcessId = processId;
        _currentIdIgdb = idIgdb;
        GameName = gameName;
        IsGameRunning = true;

        _stopwatch.Restart();
        _displayTimer.Start();
        PlayTime = "00:00:00";
        UpdateTrayMenuText();

        // If the game has an IGDB ID, try to resolve its name and background artwork
        if (!string.IsNullOrEmpty(idIgdb))
        {
            var lookupResult = _gameDataService.LookupByIgdbId(idIgdb);
            if (lookupResult != null)
            {
                // Use the canonical name from the JSON database
                GameName = lookupResult.Name;

                // Download artwork for background if available
                if (!string.IsNullOrEmpty(lookupResult.ArtworkUrl))
                {
                    var artworkUrl = lookupResult.ArtworkUrl;
                    var resolvedName = lookupResult.Name;
                    _ = Task.Run(async () =>
                    {
                        var bitmap = await _browserService.DownloadImageAsync(artworkUrl);
                        if (bitmap != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (IsGameRunning && GameName == resolvedName)
                                {
                                    GameBackgroundImage = bitmap;
                                    IsBackgroundImageVisible = true;
                                }
                            });
                        }
                    });
                }
            }
            else
            {
                // IGDB ID not in local JSON — try API fallback for artwork background
                var capturedIdIgdb = idIgdb;
                var currentGameName = gameName;
                _ = Task.Run(async () =>
                {
                    var apiResult = await _gameDataService.LookupByIgdbIdFromApiAsync(capturedIdIgdb);
                    if (apiResult?.ArtworkUrl != null)
                    {
                        var bitmap = await _browserService.DownloadImageAsync(apiResult.ArtworkUrl);
                        if (bitmap != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (IsGameRunning && GameName == currentGameName)
                                {
                                    GameBackgroundImage = bitmap;
                                    IsBackgroundImageVisible = true;
                                }
                            });
                        }
                    }
                });
            }
        }
    }

    /// <summary>
    /// Closes the session once the tracked process dies, and hands it to the user for confirmation.
    /// Nothing is written to Backloggd from here: the confirmation modal is what triggers the
    /// registration, so an unattended app never posts anything on its own.
    /// </summary>
    internal void StopRunningGame()
    {
        // Snapshot everything before the reset below clears it, since the confirmation modal
        // outlives this method and still needs the values.
        string? gameToRegister = _currentGameName;
        string? currentIdIgdb = _currentIdIgdb;
        TimeSpan elapsed = _stopwatch.Elapsed;
        string finalPlayTime = PlayTime;

        _stopwatch.Stop();
        _displayTimer.Stop();
        IsGameRunning = false;
        _currentGameName = null;
        _currentProcessId = 0;
        _currentIdIgdb = null;
        GameName = string.Empty;
        UpdateTrayMenuText();

        Console.WriteLine($"Play time for '{gameToRegister}': {finalPlayTime}");
        _logger?.Info($"[StopRunningGame] Session ended for '{gameToRegister}'. Play time: {finalPlayTime}.");

        if (!string.IsNullOrEmpty(gameToRegister))
        {
            if (elapsed < _minAllowedSession)
            {
                Console.WriteLine($"[StopRunningGame] Session too short: {elapsed.TotalMinutes} minutes. Min required: {_minAllowedSession.TotalMinutes}");
                _logger?.Warning($"[StopRunningGame] Session discarded: {elapsed.TotalMinutes:F1} min played, {_minAllowedSession.TotalMinutes} min required. Nothing is registered on Backloggd and the user is not asked.");
                ShowToast(LocalizationService.Instance["Toast_SessionTooShort"], ToastType.Warning);
                GameStatus = "No Game Running";
                IsGameDetectionPaused = false;
                IsBackgroundImageVisible = false;
                return;
            }

            _pendingGameName = gameToRegister;
            _pendingIdIgdb = currentIdIgdb;
            _pendingSessionDuration = elapsed;
            SessionGameTitle = gameToRegister;
            SessionPlayTime = finalPlayTime;

            // Detection stays paused for as long as the modal is up: a new game starting now would
            // overwrite the pending session before the user has decided what to do with it.
            IsSessionConfirmationVisible = true;
            IsGameDetectionPaused = true;
            GameStatus = "Session Confirmation...";

            if (!_settingsService.HasSeenSessionDiscardWarningV3)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(100);
                    IsSessionWarningForcedVisible = true;
                });
            }
            else
            {
                IsSessionWarningForcedVisible = false;
            }

            // The app is usually minimised to the tray when a game closes, so the window has to
            // surface and flash — otherwise the confirmation would sit unseen and the session
            // would silently never be logged.
            Console.WriteLine("[StopRunningGame] Invoking RequestFlashWindow and RequestShowMainWindow");
            RequestFlashWindow?.Invoke();
            RequestShowMainWindow?.Invoke();

            // Hide the gameplay background
            IsBackgroundImageVisible = false;

            // Resolve game data from JSON using IGDB ID
            IsNoCoverPlaceholderVisible = false;
            IsSessionCoverLoading = true;
            IsSessionTitleLoading = true;

            if (!string.IsNullOrEmpty(currentIdIgdb))
            {
                var lookupResult = _gameDataService.LookupByIgdbId(currentIdIgdb);
                if (lookupResult != null)
                {
                    // Update game name from JSON
                    SessionGameTitle = lookupResult.Name;
                    _pendingGameName = lookupResult.Name;
                    _pendingGameUrl = lookupResult.BackloggdGameUrl;
                    IsGameIdentified = true;
                    IsSessionTitleLoading = false;

                    // Download cover image
                    if (!string.IsNullOrEmpty(lookupResult.CoverUrl))
                    {
                        var coverUrl = lookupResult.CoverUrl;
                        _ = Task.Run(async () =>
                        {
                            var bitmap = await _browserService.DownloadImageAsync(coverUrl);
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (bitmap != null)
                                {
                                    SessionGameCover = bitmap;
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
                    _logger?.Info($"[StopRunningGame] IGDB ID '{currentIdIgdb}' is not in the local database. Falling back to the API for the cover and the Backloggd link.");
                    var capturedIdIgdb = currentIdIgdb;
                    _ = Task.Run(async () =>
                    {
                        var apiResult = await _gameDataService.LookupByIgdbIdFromApiAsync(capturedIdIgdb);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (apiResult != null)
                            {
                                // API resolved the game — mark as identified
                                _pendingGameUrl = apiResult.BackloggdGameUrl;
                                IsGameIdentified = true;
                                IsSessionTitleLoading = false;

                                // Download cover
                                if (!string.IsNullOrEmpty(apiResult.CoverUrl))
                                {
                                    var coverUrl = apiResult.CoverUrl;
                                    _ = Task.Run(async () =>
                                    {
                                        var bitmap = await _browserService.DownloadImageAsync(coverUrl);
                                        Dispatcher.UIThread.Post(() =>
                                        {
                                            SessionGameCover = bitmap ?? null;
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
                                _logger?.Warning($"[StopRunningGame] The API fallback resolved nothing for IGDB ID '{capturedIdIgdb}'. The session is offered as unidentified, so the user has to pick the game by hand.");
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
                Console.WriteLine($"[StopRunningGame] No IGDB ID for '{gameToRegister}'. Game is unidentified.");
                _logger?.Info($"[StopRunningGame] Detection produced no IGDB ID for '{gameToRegister}'. The session is offered as unidentified, so the user has to pick the game by hand.");
                ShowNoCoverPlaceholder();
                IsSessionCoverLoading = false;
                IsSessionTitleLoading = false;
                IsGameIdentified = false;
                _pendingGameUrl = null;
            }
        }
    }

    /// <summary>
    /// Shows the "No cover" text placeholder in the session cover slot.
    /// </summary>
    private void ShowNoCoverPlaceholder()
    {
        SessionGameCover = null;
        IsNoCoverPlaceholderVisible = true;
    }

    [RelayCommand]
    public void DismissForcedTooltip()
    {
        IsInfoIconHovered = true;

        if (IsSessionWarningForcedVisible)
        {
            IsSessionWarningForcedVisible = false;
            _settingsService.HasSeenSessionDiscardWarningV3 = true;
            _settingsService.Save();
        }
    }

    [RelayCommand]
    public void OnInfoIconExited()
    {
        IsInfoIconHovered = false;
    }

    [RelayCommand]
    public void OnCoverPointerEntered()
    {
        IsCoverHovered = true;
    }

    [RelayCommand]
    public void OnCoverPointerExited()
    {
        IsCoverHovered = false;
    }

    [RelayCommand]
    public void OnSaveButtonPointerEntered()
    {
        IsSaveButtonHovered = true;
    }

    [RelayCommand]
    public void OnSaveButtonPointerExited()
    {
        IsSaveButtonHovered = false;
    }

    private void OnDisplayTick(object? sender, EventArgs e)
    {
        if (IsGameRunning)
        {
            PlayTime = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
            UpdateTrayMenuText();
        }
    }

    private void StopTimers()
    {
        _pollingTimer.Stop();
        _displayTimer.Stop();
        _stopwatch.Stop();
    }
}
