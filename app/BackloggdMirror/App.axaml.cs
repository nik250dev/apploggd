using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using BackloggdMirror.ViewModels;
using BackloggdMirror.Views;
using BackloggdMirror.Services;

using BackloggdMirror.Models;

namespace BackloggdMirror;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Prevent app from closing when MainWindow closes (we use Tray for lifecycle)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // Services are wired by hand and shared for the whole process lifetime; there is no DI
            // container. The same instances are later handed to MainWindowViewModel so the session
            // survives the window swap.
            var logger = new AppLogger();
            var authService = new BackloggdAuthService(logger);
            var browserService = new BackloggdBrowserService(logger);
            var gameDetectionService = new GameDetectionService(logger);
            var settingsService = new SettingsService(logger);
            var credentialStorageService = new CredentialStorageService(logger);
            var installService = new PlaywrightInstallService(logger);
            var autostartService = new AutostartService(logger);

            // Explicitly load settings here to avoid infinite recursion in constructor
            settingsService.Load();

            // The autostart entry lives in the registry, outside settings.json, and the two drift
            // apart on their own — see AutostartService.Reconcile. Doing it here, before any window,
            // is what keeps the setting from quietly describing something that is no longer true.
            autostartService.Reconcile(settingsService.StartWithWindows);

            // Initialize Language
            LocalizationService.Instance.SetLanguage(settingsService.Language);

            // Launched by the autostart entry rather than by the user, so nothing may appear on
            // screen: the app goes straight to the tray once the session is restored.
            bool silentStart = AutostartService.IsSilentStart(desktop.Args);

            var loginVm = new LoginViewModel(authService, browserService, credentialStorageService, logger, installService);

            var loginWindow = new LoginWindow
            {
                DataContext = loginVm
            };

            if (silentStart)
            {
                logger.Info("[App] Started by Windows autostart: keeping every window hidden.");

                // desktop.MainWindow is deliberately left unset: the lifetime shows whatever sits
                // there when Start() runs. Both handlers below assign it, which is also what
                // restores the rule that closing the login window quits the app.
                loginVm.UserInputRequired += () =>
                {
                    if (loginWindow.IsVisible) return;

                    logger.Info("[App] The silent start cannot continue without the user; showing the login window.");
                    desktop.MainWindow = loginWindow;
                    loginWindow.Show();
                };
            }
            else
            {
                // The login window starts out as MainWindow so that closing it quits the app; the
                // assignment below moves that role once the user is in.
                desktop.MainWindow = loginWindow;
            }

            loginVm.LoginSuccessful += () =>
            {
                var mainWindowVm = new MainWindowViewModel(gameDetectionService, authService, browserService, settingsService, credentialStorageService, logger, autostartService: autostartService);

                mainWindowVm.IsLoggedIn = true;

                if (!string.IsNullOrEmpty(loginVm.ResolvedUsername))
                {
                    // Fire and forget: errors are handled inside the ViewModel, and the window must
                    // not wait on a network round-trip to appear.
                    _ = mainWindowVm.LoadData(loginVm.ResolvedUsername);
                }

                var mainWindow = new MainWindow(logger)
                {
                    DataContext = mainWindowVm
                };

                // Reassign MainWindow before closing the login one: under OnExplicitShutdown,
                // LoginWindow.OnClosed only shuts the app down while it still holds that role.
                desktop.MainWindow = mainWindow;

                // On a silent start the window is created but never shown: the tray icon is set up
                // in MainWindow's constructor, so the app is fully usable from the tray while
                // nothing has appeared on screen. Clicking the icon shows it for the first time.
                if (!silentStart)
                {
                    mainWindow.Show();
                }

                loginWindow.Close();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}