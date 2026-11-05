using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using BackloggdMirror.Views;
using BackloggdMirror.Services;
using BackloggdMirror.Services.Input;
using BackloggdMirror.Services.Platform.Linux;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Diagnostics;
using System;
using System.Linq;

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
    public void ShowToast(string message, ToastType type, TimeSpan? duration = null, string? actionText = null, Action? action = null, string? detail = null)
    {
        Console.WriteLine($"[MainWindowViewModel] ShowToast called: {message}");
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastNotificationViewModel(message, type, RemoveToast, duration, actionText, action, detail);
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

    /// <summary>The tray notice that replaces the update toast on a silent start.</summary>
    public event Action<TrayNotice>? RequestTrayUpdateNotice;

    public event Action? RequestShowUpdateProgress;
    public event Action? RequestCloseUpdateProgress;


    private readonly IBackloggdAuthService _authService;
    private readonly IBackloggdBrowserService _browserService;
    private readonly IGameDetectionService _gameDetectionService;
    private readonly SettingsService _settingsService;
    private readonly ICredentialStorageService _credentialStorageService;
    private readonly IAppLogger _logger;
    private readonly GameDataService _gameDataService;
    private readonly AutostartService _autostartService;
    private readonly BlacklistService _blacklistService;
    private readonly PendingSessionService _pendingSessionService;
    private readonly GamepadService? _gamepadService;

    [ObservableProperty]
    private bool _isHomeVisible = true;

    [ObservableProperty]
    private bool _isSettingsVisible = false;

    [ObservableProperty]
    private bool _isPendingVisible = false;

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

    // On Linux only with a tray host: a window hidden without one could not be brought back.
    public bool CanMinimizeToTray => !OperatingSystem.IsLinux() || LinuxTrayHost.IsAvailable;

    public void NotifyTrayAvailabilityChanged() => OnPropertyChanged(nameof(CanMinimizeToTray));

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

    public bool GamepadNavigationEnabled
    {
        get => _settingsService.GamepadNavigationEnabled;
        set
        {
            if (_settingsService.GamepadNavigationEnabled != value)
            {
                _settingsService.GamepadNavigationEnabled = value;
                _settingsService.Save();
                OnPropertyChanged();
            }
        }
    }

    public bool AlwaysAddToPending
    {
        get => _settingsService.AlwaysAddToPending;
        set
        {
            if (_settingsService.AlwaysAddToPending == value) return;

            // The switch is disabled while the automatic mode is on; this only guards against a stray binding.
            if (value && _settingsService.AutoRegisterSessions)
            {
                OnPropertyChanged();
                return;
            }

            _settingsService.AlwaysAddToPending = value;
            _settingsService.Save();
            _logger.Info($"[MainWindowViewModel] User action: 'Always add to pending' set to {value}.");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutoRegisterSessionsAvailable));
        }
    }

    /// <summary>The automatic mode: finished sessions are registered on Backloggd without the confirmation.</summary>
    public bool AutoRegisterSessions
    {
        get => _settingsService.AutoRegisterSessions;
        set
        {
            if (_settingsService.AutoRegisterSessions == value) return;

            if (value && _settingsService.AlwaysAddToPending)
            {
                OnPropertyChanged();
                return;
            }

            _settingsService.AutoRegisterSessions = value;
            _settingsService.Save();
            _logger.Info($"[MainWindowViewModel] User action: automatic mode set to {value}.");
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAlwaysAddToPendingAvailable));
        }
    }

    public bool AutoUpdateGamesDatabase
    {
        get => _settingsService.AutoUpdateGamesDatabase;
        set
        {
            if (_settingsService.AutoUpdateGamesDatabase == value) return;

            _settingsService.AutoUpdateGamesDatabase = value;
            _settingsService.Save();
            _logger.Info($"[MainWindowViewModel] User action: games database auto-update set to {value}.");
            OnPropertyChanged();
        }
    }

    public bool IsAlwaysAddToPendingAvailable => !AutoRegisterSessions;

    public bool IsAutoRegisterSessionsAvailable => !AlwaysAddToPending;

    /// <summary>Experimental: a second half on the Home that detects and times a game of its own.</summary>
    public bool DualDetectionEnabled
    {
        get => _settingsService.DualDetectionEnabled;
        set
        {
            if (_settingsService.DualDetectionEnabled == value) return;

            // The switch is disabled meanwhile: removing a half would drop its session.
            if (!CanChangeDualDetection)
            {
                OnPropertyChanged();
                return;
            }

            _settingsService.DualDetectionEnabled = value;
            _settingsService.Save();
            _logger.Info($"[MainWindowViewModel] User action: dual detection set to {value}.");
            ApplyDualDetection();
            OnPropertyChanged();
        }
    }

    /// <summary>Only in the idle state, when no half has a session in any of its stages.</summary>
    public bool CanChangeDualDetection => !Slots.Any(s => s.IsBusy);

    public bool IsDualDetectionActive => Slots.Count > 1;

    /// <summary>Adds or removes the second half to match the setting. A new half starts paused.</summary>
    private void ApplyDualDetection()
    {
        if (_settingsService.DualDetectionEnabled && Slots.Count == 1)
        {
            var slot = AddSlot();
            slot.IsUserPaused = true;
        }
        else if (!_settingsService.DualDetectionEnabled && Slots.Count > 1 && !Slots[1].IsBusy)
        {
            var slot = Slots[1];
            slot.PropertyChanged -= OnSlotPropertyChanged;
            Slots.RemoveAt(1);
            slot.ReleaseArtwork();
        }

        foreach (var slot in Slots) slot.IsCompact = Slots.Count > 1;
        OnPropertyChanged(nameof(IsDualDetectionActive));
        UpdateTrayMenuText();
    }

    /// <summary>Shared for the whole process, like the blacklist: it outlives a logout.</summary>
    internal GamepadService? Gamepad => _gamepadService;

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
                RefreshPendingTexts();
                OnPropertyChanged();

                var option = System.Linq.Enumerable.FirstOrDefault(LanguageOptions, x => x.Code == value);
                if (option != null && !object.ReferenceEquals(SelectedLanguageOption, option))
                {
                    SelectedLanguageOption = option;
                }
            }
        }
    }

    [ObservableProperty]
    private bool _isLoggedIn = false;

    /// <summary>The halves of the Home, left first: each detects and times a game of its own.</summary>
    public ObservableCollection<SessionSlotViewModel> Slots { get; } = new();

    /// <summary>The left half, the only one unless the dual detection is on.</summary>
    public SessionSlotViewModel PrimarySlot => Slots[0];

    public bool IsAnyGameRunning => Slots.Any(s => s.IsGameRunning);

    public bool IsAnySessionAutoSaving => Slots.Any(s => s.IsAutoSavingSession);

    private SessionSlotViewModel AddSlot()
    {
        var slot = new SessionSlotViewModel(this, Slots.Count) { IsHomeVisible = IsHomeVisible };
        slot.PropertyChanged += OnSlotPropertyChanged;
        Slots.Add(slot);
        return slot;
    }

    // MainWindow keeps the tray in step through these.
    private void OnSlotPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionSlotViewModel.IsGameRunning):
                OnPropertyChanged(nameof(IsAnyGameRunning));
                OnPropertyChanged(nameof(CanChangeDualDetection));
                break;
            case nameof(SessionSlotViewModel.IsAutoSavingSession):
                OnPropertyChanged(nameof(IsAnySessionAutoSaving));
                OnPropertyChanged(nameof(CanChangeDualDetection));
                break;
            case nameof(SessionSlotViewModel.IsAwaitingConfirmation):
                OnPropertyChanged(nameof(CanChangeDualDetection));
                UpdateTrayMenuText();
                break;
        }
    }

    partial void OnIsHomeVisibleChanged(bool value)
    {
        foreach (var slot in Slots) slot.IsHomeVisible = value;
    }

    // #### Session Confirmation Properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsSessionOverlayVisible))]
    private bool _isSessionConfirmationVisible = false;

    /// <summary>The game picker shares the session overlay, but also opens on its own from the pending view.</summary>
    public bool IsSessionOverlayVisible => IsSessionConfirmationVisible || IsGameSelectorVisible;

    partial void OnIsSessionConfirmationVisibleChanged(bool value)
    {
        UpdateTrayMenuText();
    }

    /// <summary>The confirmations waiting for the user, one per slot whose session ended.</summary>
    public ObservableCollection<SessionConfirmationViewModel> Confirmations { get; } = new();

    /// <summary>Below this, a session is discarded rather than offered for confirmation.</summary>
    internal TimeSpan _minAllowedSession = TimeSpan.FromMinutes(1);

    // What a confirmation needs from the app.
    internal IBackloggdBrowserService Browser => _browserService;
    internal IBackloggdAuthService Auth => _authService;
    internal GameDataService GameData => _gameDataService;
    internal SettingsService Settings => _settingsService;
    internal IAppLogger Logger => _logger;
    internal SemaphoreSlim RegistrationGate => _registrationGate;

    /// <summary>Whatever the user chose: the modal goes and its slot can detect again.</summary>
    internal void OnConfirmationClosed(SessionConfirmationViewModel confirmation)
    {
        if (!Confirmations.Remove(confirmation)) return;

        // A picker left choosing for it would write into a session that is gone.
        if (ReferenceEquals(_selectorConfirmationTarget, confirmation)) CloseGameSelector();

        confirmation.Slot.IsAwaitingConfirmation = false;
        IsSessionConfirmationVisible = Confirmations.Count > 0;
        confirmation.ReleaseCover();
    }

    /// <summary>The same game in the other half: what its modal read of today's note and tags has just been overwritten.</summary>
    internal void OnConfirmationSaved(SessionConfirmationViewModel saved)
    {
        if (string.IsNullOrEmpty(saved.GameUrl)) return;

        foreach (var other in Confirmations.Where(c => c != saved && c.GameUrl == saved.GameUrl).ToList())
        {
            other.OnSameDaySaved();
        }
    }

    partial void OnIsOfflineChanged(bool value)
    {
        foreach (var confirmation in Confirmations) confirmation.IsOffline = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesListVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesErrorVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesOfflineVisible))]
    private bool _isLastPlayedGamesLoading;

    // #### Game Selector Properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsSessionOverlayVisible))]
    private bool _isGameSelectorVisible = false;

    /// <summary>The pending row the picker is choosing for; null when it serves a confirmation.</summary>
    private PendingSessionViewModel? _selectorPendingTarget;

    /// <summary>The confirmation the picker is choosing for.</summary>
    private SessionConfirmationViewModel? _selectorConfirmationTarget;

    // The last one the picker opened for, which keeps the focus there once it closes.
    private SessionConfirmationViewModel? _lastSelectorConfirmation;

    /// <summary>The confirmation the picker serves, or would serve, for the view to put the focus back on it.</summary>
    internal SessionConfirmationViewModel? SelectorConfirmationTarget => _selectorConfirmationTarget
        ?? (_lastSelectorConfirmation != null && Confirmations.Contains(_lastSelectorConfirmation) ? _lastSelectorConfirmation : Confirmations.FirstOrDefault());

    [ObservableProperty]
    private string _gameSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isGameSearchLoading = false;

    public ObservableCollection<BackloggdMirror.Models.GameSearchResult> GameSearchResults { get; } = new();

    internal void OpenGameSelectorFor(SessionConfirmationViewModel confirmation)
    {
        if (IsOffline) return;

        _selectorPendingTarget = null;
        _selectorConfirmationTarget = confirmation;
        _lastSelectorConfirmation = confirmation;
        ShowGameSelector(confirmation.SessionGameTitle);
    }

    [RelayCommand]
    private void ChangePendingSessionGame(PendingSessionViewModel item)
    {
        if (item == null || IsOffline) return;

        _selectorPendingTarget = item;
        ShowGameSelector(item.Title);
    }

    private void ShowGameSelector(string query)
    {
        IsGameSelectorVisible = true;
        GameSearchQuery = query;
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
        _selectorPendingTarget = null;
        _selectorConfirmationTarget = null;
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

        if (_selectorPendingTarget is { } item)
        {
            var session = item.Session;
            session.GameName = selectedGame.Title;
            session.GameUrl = selectedGame.RedirectLink;
            session.IdIgdb = null;
            session.CoverUrl = string.IsNullOrEmpty(selectedGame.CoverUrl) ? null : selectedGame.CoverUrl;
            session.IsIdentified = true;
            _pendingSessionService.Update(session);

            item.CoverBitmap = selectedGame.CoverBitmap;
            item.Refresh();
            UpdatePendingSummary();

            CloseGameSelector();
            return;
        }

        var confirmation = SelectorConfirmationTarget;
        IsGameSelectorVisible = false;
        _selectorConfirmationTarget = null;
        confirmation?.ApplyPickedGame(selectedGame);
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesListVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesEmptyVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesErrorVisible))]
    [NotifyPropertyChangedFor(nameof(IsLastPlayedGamesOfflineVisible))]
    [NotifyPropertyChangedFor(nameof(IsSaveAllPendingEnabled))]
    [NotifyPropertyChangedFor(nameof(SaveAllPendingTooltip))]
    private bool _isOffline;

    public bool IsLastPlayedGamesListVisible => !IsLastPlayedGamesLoading && !IsOffline && HasLastPlayedGames && !HasLastPlayedGamesError;

    public bool IsLastPlayedGamesEmptyVisible => !IsLastPlayedGamesLoading && !IsOffline && !HasLastPlayedGames && !HasLastPlayedGamesError;

    public bool IsLastPlayedGamesErrorVisible => !IsLastPlayedGamesLoading && !IsOffline && HasLastPlayedGamesError;

    public bool IsLastPlayedGamesOfflineVisible => !IsLastPlayedGamesLoading && IsOffline;

    public ObservableCollection<BackloggdMirror.Models.JournalEntry> LastPlayedGames { get; } = new();

    private readonly DispatcherTimer _pollingTimer;
    private readonly DispatcherTimer _displayTimer;

    /// <summary>
    /// Serializes everything that touches the detection services. They keep unsynchronized caches
    /// (exe and name indexes, resolved ids, per-PID emulator state) that used to be safe only
    /// because every caller happened to be the UI thread; the scan now runs off it.
    /// </summary>
    private readonly SemaphoreSlim _detectionGate = new(1, 1);

    // One RegisterGame at a time: two on the same game and day would read the same total and one would overwrite the other.
    private readonly SemaphoreSlim _registrationGate = new(1, 1);

    public MainWindowViewModel(IGameDetectionService gameDetectionService, IBackloggdAuthService authService, IBackloggdBrowserService browserService, SettingsService settingsService, ICredentialStorageService credentialStorageService, IAppLogger logger, GameDataService? gameDataService = null, AutostartService? autostartService = null, BlacklistService? blacklistService = null, GamepadService? gamepadService = null, PendingSessionService? pendingSessionService = null)
    {
        _authService = authService;
        _browserService = browserService;
        _gameDetectionService = gameDetectionService;
        _settingsService = settingsService;
        _credentialStorageService = credentialStorageService;
        _logger = logger;
        _gameDataService = gameDataService ?? new GameDataService(logger);
        _autostartService = autostartService ?? new AutostartService(logger);
        _blacklistService = blacklistService ?? new BlacklistService(logger);
        _gamepadService = gamepadService;
        _pendingSessionService = pendingSessionService ?? new PendingSessionService(logger);
        _changelogService = new ChangelogService(logger);

        RefreshBlacklistEntries();
        LoadPendingSessions();

        AddSlot();
        ApplyDualDetection();

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

        StartConnectionMonitor();

        // Initialize SelectedLanguageOption based on loaded settings
        _selectedLanguageOption = System.Linq.Enumerable.FirstOrDefault(LanguageOptions, x => x.Code == _settingsService.Language) ?? LanguageOptions[0];

        // Fire-and-forget: attempt to download an updated detectable_processed.json
        if (_settingsService.AutoUpdateGamesDatabase)
            _ = TryUpdateDetectableGamesAsync();
        else
            _logger.Info("[MainWindowViewModel] Games database auto-update is off in the settings. Using the local copy.");

        // Fire-and-forget check for a newer Apploggd version. It deliberately avoids the bottom bar,
        // which at this very moment is busy with the games database: the notice lives in the sidebar
        // badge and in Settings > About.
        _ = CheckForAppUpdateAsync();

        _ = CheckDataFolderAccessAsync();

        // Straight after an update, show what changed. Set here rather than on window load because
        // the modal lives in MainWindow's tree, so it is already up when the window first appears.
        if (AppUpdaterService.RestartedAfterUpdate)
        {
            // Consumed rather than just read: a logout builds this ViewModel again in the same
            // process, and a flag left standing would reopen the changelog on every login.
            AppUpdaterService.RestartedAfterUpdate = false;

            _logger.Info("[MainWindowViewModel] Restarted by Velopack after an update; opening the changelog.");
            OpenChangelog();
        }
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

                    // The reload swaps the indexes a detection pass reads, so it waits for one to finish.
                    await _detectionGate.WaitAsync();
                    try
                    {
                        _gameDataService.ReloadDatabase();
                        _gameDetectionService.ReloadDatabase();
                    }
                    finally
                    {
                        _detectionGate.Release();
                    }
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
        IsPendingVisible = false;
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        IsHomeVisible = false;
        IsSettingsVisible = true;
        IsPendingVisible = false;
    }

    [RelayCommand]
    internal void NavigateToPending()
    {
        IsHomeVisible = false;
        IsSettingsVisible = false;
        IsPendingVisible = true;

        // "Today" and "Yesterday" go stale once the clock passes midnight.
        RefreshPendingTexts();
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
    /// <summary>
    /// Included in <see cref="IsAnyOverlayVisible"/> even though the progress window is a window of
    /// its own: the blur belongs to whatever sits behind it, which is this one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOverlayVisible))]
    private bool _isUpdateProgressVisible = false;

    public bool IsAnyOverlayVisible => IsSessionConfirmationVisible || IsGameSelectorVisible || IsChangelogVisible || IsClearDataConfirmationVisible || IsNoBrowserWarningVisible || IsUpdateProgressVisible;

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

    private AppUpdaterService? _appUpdaterService;

    private AppUpdaterService AppUpdater => _appUpdaterService ??= new AppUpdaterService(_logger);

    /// <summary>Available release, or null when up to date. Holds the URL "Download" navigates to.</summary>
    private AppUpdateInfo? _availableUpdate;

    /// <summary>Pending Velopack update, kept so the buttons can apply it without checking again.</summary>
    private Velopack.UpdateInfo? _pendingVelopackUpdate;

    /// <summary>
    /// True from the moment an update starts until the process exits. Guards against a second click,
    /// and tells MainWindow that the close it is about to get is a real one, not a minimise to tray.
    /// </summary>
    public bool IsApplyingUpdate { get; private set; }

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    [ObservableProperty]
    private int _updateProgress;

    /// <summary>Set once downloading is done: applying gives no progress to report.</summary>
    [ObservableProperty]
    private bool _isUpdateProgressIndeterminate;

    /// <summary>Drives both the sidebar badge and the notice in Settings &gt; About.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateNoticeVisible))]
    private bool _isUpdateAvailable = false;

    /// <summary>
    /// True when this copy can install the update itself, which is what turns the notice's button
    /// from "Download" into "Update". Only a Velopack package can.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateNoticeVisible))]
    private bool _isInAppUpdateAvailable = false;

    /// <summary>
    /// The notice shows when either check found a newer version. Deliberately not the same thing as
    /// <see cref="IsUpdateAvailable"/>, which still drives the sidebar badge on its own: an
    /// anonymous rate limit on the GitHub API must not hide a button Velopack knows how to use.
    /// </summary>
    public bool IsUpdateNoticeVisible => IsUpdateAvailable || IsInAppUpdateAvailable;

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

    private bool _windowOpened;

    // Held until the window opens: on a silent start a toast would count down unseen.
    private bool _dataFolderToastPending;

    private async Task CheckDataFolderAccessAsync()
    {
        if (await Task.Run(() => DataFolderAccess.Check(DataFolderAccess.DefaultFolder, _logger))) return;

        if (_windowOpened) ShowDataFolderToast();
        else _dataFolderToastPending = true;
    }

    private void ShowDataFolderToast() =>
        ShowToast(LocalizationService.Instance["Toast_DataFolderNoAccess"], ToastType.Warning, TimeSpan.FromSeconds(15), detail: DataFolderAccess.DefaultFolder);

    public void OnWindowOpened()
    {
        _windowOpened = true;
        if (!_dataFolderToastPending) return;

        _dataFolderToastPending = false;
        ShowDataFolderToast();
    }

    /// <summary>
    /// Checks for a newer version in the background. Started when the ViewModel is built and never
    /// reports failures: with no network, or GitHub not answering, the notice simply stays hidden.
    /// </summary>
    private async Task CheckForAppUpdateAsync()
    {
        // Logged so a build that cannot self-update is obvious from the log alone.
        _logger.Info($"[MainWindowViewModel] In-app updates supported: {AppUpdater.IsSupported} (Velopack version: {AppUpdater.PackagedVersion ?? "n/a"}).");

        // Velopack answers "can this copy update itself", which is a different question from the
        // GitHub notice below: it drives the update button and its toast, nothing else.
        var velopackUpdate = await AppUpdater.CheckForUpdatesAsync();
        if (velopackUpdate is not null)
        {
            var loc = LocalizationService.Instance;
            var message = string.Format(loc["Toast_UpdateAvailable"], velopackUpdate.TargetFullRelease.Version);
            var actionText = loc["AppUpdate_UpdateButton"];

            // Every "Update Apploggd" button applies this same one, so the click needs no re-check.
            _pendingVelopackUpdate = velopackUpdate;

            Dispatcher.UIThread.Post(() =>
            {
                IsInAppUpdateAvailable = true;

                // Fills the notice on its own: the GitHub check below usually overwrites this with
                // the same version, but it is allowed to fail and leave the notice with no text.
                RefreshAppUpdateTexts();
            });

            var version = velopackUpdate.TargetFullRelease.Version.ToString();

            if (AutostartService.StartedSilently)
            {
                // The window exists but was never shown, so a toast would count down unseen.
                if (_settingsService.DismissedUpdateNoticeVersion == version)
                {
                    _logger.Info($"[MainWindowViewModel] Silent start with {version} available, but its tray notice was already dismissed; only the in-app notice remains.");
                }
                else
                {
                    _logger.Info("[MainWindowViewModel] Silent start, so the update is announced from the tray instead of a toast.");
                    Dispatcher.UIThread.Post(() => RequestTrayUpdateNotice?.Invoke(BuildUpdateNotice(version)));
                }
            }
            else
            {
                // 30s rather than the usual 7: it carries an action, so it has to survive the user looking away.
                ShowToast(message, ToastType.Warning, TimeSpan.FromSeconds(30), actionText, RunUpdateCommand);
            }
        }

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
        // Whichever check found it. GitHub's wins when both did, because only it carries a
        // publication date; Velopack's feed has no such field.
        var version = _availableUpdate?.Version ?? _pendingVelopackUpdate?.TargetFullRelease.Version.ToString();
        if (version is null) return;

        var loc = LocalizationService.Instance;

        UpdateAvailableText = string.Format(loc["AppUpdate_Available"], version);

        UpdatePublishedText = _availableUpdate?.PublishedAt is { } publishedAt
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

    /// <summary>Lets the toast and the tray notice fire the same command their buttons bind to.</summary>
    private void RunUpdateCommand() => UpdateApploggdCommand.Execute(null);

    /// <summary>No timer: it stays until the user updates, closes it or opens the window.</summary>
    private TrayNotice BuildUpdateNotice(string version)
    {
        var loc = LocalizationService.Instance;
        return new TrayNotice(TrayNoticeKind.Update, loc["TrayNotice_UpdateTitle"], loc["TrayNotice_UpdateBody"])
        {
            Kicker = "Apploggd",
            Version = version,
            ActionText = loc["TrayNotice_UpdateAction"],
            Action = RunUpdateCommand,
            Dismissed = () =>
            {
                _settingsService.DismissedUpdateNoticeVersion = version;
                _settingsService.Save();
                _logger.Info($"[MainWindowViewModel] Tray notice for {version} dismissed; later silent starts will not show it again.");
            }
        };
    }

    /// <summary>
    /// The notice for a window just hidden in the tray, matching what detection is doing. The
    /// explained version goes first, once, unless a session is waiting: that one asks for action.
    /// </summary>
    public TrayNotice BuildBackgroundNotice()
    {
        var loc = LocalizationService.Instance;
        var title = loc["TrayNotice_BackgroundTitle"];

        if (IsDualDetectionActive && Confirmations.Count > 0) return BuildDualPendingNotice();

        if (Confirmations.FirstOrDefault() is { } confirmation)
        {
            return new TrayNotice(TrayNoticeKind.PendingSession, $"{confirmation.SessionGameTitle} · {confirmation.SessionPlayTime}", loc["TrayNotice_PendingBody"])
            {
                Kicker = loc["TrayNotice_PendingKicker"],
                ActionText = loc["TrayNotice_PendingAction"],
                Action = () => RequestShowMainWindow?.Invoke()
            };
        }

        if (!_settingsService.HasSeenTrayIntro)
        {
            _settingsService.HasSeenTrayIntro = true;
            _settingsService.Save();
            return new TrayNotice(TrayNoticeKind.Intro, title, loc["TrayNotice_IntroBody"]);
        }

        if (IsDualDetectionActive) return BuildDualStatusNotice();

        if (Slots.FirstOrDefault(s => s.IsGameRunning) is { } playing)
        {
            return new TrayNotice(TrayNoticeKind.Playing, string.IsNullOrEmpty(playing.GameName) ? title : playing.GameName,
                loc[_settingsService.AlwaysAddToPending ? "TrayNotice_PlayingBodyPending"
                    : _settingsService.AutoRegisterSessions ? "TrayNotice_PlayingBodyAuto"
                    : "TrayNotice_PlayingBody"])
            {
                LiveKicker = () => string.Format(loc["TrayNotice_PlayingKicker"], playing.PlayTime)
            };
        }

        if (PrimarySlot.IsUserPaused)
        {
            return new TrayNotice(TrayNoticeKind.Paused, title, loc["TrayNotice_PausedBody"]) { Kicker = loc["TrayNotice_PausedKicker"] };
        }

        return new TrayNotice(TrayNoticeKind.Detecting, title, loc["TrayNotice_DetectingBody"]) { Kicker = loc["TrayNotice_DetectingKicker"] };
    }

    /// <summary>Both halves under the title, each with its state.</summary>
    private TrayNotice BuildDualStatusNotice()
    {
        var loc = LocalizationService.Instance;
        bool anyPlaying = Slots.Any(s => s.IsGameRunning);
        string body = anyPlaying
            ? loc[_settingsService.AlwaysAddToPending ? "TrayNotice_PlayingBodyDualPending"
                : _settingsService.AutoRegisterSessions ? "TrayNotice_PlayingBodyDualAuto"
                : "TrayNotice_PlayingBodyDual"]
            : Slots.All(s => s.IsUserPaused) ? loc["TrayNotice_PausedBody"]
            : loc["TrayNotice_DetectingBody"];

        return new TrayNotice(anyPlaying ? TrayNoticeKind.Playing : TrayNoticeKind.Detecting, loc["TrayNotice_BackgroundTitle"], body)
        {
            Rows = Slots.Select(NoticeRow).ToList()
        };
    }

    /// <summary>One or two sessions waiting: which half stays paused, and how the other one is doing.</summary>
    private TrayNotice BuildDualPendingNotice()
    {
        var loc = LocalizationService.Instance;
        Action review = () => RequestShowMainWindow?.Invoke();

        if (Confirmations.Count > 1)
        {
            return new TrayNotice(TrayNoticeKind.PendingSession, string.Empty, loc["TrayNotice_PendingBodyMany"])
            {
                Kicker = string.Format(loc["TrayNotice_PendingKickerMany"], Confirmations.Count),
                Rows = Slots.Select(NoticeRow).ToList(),
                ActionText = loc["TrayNotice_PendingActionMany"],
                Action = review
            };
        }

        var confirmation = Confirmations[0];
        return new TrayNotice(TrayNoticeKind.PendingSession, $"{confirmation.SessionGameTitle} · {confirmation.SessionPlayTime}",
            string.Format(loc["TrayNotice_PendingBodySlot"], confirmation.Slot.Number))
        {
            Kicker = loc["TrayNotice_PendingKicker"],
            TitleSlotIndex = confirmation.Slot.Index,
            Rows = Slots.Where(s => s != confirmation.Slot).Select(NoticeRow).ToList(),
            ActionText = loc["TrayNotice_PendingAction"],
            Action = review
        };
    }

    private TrayNoticeRow NoticeRow(SessionSlotViewModel slot)
    {
        var loc = LocalizationService.Instance;
        if (slot.IsAwaitingConfirmation && Confirmations.FirstOrDefault(c => c.Slot == slot) is { } confirmation)
        {
            return new TrayNoticeRow(slot.Index, TrayNoticeRowState.Pending, confirmation.SessionGameTitle) { Time = confirmation.SessionPlayTime };
        }

        if (slot.IsGameRunning)
        {
            return new TrayNoticeRow(slot.Index, TrayNoticeRowState.Playing, string.Format(loc["TrayNotice_SlotPlaying"], slot.GameName)) { LiveTime = () => slot.PlayTime };
        }

        if (slot.IsAutoSavingSession) return new TrayNoticeRow(slot.Index, TrayNoticeRowState.Saving, loc["Home_SavingSession"]);
        if (slot.IsGameDetectionHeld) return new TrayNoticeRow(slot.Index, TrayNoticeRowState.Paused, loc["TrayNotice_PausedKicker"]);
        return new TrayNoticeRow(slot.Index, TrayNoticeRowState.Detecting, loc["TrayNotice_DetectingKicker"]);
    }

    /// <summary>
    /// Stops detection before an update, so nothing starts a session while the process is being
    /// replaced. A session already being timed is <b>discarded</b>, not registered: the confirmation
    /// modal needs a user and a process, and this one is about to have neither.
    /// Leaves detection paused until the process exits; the pause is not saved, so the restarted copy detects again.
    /// </summary>
    private void SuspendDetectionForUpdate()
    {
        if (!IsAnyGameRunning)
        {
            _logger.Info("[MainWindowViewModel] Update requested; pausing game detection.");
        }

        foreach (var slot in Slots)
        {
            if (slot.IsGameRunning)
            {
                _logger.Info($"[MainWindowViewModel] Update requested while timing '{slot.CurrentGame?.Name}' ({slot.PlayTime}). Discarding the session without registering it.");
                DiscardRunningSession(slot);
            }

            slot.IsUserPaused = true;
        }

        UpdateTrayMenuText();
    }

    /// <summary>
    /// Downloads and applies the pending update, showing the progress window while it runs. Behind
    /// every "Update Apploggd" button: Settings &gt; About, the toast, and the tray notice.
    /// Does not return on success — <see cref="AppUpdaterService.ApplyOnExit"/> replaces the process.
    /// </summary>
    [RelayCommand]
    private async Task UpdateApploggdAsync()
    {
        var loc = LocalizationService.Instance;
        var update = _pendingVelopackUpdate;

        if (update is null)
        {
            _logger.Warning("[MainWindowViewModel] 'Update Apploggd' pressed but no pending update is known. Ignoring.");
            ShowToast(loc["AppUpdate_Failed"], ToastType.Error);
            return;
        }

        if (IsApplyingUpdate)
        {
            _logger.Info("[MainWindowViewModel] 'Update Apploggd' pressed again while an update was already running. Ignoring.");
            return;
        }

        // A session waiting to be confirmed is one the user already played and has not decided about.
        // Restarting now would throw it away silently, and the toast and the tray notice both sit
        // above the modal, so the button really is reachable from here.
        if (IsSessionConfirmationVisible)
        {
            _logger.Warning("[MainWindowViewModel] 'Update Apploggd' pressed while a session is awaiting confirmation. Refusing until it is resolved.");
            ShowToast(loc["AppUpdate_SessionPending"], ToastType.Warning);
            return;
        }

        // Before anything is torn down, or Update.exe would only fail once the app has closed.
        if (!AppUpdater.CanWriteInstallFolder())
        {
            _logger.Warning("[MainWindowViewModel] 'Update Apploggd' pressed but the install folder is read-only. Refusing.");

            // From the tray the window may be hidden, and the toast lives in it.
            RequestShowMainWindow?.Invoke();
            ShowToast(loc["AppUpdate_ReadOnlyFolder"], ToastType.Error, TimeSpan.FromSeconds(15));
            return;
        }

        IsApplyingUpdate = true;
        SuspendDetectionForUpdate();

        var version = update.TargetFullRelease.Version.ToString();
        var isDelta = update.DeltasToTarget.Length > 0;

        _logger.Info($"[MainWindowViewModel] === Update to {version} requested (from {AppUpdater.PackagedVersion}) ===");
        _logger.Info($"[MainWindowViewModel] Target package: {update.TargetFullRelease.FileName}; deltas available: {isDelta}.");

        UpdateProgress = 0;
        UpdateStatusText = string.Format(loc["AppUpdate_Progress_Downloading"], version);
        IsUpdateProgressVisible = true;

        // The window comes back first, so the progress sits over the app instead of the desktop.
        // It is the only thing that undoes a silent start or a minimise to tray, and it is also what
        // makes a failure visible: the toasts below live in MainWindow.
        RequestShowMainWindow?.Invoke();
        RequestShowUpdateProgress?.Invoke();

        // A beat before the download starts, so the window can be read rather than flashing past.
        // The bar runs indeterminate meanwhile: at a fixed 0% it would look stalled, not starting.
        IsUpdateProgressIndeterminate = true;
        await Task.Delay(TimeSpan.FromSeconds(5));
        IsUpdateProgressIndeterminate = false;

        // Logged in 25% steps: the callback fires far too often to put every tick in the log.
        var lastBucket = -1;
        var downloaded = await AppUpdater.DownloadAsync(update, percent =>
        {
            Dispatcher.UIThread.Post(() => UpdateProgress = percent);

            var bucket = percent / 25;
            if (bucket > lastBucket)
            {
                lastBucket = bucket;
                _logger.Info($"[MainWindowViewModel] Update download at {percent}%.");
            }
        });

        if (!downloaded)
        {
            _logger.Error("[MainWindowViewModel] Update download failed. Staying on the installed version.");
            IsApplyingUpdate = false;
            IsUpdateProgressVisible = false;
            RequestCloseUpdateProgress?.Invoke();
            ShowToast(loc["AppUpdate_Failed"], ToastType.Error);
            return;
        }

        UpdateProgress = 100;
        IsUpdateProgressIndeterminate = true;
        UpdateStatusText = loc["AppUpdate_Progress_Applying"];

        // Lets the window paint the "applying" state before the app goes away.
        await Task.Delay(400);

        if (!AppUpdater.ApplyOnExit(update))
        {
            IsApplyingUpdate = false;
            IsUpdateProgressVisible = false;
            RequestCloseUpdateProgress?.Invoke();
            ShowToast(loc["AppUpdate_Failed"], ToastType.Error);
            return;
        }

        _logger.Info("[MainWindowViewModel] Shutting down so Update.exe can replace this copy. The next log lines come from the new version.");

        // Through the lifetime rather than by closing MainWindow: on a silent start that window was
        // never shown, and Update.exe only waits 60s for this process to go away.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            _logger.Warning("[MainWindowViewModel] No desktop lifetime to shut down through; exiting the hard way.");
            Environment.Exit(0);
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
        OnPropertyChanged(nameof(GamepadNavigationEnabled));
        OnPropertyChanged(nameof(AlwaysAddToPending));
        OnPropertyChanged(nameof(AutoRegisterSessions));
        OnPropertyChanged(nameof(AutoUpdateGamesDatabase));
        OnPropertyChanged(nameof(IsAlwaysAddToPendingAvailable));
        OnPropertyChanged(nameof(IsAutoRegisterSessionsAvailable));
        OnPropertyChanged(nameof(DualDetectionEnabled));
        OnPropertyChanged(nameof(SelectedLanguageCode));

        // Same trap as the settings: the file is gone, but detection keeps this instance.
        _blacklistService.Reset();
        RefreshBlacklistEntries();

        _pendingSessionService.Reset();
        LoadPendingSessions();

        Logout();
    }

    #endregion

    #region Blacklist

    /// <summary>Asks the view for an executable to blacklist; null when the user cancels.</summary>
    public event Func<Task<string?>>? RequestPickExecutable;

    internal static readonly TimeSpan UndoToastDuration = TimeSpan.FromSeconds(10);

    /// <summary>Newest first: the entry just added is the one most likely to be looked for.</summary>
    public ObservableCollection<BlacklistEntry> BlacklistEntries { get; } = new();

    [ObservableProperty]
    private bool _hasBlacklistEntries;

    private void RefreshBlacklistEntries()
    {
        BlacklistEntries.Clear();
        foreach (var entry in _blacklistService.Entries.OrderByDescending(e => e.AddedAt))
        {
            BlacklistEntries.Add(entry);
        }

        HasBlacklistEntries = BlacklistEntries.Count > 0;
    }

    /// <summary>Drops the session being timed without registering it or asking the user.</summary>
    private void DiscardRunningSession(SessionSlotViewModel slot)
    {
        slot.Discard();
        UpdateDisplayTimer();
        UpdateTrayMenuText();
    }

    internal void BlacklistRunningGame(SessionSlotViewModel slot)
    {
        var game = slot.CurrentGame;
        if (!slot.IsGameRunning || game == null) return;

        var entry = BlacklistService.ForDetectedGame(game, string.IsNullOrEmpty(slot.GameName) ? game.Name : slot.GameName);
        _logger.Info($"[MainWindowViewModel] User action: blacklisted '{entry.DisplayName}' while timing it ({slot.PlayTime}). The session is discarded.");

        DiscardRunningSession(slot);
        AddToBlacklist(entry);
    }

    [RelayCommand]
    private async Task AddExecutableToBlacklist()
    {
        var pick = RequestPickExecutable;
        if (pick == null) return;

        string? path = await pick();
        if (string.IsNullOrEmpty(path)) return;

        if (OperatingSystem.IsLinux() && !CheckLinuxExecutable(path)) return;

        var entry = BlacklistService.ForExecutable(path);

        foreach (var slot in Slots)
        {
            if (slot.IsGameRunning && slot.CurrentGame is { } game && BlacklistService.Covers(entry, game))
            {
                _logger.Info($"[MainWindowViewModel] '{path}' was blacklisted while it was being timed. Discarding the session.");
                DiscardRunningSession(slot);
            }
        }

        AddToBlacklist(entry);
    }

    /// <summary>The Linux picker offers every file, so what Windows' *.exe filter rules out is caught here.</summary>
    private bool CheckLinuxExecutable(string path)
    {
        string? key = LinuxBlacklistFile.Check(path) switch
        {
            LinuxBlacklistFile.Problem.Script => "Toast_BlacklistScript",
            LinuxBlacklistFile.Problem.NotExecutable => "Toast_BlacklistNotExecutable",
            _ => null
        };

        if (key == null) return true;

        _logger.Info($"[MainWindowViewModel] Refused to blacklist '{path}' ({key}).");
        ShowToast(string.Format(LocalizationService.Instance[key], System.IO.Path.GetFileName(path)), ToastType.Warning);
        return false;
    }

    [RelayCommand]
    private void RemoveFromBlacklist(BlacklistEntry entry)
    {
        if (!_blacklistService.Remove(entry)) return;
        RefreshBlacklistEntries();

        var loc = LocalizationService.Instance;
        ShowToast(string.Format(loc["Toast_BlacklistRemoved"], entry.DisplayName), ToastType.Warning, UndoToastDuration, loc["Toast_Undo"], () =>
        {
            if (_blacklistService.Add(entry)) RefreshBlacklistEntries();
        });
    }

    /// <summary>Undo only reverts the list: the time of a discarded session is not brought back.</summary>
    internal void AddToBlacklist(BlacklistEntry entry)
    {
        var loc = LocalizationService.Instance;

        if (!_blacklistService.Add(entry))
        {
            ShowToast(string.Format(loc["Toast_BlacklistAlreadyListed"], entry.DisplayName), ToastType.Warning);
            return;
        }

        RefreshBlacklistEntries();
        ShowToast(string.Format(loc["Toast_BlacklistAdded"], entry.DisplayName), ToastType.Warning, UndoToastDuration, loc["Toast_Undo"], () =>
        {
            if (_blacklistService.Remove(entry)) RefreshBlacklistEntries();
        });
    }

    #endregion

    #region Pending sessions

    /// <summary>Newest first, and only those of the account signed in.</summary>
    public ObservableCollection<PendingSessionViewModel> PendingSessions { get; } = new();

    [ObservableProperty]
    private bool _hasPendingSessions;

    [ObservableProperty]
    private int _pendingSessionsCount;

    private string? _pendingSessionsUser;

    private void LoadPendingSessions()
    {
        _pendingSessionsUser = _authService.Username;
        PendingSessions.Clear();

        foreach (var session in _pendingSessionService.ForUser(_pendingSessionsUser).OrderByDescending(s => s.EndedAt))
        {
            var item = new PendingSessionViewModel(session, OnPendingMarksChanged, OnPendingExtrasChanged) { IsOffline = IsOffline };
            PendingSessions.Add(item);
            LoadPendingCover(item);
        }

        UpdatePendingSummary();
    }

    internal void AddPendingSession(PendingSession session, Avalonia.Media.Imaging.Bitmap? cover = null)
    {
        _pendingSessionService.Add(session);
        InsertPendingItem(new PendingSessionViewModel(session, OnPendingMarksChanged, OnPendingExtrasChanged) { CoverBitmap = cover, IsOffline = IsOffline }, loadCover: cover == null);
    }

    private void OnPendingExtrasChanged(PendingSessionViewModel item)
    {
        _logger.Info($"[MainWindowViewModel] User action: note and tags of pending session '{item.Title}' changed ({item.Extras.Note.Length}-character note, {item.Extras.Tags.Count} tags).");
        _pendingSessionService.Update(item.Session);
    }

    private void OnPendingMarksChanged(PendingSessionViewModel item)
    {
        _logger.Info($"[MainWindowViewModel] User action: marks of pending session '{item.Title}' set to started: {item.MarkedStarted}, finished: {item.MarkedFinished}.");
        _pendingSessionService.Update(item.Session);
    }

    private void InsertPendingItem(PendingSessionViewModel item, bool loadCover)
    {
        int index = 0;
        while (index < PendingSessions.Count && PendingSessions[index].Session.EndedAt > item.Session.EndedAt) index++;
        PendingSessions.Insert(index, item);

        if (loadCover) LoadPendingCover(item);
        UpdatePendingSummary();
    }

    private void LoadPendingCover(PendingSessionViewModel item)
    {
        var url = item.Session.CoverUrl;
        if (string.IsNullOrEmpty(url)) return;

        item.IsCoverLoading = true;
        _ = Task.Run(async () =>
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try
            {
                bitmap = await _browserService.DownloadImageAsync(url);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[MainWindowViewModel] Could not download the cover of pending session '{item.Title}': {ex.Message}. It is shown without one.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                item.CoverBitmap = bitmap;
                item.IsCoverLoading = false;
            });
        });
    }

    private void UpdatePendingSummary()
    {
        int count = PendingSessions.Count;

        PendingSessionsCount = count;
        HasPendingSessions = count > 0;
    }

    private void RefreshPendingTexts()
    {
        foreach (var item in PendingSessions) item.Refresh();
        UpdatePendingSummary();
    }

    /// <summary>
    /// "Always add to pending": resolves the game the way the modal would, without the user, and
    /// keeps the session for later. Detection is never paused for it.
    /// </summary>
    private async Task AddFinishedSessionToPendingAsync(string gameName, string? idIgdb, DateTime endedAt, TimeSpan duration)
    {
        var session = await ResolveFinishedSessionAsync(gameName, idIgdb, endedAt, duration);

        _logger.Info($"[MainWindowViewModel] 'Always add to pending' is on: '{session.GameName}' ({PendingSessionViewModel.FormatDuration(duration)}) goes to the pending list without asking.");
        AddPendingSession(session);

        var loc = LocalizationService.Instance;
        ShowToast(string.Format(loc["Toast_SessionPostponed"], session.GameName), ToastType.Warning, UndoToastDuration, loc["Toast_ViewPending"], NavigateToPending);
    }

    /// <summary>
    /// The automatic mode: registers the session on the day it ended, without the confirmation. What cannot
    /// be registered (unidentified, offline, a failed save) goes to Pending. Detection is held until the
    /// attempt is over, as it is behind the modal.
    /// </summary>
    private async Task AutoRegisterFinishedSessionAsync(SessionSlotViewModel slot, string gameName, string? idIgdb, DateTime endedAt, TimeSpan duration)
    {
        slot.IsAutoSavingSession = true;
        UpdateTrayMenuText();
        try
        {
            await TryAutoRegisterFinishedSessionAsync(gameName, idIgdb, endedAt, duration);
        }
        finally
        {
            slot.IsAutoSavingSession = false;
            UpdateTrayMenuText();
        }
    }

    private async Task TryAutoRegisterFinishedSessionAsync(string gameName, string? idIgdb, DateTime endedAt, TimeSpan duration)
    {
        var session = await ResolveFinishedSessionAsync(gameName, idIgdb, endedAt, duration);
        var loc = LocalizationService.Instance;
        string played = PendingSessionViewModel.FormatDuration(duration);

        if (!session.IsIdentified || IsOffline)
        {
            _logger.Info($"[MainWindowViewModel] Automatic mode: '{session.GameName}' ({played}) cannot be registered ({(IsOffline ? "offline" : "unidentified")}), so it goes to the pending list.");
            AddPendingSession(session);
            ShowToast(string.Format(loc[IsOffline ? "Toast_AutoOfflineToPending" : "Toast_AutoUnidentifiedToPending"], session.GameName), ToastType.Warning, UndoToastDuration, loc["Toast_ViewPending"], NavigateToPending);
            return;
        }

        await _registrationGate.WaitAsync();
        try
        {
            // TotalHours, not Hours, which wraps at 24. No extras, so the day's note and tags stay as they are.
            await _browserService.RegisterGame(session.GameName, _authService.Cookies, (int)duration.TotalHours, duration.Minutes, session.GameUrl, endedAt.Date);

            _logger.Info($"[MainWindowViewModel] Automatic mode: session registered on Backloggd without asking: '{session.GameName}', {played} on {endedAt:yyyy-MM-dd}.");
            ReloadJournal();
            ShowToast(string.Format(loc["Toast_AutoSessionSaved"], session.GameName, played), ToastType.Success);
        }
        catch (Exception ex)
        {
            _logger.Error($"[MainWindowViewModel] Automatic mode: registering '{session.GameName}' ({played}) on Backloggd failed. It goes to the pending list.", ex);
            session.SaveFailed = true;
            AddPendingSession(session);
            await EnterOfflineModeIfUnreachableAsync("an automatic session could not be saved");
            ShowToast(FriendlySaveError(ex), ToastType.Error, UndoToastDuration, loc["Toast_ViewPending"], NavigateToPending, loc["Toast_AutoSaveFailedToPending"]);
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    /// <summary>Resolves the game the way the modal would, without the user.</summary>
    private async Task<PendingSession> ResolveFinishedSessionAsync(string gameName, string? idIgdb, DateTime endedAt, TimeSpan duration)
    {
        var session = new PendingSession
        {
            Username = _authService.Username ?? string.Empty,
            GameName = gameName,
            IdIgdb = idIgdb,
            StartedAt = endedAt - duration,
            EndedAt = endedAt,
            Duration = duration
        };

        if (!string.IsNullOrEmpty(idIgdb))
        {
            try
            {
                var lookup = _gameDataService.LookupByIgdbId(idIgdb);
                if (lookup != null)
                {
                    session.GameName = lookup.Name;
                }
                else
                {
                    lookup = await _gameDataService.LookupByIgdbIdFromApiAsync(idIgdb);
                }

                if (lookup != null)
                {
                    session.GameUrl = lookup.BackloggdGameUrl;
                    session.CoverUrl = lookup.CoverUrl;
                    session.IsIdentified = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[MainWindowViewModel] Could not resolve IGDB ID '{idIgdb}' for the finished session: {ex.Message}. It is kept as unidentified.");
            }
        }

        return session;
    }

    [RelayCommand]
    private void DiscardPendingSession(PendingSessionViewModel item)
    {
        if (item == null || !_pendingSessionService.Remove(item.Session)) return;

        _logger.Info($"[MainWindowViewModel] User action: discarded pending session '{item.Title}'.");
        PendingSessions.Remove(item);
        UpdatePendingSummary();

        var loc = LocalizationService.Instance;
        ShowToast(string.Format(loc["Toast_PendingDiscarded"], item.Title), ToastType.Warning, UndoToastDuration, loc["Toast_Undo"], () =>
        {
            if (PendingSessions.Contains(item)) return;
            _pendingSessionService.Add(item.Session);
            InsertPendingItem(item, loadCover: false);
        });
    }

    /// <summary>True while any pending row is being saved or waits its turn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveAllPendingEnabled))]
    private bool _isSavingPending;

    /// <summary>Only for "Save all", which shows the spinner until the whole batch is done.</summary>
    [ObservableProperty]
    private bool _isSavingAllPending;

    private int _pendingSavesInFlight;

    private void SetPendingSaveInFlight(bool started)
    {
        _pendingSavesInFlight += started ? 1 : -1;
        IsSavingPending = _pendingSavesInFlight > 0;
    }

    // Concurrent so another row can be queued meanwhile; _registrationGate runs them one by one.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SavePendingSession(PendingSessionViewModel item)
    {
        if (item == null || !item.CanSave || IsOffline) return;

        _logger.Info($"[MainWindowViewModel] User action: save pending session '{item.Title}' ({PendingSessionViewModel.FormatDuration(item.Session.Duration)}, played {item.Session.StartedAt:yyyy-MM-dd}).");

        item.IsSaving = true;
        SetPendingSaveInFlight(true);
        try
        {
            var error = await RegisterPendingAsync(item);
            if (error == null)
            {
                ReloadJournal();
                ShowToast(LocalizationService.Instance["Toast_SessionSaved"], ToastType.Success);
            }
            else
            {
                ShowToast(FriendlySaveError(error), ToastType.Error);
            }
        }
        finally
        {
            item.IsSaving = false;
            SetPendingSaveInFlight(false);
        }
    }

    /// <summary>
    /// One at a time and oldest first; a failure does not stop the rest, and unidentified rows stay.
    /// Every row in the batch is locked from the start, so none can be discarded or changed while it waits.
    /// </summary>
    public bool IsSaveAllPendingEnabled => !IsSavingPending && !IsOffline;

    public string? SaveAllPendingTooltip => IsOffline ? LocalizationService.Instance["Pending_OfflineSaveTooltip"] : null;

    [RelayCommand]
    private async Task SaveAllPendingSessions()
    {
        if (IsSavingPending || IsOffline) return;

        var items = PendingSessions.Where(p => p.CanSave).OrderBy(p => p.Session.StartedAt).ToList();
        _logger.Info($"[MainWindowViewModel] User action: save all pending sessions ({items.Count} identified of {PendingSessions.Count}).");
        if (items.Count == 0) return;

        foreach (var item in items) item.IsSaving = true;
        IsSavingAllPending = true;
        SetPendingSaveInFlight(true);

        int attempted = 0, saved = 0;
        Exception? lastError = null;
        try
        {
            foreach (var item in items)
            {
                // The list is reloaded on an account change.
                if (!PendingSessions.Contains(item)) continue;

                attempted++;
                var error = await RegisterPendingAsync(item);
                if (error == null) saved++;
                else lastError = error;

                item.IsSaving = false;

                // The rest would only fail the same way, each one launching a browser first.
                if (IsOffline) break;
            }
        }
        finally
        {
            foreach (var item in items) item.IsSaving = false;
            IsSavingAllPending = false;
            SetPendingSaveInFlight(false);
        }

        if (saved > 0) ReloadJournal();
        if (attempted == 0) return;

        var loc = LocalizationService.Instance;
        if (saved == attempted)
        {
            ShowToast(saved == 1 ? loc["Toast_SessionSaved"] : string.Format(loc["Pending_SavedAll"], saved), ToastType.Success);
        }
        else if (attempted == 1)
        {
            ShowToast(FriendlySaveError(lastError!), ToastType.Error);
        }
        else
        {
            ShowToast(string.Format(loc["Pending_SavedSome"], saved, attempted), ToastType.Error);
        }
    }

    /// <summary>Registers the row on the day it was played. Returns the error, or null once it is saved and gone from the list.</summary>
    private async Task<Exception?> RegisterPendingAsync(PendingSessionViewModel item)
    {
        var session = item.Session;

        await _registrationGate.WaitAsync();
        try
        {
            // TotalHours, not Hours, which wraps at 24.
            await _browserService.RegisterGame(session.GameName, _authService.Cookies, (int)session.Duration.TotalHours, session.Duration.Minutes, session.GameUrl, session.StartedAt.Date, session.MarkedStarted, session.MarkedFinished, item.Extras.ToExtras());

            _logger.Info($"[MainWindowViewModel] Pending session registered on Backloggd: '{session.GameName}', {PendingSessionViewModel.FormatDuration(session.Duration)} on {session.StartedAt:yyyy-MM-dd}.");
            _pendingSessionService.Remove(session);
            PendingSessions.Remove(item);
            UpdatePendingSummary();
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"[MainWindowViewModel] Registering pending session '{session.GameName}' ({PendingSessionViewModel.FormatDuration(session.Duration)}, {session.StartedAt:yyyy-MM-dd}) on Backloggd failed. It stays in Pending.", ex);
            session.SaveFailed = true;
            item.CommitExtras();
            _pendingSessionService.Update(session);
            item.Refresh();
            await EnterOfflineModeIfUnreachableAsync("a pending session could not be saved");
            return ex;
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    // A reload waiting to start takes in every request made meanwhile; one asked for while a load runs follows it.
    private bool _journalReloadQueued;
    private bool _journalReloadRunning;
    private bool _journalReloadAgain;

    /// <summary>
    /// Refreshes the recently played list after a save. Two saves in a row reload once, after both: each load is a whole
    /// browser, and the first one's list would be stale before it arrived.
    /// </summary>
    internal void ReloadJournal()
    {
        if (_authService.Username == null)
        {
            _logger.Warning("[MainWindowViewModel] A session was registered but the username is null, so the recently played list could not be refreshed.");
            return;
        }

        if (_journalReloadQueued) return;
        if (_journalReloadRunning)
        {
            _journalReloadAgain = true;
            return;
        }

        _journalReloadQueued = true;
        _ = RunJournalReloadAsync();
    }

    private async Task RunJournalReloadAsync()
    {
        try
        {
            do
            {
                _journalReloadAgain = false;
                await WaitForRegistrationsAsync();

                _journalReloadQueued = false;
                _journalReloadRunning = true;
                if (_authService.Username is { } username) await LoadData(username);
                _journalReloadRunning = false;
            }
            while (_journalReloadAgain && !IsLoggingOut);
        }
        finally
        {
            _journalReloadQueued = false;
            _journalReloadRunning = false;
        }
    }

    /// <summary>Until no registration holds or awaits the gate. Its waiters are released in order, so this comes after those already queued.</summary>
    private async Task WaitForRegistrationsAsync()
    {
        do
        {
            await _registrationGate.WaitAsync();
            _registrationGate.Release();

            // A save that was waiting takes the gate right after it is released.
            await Task.Delay(250);
        }
        while (_registrationGate.CurrentCount == 0);
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
    private void Logout() => PerformLogout(null);

    /// <param name="loginMessage">Shown on the login form, as the reason it came back.</param>
    private void PerformLogout(string? loginMessage)
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
                var loginVm = new LoginViewModel(newAuthService, newBrowserService, newCredentialStorageService, newLogger, newInstallService, loginMessage);

                // Attach handler for successful login to navigate back to MainWindow
                loginVm.LoginSuccessful += () =>
                {
                    // The blacklist is the instance detection reads; a new one would show a list that detection ignores.
                    var mainWindowVm = new MainWindowViewModel(_gameDetectionService, newAuthService, newBrowserService, _settingsService, newCredentialStorageService, newLogger, blacklistService: _blacklistService, gamepadService: _gamepadService, pendingSessionService: _pendingSessionService);

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

    /// <summary>A read of today's note and tags started when the game was detected, so the confirmation opens with them.</summary>
    internal sealed record SessionExtrasPrefetch(string GameUrl, DateTime Day, Task<PlaySessionExtras?> Read);

    private void PrefetchTodaySessionExtras(SessionSlotViewModel slot, string gameName, string? gameUrl)
    {
        // Without the confirmation nothing is filled: pending entries start empty and the automatic mode leaves Backloggd's.
        if (IsOffline || _settingsService.AlwaysAddToPending || _settingsService.AutoRegisterSessions || string.IsNullOrEmpty(gameUrl)) return;

        slot.ExtrasPrefetch = new(gameUrl, DateTime.Today, ReadTodaySessionExtrasAsync(gameName, gameUrl));
    }

    /// <summary>Null when they could not be read.</summary>
    // One read at a time: each launches a whole browser, and two games can be detected seconds apart.
    private readonly SemaphoreSlim _extrasReadGate = new(1, 1);

    internal async Task<PlaySessionExtras?> ReadTodaySessionExtrasAsync(string gameName, string gameUrl)
    {
        await _extrasReadGate.WaitAsync();
        try
        {
            return await _browserService.GetTodayPlaySessionExtrasAsync(gameName, _authService.Cookies, gameUrl);
        }
        catch (Exception ex)
        {
            _logger.Warning($"[MainWindowViewModel] Today's note and tags of '{gameName}' could not be read. Saving leaves Backloggd's as they are unless new ones are written. {ex.Message}");
            return null;
        }
        finally
        {
            _extrasReadGate.Release();
        }
    }

    internal string FriendlySaveError(Exception ex)
    {
        var loc = LocalizationService.Instance;

        if (IsOffline)
        {
            return loc["Toast_ConnectionError"];
        }

        if (ex.Message.Contains("ERR_INTERNET_DISCONNECTED") ||
            ex.Message.Contains("ERR_NAME_NOT_RESOLVED") ||
            ex.Message.Contains("ERR_CONNECTION_REFUSED"))
        {
            return loc["Toast_ConnectionError"];
        }

        if (ex.Message.Contains("Timeout"))
        {
            return loc["Toast_TimeoutError"];
        }

        return string.Format(loc["Toast_UnexpectedError"], ex.Message);
    }

    // Bumped per load: two saves in a row reload the list twice, and the older load must not mix its entries in.
    private int _journalLoad;

    public async Task LoadData(string username)
    {
        if (!string.Equals(_pendingSessionsUser, _authService.Username, StringComparison.OrdinalIgnoreCase))
        {
            LoadPendingSessions();
        }

        int load = ++_journalLoad;
        IsLastPlayedGamesLoading = true;
        HasLastPlayedGamesError = false;
        try
        {
            var games = await _browserService.GetLastPlayedGames(username, _authService.Cookies);
            if (load != _journalLoad) return;

            // Their native memory goes now, not whenever the finalizer runs.
            var replaced = LastPlayedGames.ToList();
            LastPlayedGames.Clear();
            foreach (var old in replaced) old.CoverBitmap?.Dispose();
            if (games == null)
            {
                HasLastPlayedGamesError = true;
                HasLastPlayedGames = false;
                await EnterOfflineModeIfUnreachableAsync("the recently played list could not be loaded");
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
                    if (load != _journalLoad)
                    {
                        game.CoverBitmap?.Dispose();
                        return;
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
            if (load != _journalLoad) return;
            HasLastPlayedGamesError = true;
            HasLastPlayedGames = false;
            await EnterOfflineModeIfUnreachableAsync("the recently played list could not be loaded");
        }
        finally
        {
            if (load == _journalLoad) IsLastPlayedGamesLoading = false;
        }
    }



    [RelayCommand]
    private void ReloadRecentlyPlayedGames()
    {
        _logger?.Info("[MainWindowViewModel] User action: reloaded recently played games.");
        if (IsOffline)
        {
            _ = CheckConnectionAsync(manual: true, force: true);
            return;
        }

        if (!string.IsNullOrEmpty(_authService.Username))
        {
            _ = LoadData(_authService.Username);
        }
    }

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

    internal void ToggleSlotDetection(SessionSlotViewModel slot)
    {
        if (slot.IsBusy) return;

        slot.IsUserPaused = !slot.IsUserPaused;
        UpdateTrayMenuText();
    }

    /// <summary>Raised whenever the tray entries may have changed; the native menu has no bindings.</summary>
    public event Action? TrayMenuChanged;

    private void UpdateTrayMenuText()
    {
        var loc = LocalizationService.Instance;
        foreach (var slot in Slots)
        {
            string text = slot.IsAwaitingConfirmation ? loc["Tray_WaitingConfirmation"]
                // Without the time on Linux: GNOME's AppIndicator drops an entry that changes every second, and every one below it.
                : slot.IsGameRunning ? (OperatingSystem.IsLinux() ? string.Format(loc["Tray_PlayingNoTime"], slot.GameName) : string.Format(loc["Tray_Playing"], slot.GameName, slot.PlayTime))
                : slot.IsAutoSavingSession ? loc["Home_SavingSession"]
                : slot.IsUserPaused ? loc["Home_ResumeSearch"]
                : loc["Home_PauseSearch"];

            slot.TrayMenuText = IsDualDetectionActive ? string.Format(loc["Tray_Slot"], slot.Number, text) : text;
        }

        TrayMenuChanged?.Invoke();
    }

    /// <summary>
    /// The detection heartbeat, every three seconds. A slot with a game only checks that game's process; a free
    /// one gets the next game a scan finds, which skips the games already timed so none can take another's timer.
    /// </summary>
    internal void OnPollingTick(object? sender, EventArgs e)
    {
        if (_logoutWhenIdle && !IsSessionInProgress)
        {
            _logoutWhenIdle = false;
            LogoutForExpiredSession();
            return;
        }

        _ = RunDetectionPassAsync();
    }

    /// <summary>
    /// Only the scan leaves the UI thread; the await brings the decision back before anything
    /// observable is touched, which is what keeps <see cref="StartNewGame"/>,
    /// <see cref="StopRunningGame"/> and the timers on the thread they have always run on.
    /// </summary>
    private async Task RunDetectionPassAsync()
    {
        if (!Slots.Any(s => s.IsGameRunning || s.IsDetecting)) return;

        // A slow pass (an API call on a bad connection) skips ticks instead of queueing them up.
        if (!await _detectionGate.WaitAsync(0)) return;

        try
        {
            bool ended = false;
            foreach (var slot in Slots.ToList())
            {
                if (!slot.IsGameRunning) continue;

                var game = slot.CurrentGame;
                if (game == null)
                {
                    StopRunningGame(slot);
                    ended = true;
                    continue;
                }

                bool stillRunning = await Task.Run(() => _gameDetectionService.IsStillRunning(game));

                // The session may have been closed from the tray while the scan was running.
                if (!slot.IsTiming(game)) continue;

                if (!stillRunning)
                {
                    StopRunningGame(slot);
                    ended = true;
                }
            }

            // The pass that ends a session leaves the next scan to the next tick.
            if (ended) return;

            var target = Slots.FirstOrDefault(s => s.IsDetecting);
            if (target == null) return;

            long blacklistVersion = _blacklistService.Version;
            var timed = Slots.Where(s => s.CurrentGame != null).Select(s => s.CurrentGame!.ProcessId).ToList();
            var detected = await Task.Run(() => _gameDetectionService.Detect(timed));

            if (!ReferenceEquals(Slots.FirstOrDefault(s => s.IsDetecting), target)) return;

            // Scanned against a blacklist that has changed since, so it may be the game just blacklisted.
            if (_blacklistService.Version != blacklistVersion) return;

            if (detected != null)
            {
                StartNewGame(target, detected);
            }
            else
            {
                UpdateTrayMenuText();
            }
        }
        catch (Exception ex)
        {
            // Nothing observes this task, so an escaping exception would take the process down.
            _logger?.Error("[MainWindowViewModel] The detection pass failed. Skipping this tick.", ex);
        }
        finally
        {
            _detectionGate.Release();
        }
    }

    internal void StartNewGame(SessionSlotViewModel slot, DetectedGame game)
    {
        string gameName = game.Name;
        string? idIgdb = game.IdIgdb;

        string emulator = game.EmulatorName != null ? $" in {game.EmulatorName}" : string.Empty;
        _logger?.Info($"[StartNewGame] Session started for '{gameName}' (PID {game.ProcessId}, IGDB: {idIgdb ?? "null"}). Detected by {game.Source.TierLabel()}{emulator}.");

        slot.Start(game);
        UpdateDisplayTimer();
        UpdateTrayMenuText();

        // If the game has an IGDB ID, try to resolve its name and background artwork
        if (!string.IsNullOrEmpty(idIgdb))
        {
            var lookupResult = _gameDataService.LookupByIgdbId(idIgdb);
            if (lookupResult != null)
            {
                // Use the canonical name from the JSON database
                slot.GameName = lookupResult.Name;

                PrefetchTodaySessionExtras(slot, lookupResult.Name, lookupResult.BackloggdGameUrl);

                // Download artwork for background if available
                if (!string.IsNullOrEmpty(lookupResult.ArtworkUrl))
                {
                    var artworkUrl = lookupResult.ArtworkUrl;
                    _ = Task.Run(async () =>
                    {
                        var bitmap = await _browserService.DownloadImageAsync(artworkUrl);
                        if (bitmap != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (slot.IsTiming(game)) slot.ShowArtwork(bitmap);
                            });
                        }
                    });
                }
            }
            else
            {
                // IGDB ID not in local JSON — try API fallback for artwork background
                var capturedIdIgdb = idIgdb;
                _ = Task.Run(async () =>
                {
                    var apiResult = await _gameDataService.LookupByIgdbIdFromApiAsync(capturedIdIgdb);
                    if (apiResult?.BackloggdGameUrl != null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (slot.IsTiming(game)) PrefetchTodaySessionExtras(slot, gameName, apiResult.BackloggdGameUrl);
                        });
                    }

                    if (apiResult?.ArtworkUrl != null)
                    {
                        var bitmap = await _browserService.DownloadImageAsync(apiResult.ArtworkUrl);
                        if (bitmap != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (slot.IsTiming(game)) slot.ShowArtwork(bitmap);
                            });
                        }
                    }
                });
            }
        }
    }

    /// <summary>
    /// Closes the session once the tracked process dies, and hands it to the user for confirmation.
    /// Nothing is written to Backloggd from here unless the user turned on the automatic mode:
    /// otherwise the confirmation modal is what triggers the registration.
    /// </summary>
    internal void StopRunningGame(SessionSlotViewModel slot)
    {
        // Snapshot everything before the reset below clears it, since the confirmation modal
        // outlives this method and still needs the values.
        var finishedGame = slot.CurrentGame;
        string finishedDisplayName = slot.GameName;
        string? gameToRegister = finishedGame?.Name;
        string? currentIdIgdb = finishedGame?.IdIgdb;
        TimeSpan elapsed = slot.Elapsed;
        DateTime endedAt = DateTime.Now;
        string finalPlayTime = slot.PlayTime;
        var extrasPrefetch = slot.ExtrasPrefetch;

        slot.Stop();
        UpdateDisplayTimer();
        UpdateTrayMenuText();

        Console.WriteLine($"Play time for '{gameToRegister}': {finalPlayTime}");
        _logger?.Info($"[StopRunningGame] Session ended for '{gameToRegister}'. Play time: {finalPlayTime}.");

        if (!string.IsNullOrEmpty(gameToRegister))
        {
            if (elapsed < _minAllowedSession)
            {
                Console.WriteLine($"[StopRunningGame] Session too short: {elapsed.TotalMinutes} minutes. Min required: {_minAllowedSession.TotalMinutes}");
                _logger?.Warning($"[StopRunningGame] Session discarded: {elapsed.TotalMinutes:F1} min played, {_minAllowedSession.TotalMinutes} min required. Nothing is registered on Backloggd and the user is not asked.");
                ShowToast(string.Format(LocalizationService.Instance["Toast_SessionTooShortGame"], finishedDisplayName), ToastType.Warning);
                return;
            }

            if (_settingsService.AlwaysAddToPending)
            {
                _ = AddFinishedSessionToPendingAsync(gameToRegister, currentIdIgdb, endedAt, elapsed);
                return;
            }

            if (_settingsService.AutoRegisterSessions)
            {
                _ = AutoRegisterFinishedSessionAsync(slot, gameToRegister, currentIdIgdb, endedAt, elapsed);
                return;
            }

            // The confirmation goes first: a picker left open for a pending row would hide it.
            if (_selectorPendingTarget != null) CloseGameSelector();

            var confirmation = new SessionConfirmationViewModel(this, slot, finishedGame, finishedDisplayName, gameToRegister, elapsed, endedAt, finalPlayTime);

            // The slot is held for as long as the modal is up: a new game starting now would
            // overwrite the pending session before the user has decided what to do with it.
            slot.IsAwaitingConfirmation = true;

            // By half, not by arrival: game 1's modal always sits on the left.
            Confirmations.Insert(Confirmations.Count(c => c.Slot.Index < slot.Index), confirmation);
            IsSessionConfirmationVisible = true;

            // The app is usually minimised to the tray when a game closes, so the window has to
            // surface and flash — otherwise the confirmation would sit unseen and the session
            // would silently never be logged.
            Console.WriteLine("[StopRunningGame] Invoking RequestFlashWindow and RequestShowMainWindow");
            RequestFlashWindow?.Invoke();
            RequestShowMainWindow?.Invoke();

            confirmation.Begin(extrasPrefetch);
        }
    }

    private void OnDisplayTick(object? sender, EventArgs e)
    {
        if (!IsAnyGameRunning) return;

        foreach (var slot in Slots) slot.RefreshPlayTime();
        UpdateTrayMenuText();
    }

    // One timer for every slot, running while any of them times a game.
    private void UpdateDisplayTimer()
    {
        if (!IsAnyGameRunning) _displayTimer.Stop();
        else if (!_displayTimer.IsEnabled) _displayTimer.Start();
    }

    private void StopTimers()
    {
        _pollingTimer.Stop();
        _displayTimer.Stop();
        _offlineTimer?.Stop();
        StopConnectionMonitor();
    }

    #region Offline mode

    // Pinging is cheap; validating the session launches a whole browser, so that waits for a ping
    // to answer and then runs no more often than the second interval.
    private static readonly TimeSpan OfflinePingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfflinePageCheckInterval = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _offlineTimer;
    private DateTime _nextOfflinePageCheck;
    private bool _isCheckingConnection;

    /// <summary>
    /// After a failed call to Backloggd: only a failed ping turns it into offline mode, so an
    /// anti-bot screen or a changed selector keeps showing its own error.
    /// </summary>
    internal async Task EnterOfflineModeIfUnreachableAsync(string reason)
    {
        if (IsOffline || IsLoggingOut) return;
        if (await BackloggdConnectivity.IsReachableAsync()) return;

        EnterOfflineMode($"{reason} and backloggd.com does not answer a ping");
    }

    public void EnterOfflineMode(string reason)
    {
        if (IsOffline || IsLoggingOut) return;

        _logger.Warning($"[MainWindowViewModel] Entering offline mode: {reason}.");
        IsOffline = true;
        LastPlayedGames.Clear();
        HasLastPlayedGames = false;
        HasLastPlayedGamesError = false;
        foreach (var item in PendingSessions) item.IsOffline = true;

        _onlinePingTimer?.Stop();
        _failedPings = 0;

        _nextOfflinePageCheck = DateTime.MinValue;
        if (_offlineTimer == null)
        {
            _offlineTimer = new DispatcherTimer { Interval = OfflinePingInterval };
            _offlineTimer.Tick += (_, _) => _ = CheckConnectionAsync(manual: false);
        }
        _offlineTimer.Start();
    }

    private void ExitOfflineMode()
    {
        _offlineTimer?.Stop();
        _failedPings = 0;
        if (_onlinePingTimer != null)
        {
            _onlinePingTimer.Interval = OnlinePingInterval;
            _onlinePingTimer.Start();
        }
        IsOffline = false;
        foreach (var item in PendingSessions) item.IsOffline = false;
        ReloadJournal();
    }

    /// <summary>
    /// A ping first, then a real page load: only Backloggd's logged-in page ends offline mode, so a
    /// captive portal or an anti-bot screen that answers the ping does not count as being back.
    /// </summary>
    /// <param name="manual">The reload button: shows the spinner.</param>
    /// <param name="force">Skips the wait between page checks (manual retries and network changes).</param>
    private async Task CheckConnectionAsync(bool manual, bool force = false)
    {
        if (_isCheckingConnection || !IsOffline) return;
        _isCheckingConnection = true;
        if (manual) IsLastPlayedGamesLoading = true;

        try
        {
            if (!await BackloggdConnectivity.IsReachableAsync()) return;
            if (!manual && !force && DateTime.UtcNow < _nextOfflinePageCheck) return;
            _nextOfflinePageCheck = DateTime.UtcNow + OfflinePageCheckInterval;

            _logger.Info("[MainWindowViewModel] backloggd.com answers a ping again. Validating the session with a page load.");
            var result = await Task.Run(() => _authService.CheckSessionAsync());
            if (!IsOffline || IsLoggingOut) return;

            switch (result.Status)
            {
                case SessionCheckStatus.Valid:
                    _logger.Info("[MainWindowViewModel] Backloggd is reachable and the session is valid. Leaving offline mode.");
                    ExitOfflineMode();
                    break;

                case SessionCheckStatus.Expired:
                    _offlineTimer?.Stop();
                    LogoutForExpiredSession();
                    break;

                default:
                    _logger.Info($"[MainWindowViewModel] The ping answered but Backloggd did not serve its page. Staying offline; next page check after {_nextOfflinePageCheck.ToLocalTime():HH:mm:ss}.");
                    break;
            }
        }
        finally
        {
            _isCheckingConnection = false;
            // Once back online the journal reload owns the spinner.
            if (manual && IsOffline) IsLastPlayedGamesLoading = false;
        }
    }

    // Online, a ping per minute catches a connection that drops while nothing talks to Backloggd. One
    // lost ping is not enough: a second one follows sooner, and only two failures in a row go offline.
    private static readonly TimeSpan OnlinePingInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OnlinePingRetryInterval = TimeSpan.FromSeconds(10);

    // Network changes arrive in bursts while an adapter comes up (address, DNS, routes); this waits for them to settle.
    private static readonly TimeSpan NetworkChangeSettleDelay = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _onlinePingTimer;
    private DispatcherTimer? _networkChangeTimer;
    private int _failedPings;
    private bool _isPinging;

    private void StartConnectionMonitor()
    {
        _onlinePingTimer = new DispatcherTimer { Interval = OnlinePingInterval };
        _onlinePingTimer.Tick += (_, _) => _ = PingWhileOnlineAsync();
        _onlinePingTimer.Start();

        _networkChangeTimer = new DispatcherTimer { Interval = NetworkChangeSettleDelay };
        _networkChangeTimer.Tick += (_, _) => OnNetworkSettled();

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    // The events are static: a view model left subscribed after a logout would keep reacting.
    private void StopConnectionMonitor()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _onlinePingTimer?.Stop();
        _networkChangeTimer?.Stop();
    }

    // Both raised on a pool thread. They only bring the next check forward: the ping always decides.
    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RestartNetworkChangeTimer);

    private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => Dispatcher.UIThread.Post(RestartNetworkChangeTimer);

    private void RestartNetworkChangeTimer()
    {
        if (IsLoggingOut || _networkChangeTimer == null) return;
        _networkChangeTimer.Stop();
        _networkChangeTimer.Start();
    }

    private void OnNetworkSettled()
    {
        _networkChangeTimer?.Stop();
        if (IsLoggingOut) return;

        _logger.Info($"[MainWindowViewModel] The network changed; checking the connection ({(IsOffline ? "offline" : "online")}).");
        if (IsOffline) _ = CheckConnectionAsync(manual: false, force: true);
        else _ = PingWhileOnlineAsync();
    }

    private async Task PingWhileOnlineAsync()
    {
        if (IsOffline || IsLoggingOut || _isPinging) return;
        _isPinging = true;

        try
        {
            bool reachable = await BackloggdConnectivity.IsReachableAsync();
            if (IsOffline || IsLoggingOut || _onlinePingTimer == null) return;

            if (reachable)
            {
                _failedPings = 0;
                _onlinePingTimer.Interval = OnlinePingInterval;
                return;
            }

            _failedPings++;
            if (_failedPings >= 2)
            {
                EnterOfflineMode("backloggd.com did not answer two pings in a row");
            }
            else
            {
                _logger.Info($"[MainWindowViewModel] backloggd.com did not answer a ping. Trying again in {OnlinePingRetryInterval.TotalSeconds:0} s before going offline.");
                _onlinePingTimer.Interval = OnlinePingRetryInterval;
            }
        }
        finally
        {
            _isPinging = false;
        }
    }

    // Set when the session expires mid-game: the logout waits so the session can still go to Pending.
    private bool _logoutWhenIdle;

    private bool IsSessionInProgress => IsAnyGameRunning || IsSessionConfirmationVisible || IsSavingPending;

    /// <summary>
    /// Back to the login with the expired notice. Offline mode stays on until then, which keeps Save
    /// disabled: with dead cookies it could only fail.
    /// </summary>
    private void LogoutForExpiredSession()
    {
        if (IsSessionInProgress)
        {
            _logger.Warning("[MainWindowViewModel] Backloggd is reachable but the saved session has expired. Logging out once the current session is resolved.");
            _logoutWhenIdle = true;
            return;
        }

        _logger.Warning("[MainWindowViewModel] Backloggd is reachable but the saved session has expired. Logging out.");
        PerformLogout(LocalizationService.Instance["Login_Status_SessionExpired"]);
    }

    #endregion
}
