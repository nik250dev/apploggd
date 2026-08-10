using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia;
using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using BackloggdMirror.Services;
using Avalonia.Threading;
using System.Reflection;

namespace BackloggdMirror.Views;

public partial class MainWindow : Window
{
    private bool _canClose = false;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayToggleItem;
    private NativeMenuItem? _trayExitItem;
    private TrayNotificationWindow? _trayNotificationWindow;

    // Taken as a constructor argument rather than read off the DataContext: the tray icon is built
    // in the constructor, before any DataContext has been assigned, and its failures are exactly
    // the ones worth having in the log.
    private readonly IAppLogger? _logger;

    // Icon Bitmaps
    private Bitmap? _playIconBitmap;
    private Bitmap? _pauseIconBitmap;
    private Bitmap? _clockIconBitmap;

    // Path Data Constants
    private const string PlayPathData = "M8,5.14V19.14L19,12.14L8,5.14Z";
    private const string PausePathData = "M23 5C22 5 22 6 22 6V16C22 17 23 17 24 17H25C26 17 26 16 26 16V6C26 6 26 5 25 5ZM30 5C30 5 29 5 29 6V16C29 17 30 17 31 17H32C32 17 33 17 33 16V6C33 6 33 5 32 5Z";
    private const string ClockPathData = "M12,20A8,8 0 0,0 20,12A8,8 0 0,0 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22C6.47,22 2,17.5 2,12A10,10 0 0,1 12,2M12.5,7V12.25L17,14.92L16.25,16.15L11,13V7H12.5Z";

    /// <summary>
    /// Kept genuinely parameterless (rather than folded into the overload below with a default
    /// argument) because Avalonia's runtime XAML loader only accepts a public parameterless
    /// constructor.
    /// </summary>
    public MainWindow() : this(null)
    {
    }

    public MainWindow(IAppLogger? logger)
    {
        _logger = logger;

        InitializeComponent();
        InitializeTrayIcons(); // Pre-render icons
        InitializeTrayIcon();
    }

    private void InitializeTrayIcons()
    {
        try
        {
            // Use a standard size for all icons to ensure alignment
            _playIconBitmap = CreateBitmapFromPath(PlayPathData);
            _pauseIconBitmap = CreateBitmapFromPath(PausePathData);
            _clockIconBitmap = CreateBitmapFromPath(ClockPathData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating tray icons: {ex.Message}");
            _logger?.Error("[MainWindow] Could not rasterize the tray menu icons. The menu items will show without icons.", ex);
        }
    }

    /// <summary>
    /// Rasterizes path data into a tray-menu icon. Native menu items take a Bitmap, not vector
    /// geometry, so the icons are rendered once at startup instead of being declared in XAML.
    /// </summary>
    private Bitmap CreateBitmapFromPath(string data)
    {
        var pathGeometry = PathGeometry.Parse(data);
        var pathIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = pathGeometry,
            Fill = Brushes.White, // Requested: White Color
            Stretch = Stretch.Uniform,
            Width = 16,  // Standard inner icon size
            Height = 16,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        // Container to center the icon
        var grid = new Grid
        {
            Width = 24, // Standard tray icon slot size (logical)
            Height = 24,
            Children = { pathIcon }
        };

        grid.Measure(new Size(24, 24));
        grid.Arrange(new Rect(0, 0, 24, 24));

        // Render at higher DPI for crispness
        var renderBitmap = new RenderTargetBitmap(new PixelSize(48, 48), new Vector(144, 144));
        renderBitmap.Render(grid);

        return renderBitmap;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            SubscribeToEvents(vm);
            UpdateTrayMenuState(vm);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            SubscribeToEvents(vm);
        }
    }

    private void SubscribeToEvents(BackloggdMirror.ViewModels.MainWindowViewModel vm)
    {
        // Unsubscribe first to ensure no duplicates
        vm.RequestFlashWindow -= FlashWindow;
        vm.RequestFlashWindow += FlashWindow;

        vm.RequestShowMainWindow -= ShowMainWindow;
        vm.RequestShowMainWindow += ShowMainWindow;

        vm.RequestCloseApplication -= CloseApplication;
        vm.RequestCloseApplication += CloseApplication;

        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // Init/Sync initial state
        UpdateTrayMenuState(vm);
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/app-logo.png"))),
                ToolTipText = "Apploggd"
            };

            _trayIcon.Clicked += (s, e) => RestoreMainWindow();

            var menu = new NativeMenu();

            _trayToggleItem = new NativeMenuItem(LocalizationService.Instance["Home_PauseSearch"]);
            _trayToggleItem.Click += (s, e) =>
            {
                if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
                {
                    vm.ToggleGameDetectionCommand.Execute(null);
                }
            };
            menu.Items.Add(_trayToggleItem);

            _trayExitItem = new NativeMenuItem(LocalizationService.Instance["Tray_Exit"]);
            _trayExitItem.Click += (s, e) =>
            {
                if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
                {
                    vm.ExitApplicationCommand.Execute(null);
                }
            };
            menu.Items.Add(_trayExitItem);

            LocalizationService.Instance.PropertyChanged += OnLocalizationPropertyChanged;

            _trayIcon.Menu = menu;

            if (Application.Current != null)
            {
                var icons = TrayIcon.GetIcons(Application.Current);
                icons?.Add(_trayIcon);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing TrayIcon: {ex.Message}");
            _logger?.Error("[MainWindow] The tray icon could not be created. With no icon there is no way back to the window once it is minimized to the tray, and no way to quit from there.", ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackloggdMirror.ViewModels.MainWindowViewModel.TrayMenuActionText)
            || e.PropertyName == nameof(BackloggdMirror.ViewModels.MainWindowViewModel.IsGameRunning)
            || e.PropertyName == nameof(BackloggdMirror.ViewModels.MainWindowViewModel.IsSessionConfirmationVisible))
        {
            if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
            {
                UpdateTrayMenuState(vm);
            }
        }
    }

    private void UpdateTrayMenuState(BackloggdMirror.ViewModels.MainWindowViewModel vm)
    {
        if (_trayToggleItem == null) return;

        _trayToggleItem.Header = vm.TrayMenuActionText;

        _trayToggleItem.IsEnabled = !(vm.IsSessionConfirmationVisible || vm.IsGameRunning);

        if (vm.IsSessionConfirmationVisible || vm.IsGameRunning)
        {
            _trayToggleItem.Icon = _clockIconBitmap;
        }
        else if (vm.IsGameDetectionPaused)
        {
            _trayToggleItem.Icon = _playIconBitmap;
        }
        else
        {
            _trayToggleItem.Icon = _pauseIconBitmap;
        }
    }

    private void RestoreMainWindow()
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Console.WriteLine("[MainWindow] ShowMainWindow called.");
        if (!IsVisible)
        {
            Show();
        }

        // Center the window on the active display considering its scaling factor
        CenterWindowOnCurrentScreen();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // Activate() alone does not raise the window when the foreground belongs to another process,
        // which is exactly the case here (a game just exited). Toggling Topmost forces it up without
        // leaving the window permanently pinned.
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// Centres the window on whichever display it currently sits on, not the primary one.
    /// Position is in physical pixels while Width/Height are logical, so the screen's scaling factor
    /// has to be applied by hand — skipping it leaves the window off-centre on any display that is
    /// not at 100%.
    /// </summary>
    private void CenterWindowOnCurrentScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen != null)
            {
                double scaling = screen.Scaling;

                // Working area, so the taskbar is excluded from the centring.
                var workingArea = screen.WorkingArea;

                int windowWidthPhysical = (int)(Width * scaling);
                int windowHeightPhysical = (int)(Height * scaling);

                int x = workingArea.X + (workingArea.Width - windowWidthPhysical) / 2;
                int y = workingArea.Y + (workingArea.Height - windowHeightPhysical) / 2;

                Position = new PixelPoint(x, y);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error centering window: {ex.Message}");
            _logger?.Warning($"[MainWindow] Could not centre the window on the current screen: {ex.Message}. It opens wherever it last was.");
        }
    }

    private void CloseApplication()
    {
        _canClose = true;
        Close();
    }

    /// <summary>
    /// With the "Minimize to tray" setting on, the close button hides the window instead of closing
    /// it, so detection carries on in the background. Only the tray menu's Exit really quits.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_canClose)
        {
            // Exit requested from the tray menu: this one is a real close.
            base.OnClosing(e);
            return;
        }

        bool minimizeToTray = true; // Default
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            if (vm.IsLoggingOut)
            {
                // A logout must genuinely close this window so the login one can replace it; the
                // setting does not apply here.
                minimizeToTray = false;
            }
            else
            {
                minimizeToTray = vm.MinimizeToTray;
            }
        }

        if (minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowTrayNotification();
        }
        else
        {
            // Allow close
            base.OnClosing(e);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_trayNotificationWindow != null)
        {
            _trayNotificationWindow.Close();
        }

        // Cleanup TrayIcon
        if (_trayIcon != null)
        {
            if (Application.Current != null)
            {
                var icons = TrayIcon.GetIcons(Application.Current);
                icons?.Remove(_trayIcon);
            }
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        LocalizationService.Instance.PropertyChanged -= OnLocalizationPropertyChanged;

        // Under OnExplicitShutdown the process outlives its last window, so the shutdown has to be
        // explicit — except on a logout, where the login window is about to take over.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool isLoggingOut = false;
            if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
            {
                isLoggingOut = vm.IsLoggingOut;
            }

            if (!isLoggingOut)
            {
                desktop.Shutdown();
            }
        }
    }

    private void ShowTrayNotification()
    {
        if (_trayNotificationWindow != null)
        {
            _trayNotificationWindow.Close();
        }

        _trayNotificationWindow = new TrayNotificationWindow(_logger);
        _trayNotificationWindow.Closed += (s, ev) => _trayNotificationWindow = null;
        _trayNotificationWindow.Show();
    }

    private void OnInfoIconPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.DismissForcedTooltip();
        }
    }

    private void OnInfoIconPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.OnInfoIconExited();
        }
    }

    private void OnCoverPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.OnCoverPointerEntered();
        }
    }

    private void OnCoverPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.OnCoverPointerExited();
        }
    }

    private void OnSaveButtonPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.OnSaveButtonPointerEntered();
        }
    }

    private void OnSaveButtonPointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            vm.OnSaveButtonPointerExited();
        }
    }

    // P/Invoke for FlashWindowEx
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public System.IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_CAPTION = 1;
    private const uint FLASHW_TRAY = 2;
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMER = 4;
    private const uint FLASHW_TIMERNOFG = 12;

    /// <summary>
    /// Flashes the taskbar button so a pending session confirmation cannot be missed. Windows-only
    /// and silently skipped elsewhere: it is an attention cue, not a requirement.
    /// </summary>
    private void FlashWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle()?.Handle ?? System.IntPtr.Zero;
            if (handle != System.IntPtr.Zero)
            {
                FLASHWINFO fInfo = new FLASHWINFO();
                fInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(fInfo);
                fInfo.hwnd = handle;
                // TIMERNOFG keeps flashing until the user brings the window forward, which is what
                // makes this survive the app being minimised to the tray. uCount is therefore
                // unbounded: the user's attention, not a counter, is what stops it.
                fInfo.dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG;
                fInfo.uCount = uint.MaxValue;
                fInfo.dwTimeout = 0;

                FlashWindowEx(ref fInfo);
            }
        }
    }


    /// <summary>
    /// The native tray menu is built in code, not bound to XAML, so a language change has to be
    /// pushed into it by hand.
    /// </summary>
    private void OnLocalizationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "Item[]")
        {
            if (_trayExitItem != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_trayExitItem != null)
                        _trayExitItem.Header = LocalizationService.Instance["Tray_Exit"];
                });
            }
        }
    }
}