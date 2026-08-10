using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackloggdMirror.Services;
using System.Threading.Tasks;
using System;


namespace BackloggdMirror.ViewModels
{
    /// <summary>
    /// Runs three sequential startup phases in a single window instead of separate dialogs:
    /// resolve a usable browser, restore a saved session, and fall back to the credentials form.
    /// All of them end the same way: <see cref="ResolvedUsername"/> set, then
    /// <see cref="LoginSuccessful"/> raised.
    /// </summary>
    public partial class LoginViewModel : ViewModelBase
    {
        private readonly IBackloggdAuthService _authService;
        private readonly IBackloggdBrowserService _browserService;
        private readonly ICredentialStorageService _credentialStorageService;
        private readonly IAppLogger _logger;
        private readonly IBrowserProvisioner _installService;

        // Drops progress messages emitted after their phase has closed, which would otherwise stay
        // stuck on screen (most visible on a first run, where no later phase overwrites them).
        //
        // Ordering is already handled by the Dispatcher: progress and closure are both queued with
        // Post at the same priority, and equal priorities run in queueing order, so the closure runs
        // last. Hence the closures must NOT use Invoke — its Send priority overtakes the progress
        // messages already queued, which is exactly how the bug appeared. This flag only covers a
        // progress message genuinely emitted late.
        //
        // UI thread only, so no synchronization needed.
        private bool _acceptProgress;

        [ObservableProperty]
        private string _username = string.Empty;

        [ObservableProperty]
        private string _password = string.Empty;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanLogin))]
        private bool _isBusy = false;

        // Persists cookies, never the password.
        [ObservableProperty]
        private bool _isRememberMeChecked = false;

        // Despite the name, this covers every unattended startup phase: browser resolution and
        // download included, not just the session check.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUiVisible))]
        private bool _isCheckingSession = false;

        // True when the browser could not be installed; login is not possible until resolved.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanLogin))]
        private bool _isBrowserUnavailable = false;

        // True while the "a browser is required" prompt is up, waiting for the user to decide.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUiVisible))]
        private bool _isBrowserPromptVisible = false;

        // The form is hidden rather than covered, so Tab focus and the Login button's IsDefault
        // binding cannot reach it while another phase owns the window.
        public bool IsUiVisible => !IsCheckingSession && !IsBrowserPromptVisible;

        public bool CanLogin => !IsBusy && !IsBrowserUnavailable;

        // Wired by the view. LoginWindow.OnClosed turns this into an app shutdown while this is
        // still the main window.
        public Action? RequestClose { get; set; }

        public event Action? LoginSuccessful;

        /// <summary>
        /// Raised when the flow has gone as far as it can without the user: no browser, an expired
        /// session, or no saved session at all. A silent start keeps this window hidden, so without
        /// this event those dead ends would leave the app running with nothing on screen.
        /// </summary>
        public event Action? UserInputRequired;

        // Canonical username as Backloggd spells it, which need not match what was typed.
        public string ResolvedUsername { get; private set; } = string.Empty;



        public LoginViewModel(IBackloggdAuthService authService, IBackloggdBrowserService browserService, ICredentialStorageService credentialStorageService, IAppLogger logger, IBrowserProvisioner installService)
        {
            _authService = authService;
            _browserService = browserService;
            _credentialStorageService = credentialStorageService;
            _logger = logger;
            _installService = installService;

            EnsureBrowserThenCheckSession();
        }

        /// <summary>
        /// Startup gate: both session restore and manual login drive a browser, so one has to be
        /// resolved first. Nothing is downloaded without asking — with no browser available this
        /// flow stops here and resumes from <see cref="AcceptBrowserDownload"/>.
        /// </summary>
        private void EnsureBrowserThenCheckSession()
        {
            IsBusy = true;
            IsCheckingSession = true;
            IsBrowserPromptVisible = false;
            _acceptProgress = true;
            StatusMessage = LocalizationService.Instance["Browser_Install_Checking"];

            Task.Run(async () =>
            {
                BrowserResolution resolution;
                try
                {
                    resolution = await _installService.ResolveBrowserAsync(PostProgress);
                }
                catch (Exception ex)
                {
                    _logger.Error("[LoginViewModel] Browser resolution threw unexpectedly.", ex);
                    resolution = BrowserResolution.NoBrowserAvailable;
                }
                // Post, not Invoke: see the note on _acceptProgress. Nothing awaits this closure
                // either, so Invoke would block a pool thread for nothing.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _acceptProgress = false;

                    if (resolution == BrowserResolution.NoBrowserAvailable)
                    {
                        // Hand over to the user. Nothing is left awaiting: this Task ends here and
                        // the flow resumes from whichever modal button gets pressed.
                        _logger.Info("[LoginViewModel] No browser available. Asking the user before downloading.");
                        IsBusy = false;
                        IsCheckingSession = false;
                        StatusMessage = string.Empty;
                        IsBrowserPromptVisible = true;
                        UserInputRequired?.Invoke();
                        return;
                    }

                    ContinueAfterBrowserReady();
                });
            });
        }

        /// <summary>
        /// Marshals a progress message to the UI thread, discarding it if its phase already closed
        /// (see <see cref="_acceptProgress"/>).
        /// </summary>
        private void PostProgress(string message)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_acceptProgress) StatusMessage = message;
            });
        }

        /// <summary>
        /// Single resumption point once a browser is usable, reached from the startup gate and from
        /// the download prompt alike.
        /// </summary>
        private void ContinueAfterBrowserReady()
        {
            IsBusy = false;
            IsCheckingSession = false;
            IsBrowserPromptVisible = false;
            StatusMessage = string.Empty;
            CheckSavedSession();
        }

        /// <summary>
        /// Prompt's "Accept": downloads Playwright's Chromium, as the app did before this prompt existed.
        /// </summary>
        [RelayCommand]
        private void AcceptBrowserDownload()
        {
            // Guards against a double click or a second Enter queueing two downloads.
            if (!IsBrowserPromptVisible) return;

            _logger.Info("[LoginViewModel] User accepted the Chromium download.");
            IsBrowserPromptVisible = false;
            IsBusy = true;
            IsCheckingSession = true;
            _acceptProgress = true;
            StatusMessage = LocalizationService.Instance["Browser_Install_Downloading"];

            Task.Run(async () =>
            {
                var result = await _installService.EnsureChromiumInstalledAsync(PostProgress);

                // Post, not Invoke: same reason as in EnsureBrowserThenCheckSession.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _acceptProgress = false;

                    if (result == BrowserInstallResult.Failed)
                    {
                        IsBrowserUnavailable = true;
                        StatusMessage = LocalizationService.Instance["Browser_Install_Failed"];
                        IsBusy = false;
                        IsCheckingSession = false;
                        return;
                    }

                    BrowserLaunch.Configure(BrowserSelection.Bundled);
                    ContinueAfterBrowserReady();
                });
            });
        }

        /// <summary>
        /// Prompt's "Close": quits the app, since it cannot do anything useful without a browser.
        /// </summary>
        [RelayCommand]
        private void CloseFromBrowserPrompt()
        {
            _logger.Info("[LoginViewModel] User declined the Chromium download. Shutting down.");
            IsBrowserPromptVisible = false;

            if (RequestClose != null)
            {
                // LoginWindow.OnClosed turns this into desktop.Shutdown().
                RequestClose.Invoke();
                return;
            }

            // Safety net: with ShutdownMode.OnExplicitShutdown, ending up with no window and no
            // Shutdown() leaves an invisible zombie process holding the single-instance mutex,
            // which then blocks the user from reopening the app at all.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }

        /// <summary>
        /// Skips the form when a "Remember me" session is still valid. Cookies alone cannot be
        /// trusted — they expire server-side with nothing written to disk — so validity is decided
        /// by actually loading Backloggd and seeing whether it answers with a logged-in page.
        /// Falls through to the form on failure; no saved session at all is not an error.
        /// </summary>
        private void CheckSavedSession()
        {
            var cookies = _credentialStorageService.LoadCookies();
            if (cookies != null && cookies.Count > 0)
            {
                foreach (var c in cookies)
                {
                    _authService.Cookies.Add(c);
                }

                IsBusy = true;
                IsCheckingSession = true;
                StatusMessage = LocalizationService.Instance["Login_Status_Restoring"];

                Task.Run(async () =>
                {
                    try
                    {
                        var username = await _authService.ResolveUsernameFromSession();
                        if (!string.IsNullOrEmpty(username))
                        {
                            // Success!
                            _logger.Info($"[LoginViewModel] Session restored successfully for user: {username}. User is already logged in.");
                            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                            {
                                // Assigned inside the UI thread callback: LoginSuccessful subscribers
                                // read it the moment the event fires, so it must be set BEFORE the
                                // line below.
                                ResolvedUsername = username;
                                IsBusy = false;
                                IsCheckingSession = false;
                                LoginSuccessful?.Invoke();
                            });
                        }
                        else
                        {
                            // Cookies invalid or expired
                            _logger.Info("[LoginViewModel] Saved session was invalid or expired.");
                            _credentialStorageService.ClearCookies();
                            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                            {
                                StatusMessage = LocalizationService.Instance["Login_Status_SessionExpired"];
                                IsBusy = false;
                                IsCheckingSession = false;
                                UserInputRequired?.Invoke();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("[LoginViewModel] Failed to restore session.", ex);
                        System.Diagnostics.Debug.WriteLine($"Failed to restore session: {ex.Message}");
                        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                       {
                           StatusMessage = "";
                           IsBusy = false;
                           IsCheckingSession = false;
                           UserInputRequired?.Invoke();
                       });
                    }
                });
            }
            else
            {
                // Nothing saved to restore, so the form is the whole flow from here.
                UserInputRequired?.Invoke();
            }
        }

        [RelayCommand]
        private async Task Login()
        {
            System.Diagnostics.Debug.WriteLine($"[LoginViewModel] Username: '{Username}'");

            if (IsBrowserUnavailable)
            {
                StatusMessage = LocalizationService.Instance["Browser_Install_Failed"];
                return;
            }

            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                StatusMessage = LocalizationService.Instance["Login_Status_EnterCredentials"];
                return;
            }

            IsBusy = true;
            StatusMessage = LocalizationService.Instance["Login_Status_LoggingIn"];

            try
            {
                // Driven through a real browser, not HttpClient: Backloggd's anti-bot layer serves
                // a block page to plain HTTP clients.
                var (resolvedUsername, cookies, errorReason) = await _browserService.LoginAsync(Username, Password, IsRememberMeChecked);

                if (!string.IsNullOrEmpty(resolvedUsername) && cookies != null)
                {
                    ResolvedUsername = resolvedUsername;
                    _authService.SetUsername(resolvedUsername);

                    // The auth service is the process-wide holder of the session; every other
                    // service reads its cookies from there.
                    var domainUri = new Uri("https://backloggd.com");
                    var cookieCollection = cookies.GetCookies(domainUri);
                    foreach (System.Net.Cookie c in cookieCollection)
                    {
                        _authService.Cookies.Add(c);
                    }

                    // Clearing on the "no" branch matters: an earlier session may still be on disk.
                    if (IsRememberMeChecked)
                    {
                        var cookieList = new System.Collections.Generic.List<System.Net.Cookie>();
                        foreach (System.Net.Cookie c in cookieCollection) cookieList.Add(c);
                        _credentialStorageService.SaveCookies(cookieList);
                    }
                    else
                    {
                        _credentialStorageService.ClearCookies();
                    }


                    StatusMessage = LocalizationService.Instance["Login_Status_Success"];
                    _logger.Info($"[LoginViewModel] Login successful for user: {resolvedUsername}. Remember Me: {IsRememberMeChecked}");
                    await Task.Delay(1000); // Show success message briefly

                    LoginSuccessful?.Invoke();
                }
                else
                {
                    // errorReason is a typed string agreed with IBackloggdBrowserService; each value
                    // maps to a localization key so the user gets the real cause instead of a
                    // blanket "login failed".
                    if (errorReason == "BrowserClosed")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_BrowserClosed"];
                        _logger.Warning("[LoginViewModel] Login failed. Reason: Browser closed.");
                    }
                    else if (errorReason == "TimeoutError")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_Timeout"];
                        _logger.Warning("[LoginViewModel] Login failed. Reason: Timeout.");
                    }
                    else if (errorReason == "NetworkError")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_NetworkError"];
                        _logger.Warning("[LoginViewModel] Login failed. Reason: Network error (could not reach backloggd.com).");
                    }
                    else if (errorReason == "BlockedByAntiBot")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_BlockedByAntiBot"];
                        _logger.Error("[LoginViewModel] Login failed. Reason: Backloggd's anti-bot protection served its block page instead of the login flow. See the logs for the protection that was detected.");
                    }
                    else if (errorReason == "BrowserExecutableNotFound")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_BrowserNotFound"];
                        _logger.Error("[LoginViewModel] Playwright browser not found.");
                    }
                    else if (errorReason == "BrowserDepsMissing")
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_BrowserDepsMissing"];
                        _logger.Error("[LoginViewModel] Browser installed but system libraries are missing (Linux dependencies).");
                    }
                    else
                    {
                        StatusMessage = LocalizationService.Instance["Login_Status_Failed"];
                        _logger.Warning($"[LoginViewModel] Login failed. Reason: {errorReason ?? "Unknown"}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[LoginViewModel] Login failed with exception.", ex);
                System.Diagnostics.Debug.WriteLine($"[LoginViewModel] Login error: {ex.Message}");
                StatusMessage = LocalizationService.Instance["Login_Status_Failed"];
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke();
        }
    }
}
