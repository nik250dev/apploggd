using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia;
using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using BackloggdMirror.Models;
using BackloggdMirror.Services;
using BackloggdMirror.Services.Input;
using BackloggdMirror.Services.Platform.Linux;
using Avalonia.Threading;
using System.Reflection;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Avalonia.VisualTree;

namespace BackloggdMirror.Views;

public partial class MainWindow : Window
{
    private bool _canClose = false;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayOpenItem;
    private NativeMenu? _trayMenu;
    // One pause/resume entry per half of the Home, in order.
    private readonly List<NativeMenuItem> _traySlotItems = new();
    private NativeMenuItemSeparator? _trayExitSeparator;
    private NativeMenuItem? _trayExitItem;
    private TrayNotificationWindow? _trayNotificationWindow;
    private UpdateProgressWindow? _updateProgressWindow;
    private GamepadNavigator? _gamepadNavigator;
    // Whether the last press came from the pointer rather than the keyboard.
    private bool _lastInputWasPointer;

    // Big Picture takes the foreground back ~2 s after its game exits, burying the confirmation behind it.
    private static readonly TimeSpan ConfirmationHoldFront = TimeSpan.FromSeconds(10);
    private DispatcherTimer? _holdFrontTimer;
    private DateTime _holdFrontUntil;

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
        AddHandler(PointerPressedEvent, (_, _) => _lastInputWasPointer = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => _lastInputWasPointer = false, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        UpdateAnimationsEnabled();
        InitializeTrayIcons(); // Pre-render icons
        InitializeTrayIcon();

        AddHandler(PointerPressedEvent, StopHoldingFront, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, StopHoldingFront, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(this, OnWndProc);

        if (OperatingSystem.IsLinux())
        {
            LinuxTrayHost.AvailabilityChanged += OnLinuxTrayAvailabilityChanged;
        }
    }

    private void OnLinuxTrayAvailabilityChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
            {
                vm.NotifyTrayAvailabilityChanged();
            }

            // A window hidden in a tray that just vanished would be unreachable.
            if (!LinuxTrayHost.IsAvailable && !IsVisible && !_canClose)
            {
                _logger?.Info("[MainWindow] The tray went away while the window was hidden in it; showing the window.");
                ShowMainWindow();
            }
        });
    }

    /// <summary>Linux silent start: without a tray to hide in, the window goes to the taskbar minimized instead of staying unreachable.</summary>
    public async Task EnsureReachableAfterLinuxSilentStartAsync()
    {
        if (await LinuxTrayHost.WaitForHostAsync(TimeSpan.FromSeconds(15))) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible || _canClose) return;

            _logger?.Info("[MainWindow] Silent start with no tray: showing the window minimized.");
            Show();

            // X11 ignores a minimized state set before the window is mapped.
            Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// Drives the "animate" class the infinite animations of MainWindow.axaml hang off. Neither
    /// minimizing nor hiding to the tray stops an Avalonia animation on its own — that was ~7 % of
    /// a core burnt on frames nobody can see. Removing the class detaches them outright, so the
    /// render loop has nothing left to wake up for; they restart from frame one on the way back.
    /// </summary>
    private void UpdateAnimationsEnabled()
    {
        Classes.Set("animate", IsVisible && WindowState != WindowState.Minimized);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
        {
            UpdateAnimationsEnabled();
        }
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
        _gamepadNavigator?.Dispose();
        _gamepadNavigator = null;

        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            SubscribeToEvents(vm);
            UpdateTrayMenuState(vm);

            if (vm.Gamepad != null)
            {
                _gamepadNavigator = new GamepadNavigator(this, vm, vm.Gamepad, _logger);
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            SubscribeToEvents(vm);
            vm.OnWindowOpened();
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

        vm.RequestTrayUpdateNotice -= ShowTrayUpdateNotice;
        vm.RequestTrayUpdateNotice += ShowTrayUpdateNotice;

        vm.RequestShowUpdateProgress -= ShowUpdateProgress;
        vm.RequestShowUpdateProgress += ShowUpdateProgress;

        vm.RequestCloseUpdateProgress -= CloseUpdateProgress;
        vm.RequestCloseUpdateProgress += CloseUpdateProgress;

        vm.RequestPickExecutable -= PickExecutableAsync;
        vm.RequestPickExecutable += PickExecutableAsync;

        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        vm.Confirmations.CollectionChanged -= OnConfirmationsChanged;
        vm.Confirmations.CollectionChanged += OnConfirmationsChanged;

        vm.TrayMenuChanged -= OnTrayMenuChanged;
        vm.TrayMenuChanged += OnTrayMenuChanged;

        // Init/Sync initial state
        UpdateTrayMenuState(vm);
    }

    private void InitializeTrayIcon()
    {
        try
        {
            // The Deck's panel is dark and its tray icons are white glyphs.
            string trayLogo = LinuxSteamDeck.IsSteamDeck ? "app-logo-white.png" : "app-logo.png";
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri($"avares://{Assembly.GetExecutingAssembly().GetName().Name}/Assets/{trayLogo}"))),
                ToolTipText = "Apploggd"
            };

            _trayIcon.Clicked += (s, e) => RestoreMainWindow();

            var menu = new NativeMenu();

            // Some Linux hosts (GNOME's AppIndicator) open the menu on left click instead of activating the icon.
            if (OperatingSystem.IsLinux())
            {
                _trayOpenItem = new NativeMenuItem(LocalizationService.Instance["Tray_Open"]);
                _trayOpenItem.Click += (s, e) => RestoreMainWindow();
                menu.Items.Add(_trayOpenItem);
                menu.Items.Add(new NativeMenuItemSeparator());
            }

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

            _trayMenu = menu;
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
        if (e.PropertyName == nameof(BackloggdMirror.ViewModels.MainWindowViewModel.IsSessionConfirmationVisible)
            && DataContext is BackloggdMirror.ViewModels.MainWindowViewModel { IsSessionConfirmationVisible: true })
        {
            StartHoldingFront();
        }
    }

    private void OnTrayMenuChanged()
    {
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm) UpdateTrayMenuState(vm);
    }

    private void OnConfirmationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var confirmation in e.OldItems?.OfType<BackloggdMirror.ViewModels.SessionConfirmationViewModel>() ?? [])
            confirmation.PropertyChanged -= OnConfirmationPropertyChanged;

        foreach (var confirmation in e.NewItems?.OfType<BackloggdMirror.ViewModels.SessionConfirmationViewModel>() ?? [])
            confirmation.PropertyChanged += OnConfirmationPropertyChanged;

        // Each game that ends brings the window back, the second one too.
        if (e.NewItems?.Count > 0) StartHoldingFront();

        SlideRemainingDialogs(e);
    }

    private static readonly TimeSpan DialogSlideDuration = TimeSpan.FromMilliseconds(320);

    // Long enough for a click aimed at the old spot not to land on the dialog that moved under it.
    private static readonly TimeSpan DialogSlideClickGuard = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// One modal centred, two side by side: when the second arrives or one goes, the ones that stay slide to their
    /// new place instead of jumping, and ignore clicks while they move.
    /// </summary>
    private void SlideRemainingDialogs(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not BackloggdMirror.ViewModels.MainWindowViewModel vm) return;

        var added = e.NewItems?.OfType<object>().ToHashSet() ?? [];
        var before = new List<(Control Container, double X)>();
        foreach (var confirmation in vm.Confirmations)
        {
            if (added.Contains(confirmation)) continue;
            if (SessionDialogsHost.ContainerFromItem(confirmation) is { IsVisible: true } container
                && container.TranslatePoint(default, this) is { } point)
            {
                before.Add((container, point.X));
            }
        }
        if (before.Count == 0) return;

        // After the layout pass, which is when the containers reach their new place.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var (container, oldX) in before)
            {
                if (container.TranslatePoint(default, this) is not { } now) continue;
                double dx = oldX - now.X;
                if (Math.Abs(dx) < 1) continue;

                var slide = new TranslateTransform(dx, 0);
                container.RenderTransform = slide;
                slide.Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.DoubleTransition { Property = TranslateTransform.XProperty, Duration = DialogSlideDuration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() }
                };
                slide.X = 0;

                container.IsHitTestVisible = false;
                DispatcherTimer.RunOnce(() => container.IsHitTestVisible = true, DialogSlideClickGuard);
            }
        }, DispatcherPriority.Background);
    }

    private void OnConfirmationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not BackloggdMirror.ViewModels.SessionConfirmationViewModel confirmation) return;

        if (e.PropertyName == nameof(BackloggdMirror.ViewModels.SessionConfirmationViewModel.IsSessionNoteOpen))
        {
            OnSessionExtraToggled(confirmation, confirmation.IsSessionNoteOpen, "SessionNotePanel",
                () => SessionControl<TextBox>(confirmation, "SessionNoteBox"), "SessionAddNoteButton");
        }
        else if (e.PropertyName == nameof(BackloggdMirror.ViewModels.SessionConfirmationViewModel.IsSessionTagsOpen))
        {
            OnSessionExtraToggled(confirmation, confirmation.IsSessionTagsOpen, "SessionTagsPanel",
                () => TagInputIn(SessionControl<Views.Controls.RevealPanel>(confirmation, "SessionTagsPanel")), "SessionAddTagsButton");
        }
    }

    /// <summary>A named control inside the dialog of a confirmation.</summary>
    internal T? SessionControl<T>(BackloggdMirror.ViewModels.SessionConfirmationViewModel? confirmation, string name) where T : Control =>
        confirmation == null ? null
            : SessionDialogsHost.ContainerFromItem(confirmation)?.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static BackloggdMirror.ViewModels.SessionConfirmationViewModel? ConfirmationOf(object? sender) =>
        (sender as Control)?.DataContext as BackloggdMirror.ViewModels.SessionConfirmationViewModel;

    /// <summary>An opened field takes the focus, unless it opened to show what Backloggd had; a removed one hands it back to its button, if the focus was inside.</summary>
    private void OnSessionExtraToggled(BackloggdMirror.ViewModels.SessionConfirmationViewModel confirmation, bool opened, string panelName, Func<Control?> field, string addButtonName)
    {
        if (confirmation.IsFillingSessionExtras
            || DataContext is not BackloggdMirror.ViewModels.MainWindowViewModel vm || !vm.Confirmations.Contains(confirmation)) return;

        var panel = SessionControl<Views.Controls.RevealPanel>(confirmation, panelName);
        bool focusInside = panel != null && FocusManager?.GetFocusedElement() is Visual focused && panel.IsVisualAncestorOf(focused);
        Dispatcher.UIThread.Post(() =>
        {
            if (opened) FocusSessionControl(field());
            else if (focusInside || FocusManager?.GetFocusedElement() == null) FocusSessionControl(SessionControl<Button>(confirmation, addButtonName));
        }, DispatcherPriority.Background);
    }

    // Directional under the controller, so the navigator's edge lands on it as on any other control.
    private void FocusSessionControl(Control? control)
    {
        if (control == null || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) return;
        control.Focus(_gamepadNavigator?.IsControllerMode == true ? Avalonia.Input.NavigationMethod.Directional : Avalonia.Input.NavigationMethod.Unspecified);
    }

    private static Views.Controls.TagInputBox? TagInputIn(Visual? scope) =>
        scope?.GetVisualDescendants().OfType<Views.Controls.TagInputBox>().FirstOrDefault();

    // A click anywhere in the box, not just on the input line, starts typing.
    private void OnTagBoxPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        TagInputIn(sender as Visual)?.Focus();
    }

    /// <summary>With the pointer the focus goes back to typing; from the keyboard or a controller, to the chip that takes the removed one's place.</summary>
    private void OnSessionTagRemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not string tag
            || (sender as Visual)?.FindAncestorOfType<ItemsControl>() is not { DataContext: BackloggdMirror.ViewModels.SessionExtrasViewModel extras } list) return;

        var tagBox = list.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tag-box"));
        int index = extras.Tags.IndexOf(tag);
        extras.RemoveTag(tag);

        Dispatcher.UIThread.Post(() =>
        {
            int count = extras.Tags.Count;
            bool byPointer = _lastInputWasPointer && _gamepadNavigator?.IsControllerMode != true;
            Control? next = count == 0 || byPointer ? null
                : list.ContainerFromIndex(Math.Min(index, count - 1))?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
            FocusSessionControl(next ?? TagInputIn(tagBox));
        }, DispatcherPriority.Background);
    }

    private static Border? PendingCardOf(object? sender) =>
        (sender as Visual)?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("pending-card"));

    // The note takes the focus once the editor is in place.
    private void OnPendingExtrasOpenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var card = PendingCardOf(sender);
        Dispatcher.UIThread.Post(() =>
            FocusSessionControl(card?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("session-note"))),
            DispatcherPriority.Background);
    }

    // Done or Cancel take their buttons away; the focus goes to what opens the editor again.
    private void OnPendingExtrasClosedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var card = PendingCardOf(sender);
        Dispatcher.UIThread.Post(() =>
            FocusSessionControl(card?.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => (b.Classes.Contains("extras-summary") || b.Classes.Contains("add-extra")) && b.IsEffectivelyVisible)),
            DispatcherPriority.Background);
    }

    // Up when it would spill past the window: a popup is only kept on the screen, and under Gamescope it is drawn inside the window.
    private void OnNotePrivacyDropDownOpened(object? sender, EventArgs e)
    {
        (sender as ComboBox)?.Classes.Add("open");
        TurnPrivacyChevron(sender, 180);
        if (sender is not ComboBox comboBox
            || comboBox.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is not { Child: Control menu } popup
            || comboBox.TranslatePoint(new Point(0, comboBox.Bounds.Height), this) is not { } linkBottom) return;

        bool up = linkBottom.Y + menu.DesiredSize.Height > ClientSize.Height;
        popup.Placement = up ? PlacementMode.TopEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedLeft;
        // The menu keeps a transparent margin below it for the shadow.
        popup.VerticalOffset = up ? menu.Margin.Bottom - 2 : 2;
        menu.RenderTransformOrigin = up ? new RelativePoint(0, 1, RelativeUnit.Relative) : RelativePoint.TopLeft;
    }

    private void OnNotePrivacyDropDownClosed(object? sender, EventArgs e)
    {
        (sender as ComboBox)?.Classes.Remove("open");
        TurnPrivacyChevron(sender, 0);
    }

    // The angle lives on the template's RotateTransform, which animates it; a RenderTransform set from a style did not turn.
    private static void TurnPrivacyChevron(object? comboBox, double angle)
    {
        if ((comboBox as Visual)?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault(p => p.Name == "Chevron")?.RenderTransform is RotateTransform rotate)
        {
            rotate.Angle = angle;
        }
    }

    // The chosen privacy pops in; two classes so each change restarts the animation.
    private void OnNotePrivacyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || e.RemovedItems.Count == 0) return;
        bool wasA = comboBox.Classes.Contains("pop-a");
        comboBox.Classes.Set("pop-a", !wasA);
        comboBox.Classes.Set("pop-b", wasA);
    }

    private void StartHoldingFront()
    {
        _holdFrontUntil = DateTime.UtcNow + ConfirmationHoldFront;
        if (_holdFrontTimer == null)
        {
            // Normal priority: Background timers can starve while the window sits behind another app.
            _holdFrontTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
            _holdFrontTimer.Tick += OnHoldFrontTick;
        }
        _holdFrontTimer.Start();
        WatchInputElsewhere(true);
    }

    private void StopHoldingFront()
    {
        _holdFrontTimer?.Stop();
        WatchInputElsewhere(false);
    }

    // A click or key press here means the user is at the desktop, so whatever takes the foreground next is their choice.
    private void StopHoldingFront(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => StopHoldingFront();

    private void OnHoldFrontTick(object? sender, EventArgs e)
    {
        // Minimized, sent to the tray or already saving: the user has seen it and chose to leave it.
        bool stillWanted = DataContext is BackloggdMirror.ViewModels.MainWindowViewModel { IsSessionConfirmationVisible: true } vm
            && vm.Confirmations.Any(c => !c.IsSavingSession)
            && DateTime.UtcNow < _holdFrontUntil
            && IsVisible
            && WindowState != WindowState.Minimized;

        if (!stillWanted)
        {
            StopHoldingFront();
            return;
        }

        if (OwnsForeground()) return;

        _logger?.Info("[MainWindow] Another window took the foreground over the session confirmation; bringing it back.");
        BringToFront();
    }

    private bool _watchingInputElsewhere;

    /// <summary>Raw input in sink mode: presses in other windows too, which the window's own events never see.</summary>
    private void WatchInputElsewhere(bool watch)
    {
        if (!OperatingSystem.IsWindows() || watch == _watchingInputElsewhere) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        uint flags = watch ? RIDEV_INPUTSINK : RIDEV_REMOVE;
        IntPtr target = watch ? handle : IntPtr.Zero;
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = flags, hwndTarget = target },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = flags, hwndTarget = target }
        };
        if (RegisterRawInputDevices(devices, (uint)devices.Length, (uint)System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _watchingInputElsewhere = watch;
        }
        else
        {
            _logger?.Warning($"[MainWindow] Could not {(watch ? "watch" : "stop watching")} clicks and key presses outside the window (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT && _watchingInputElsewhere && IsRawPress(lParam))
        {
            _logger?.Info("[MainWindow] Click or key press while the session confirmation was up; leaving the foreground to the user.");
            StopHoldingFront();
        }
        return IntPtr.Zero;
    }

    /// <summary>A mouse button or a key going down; moving the mouse or letting go does not count.</summary>
    private static bool IsRawPress(IntPtr rawInput)
    {
        int headerSize = System.Runtime.InteropServices.Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(rawInput, RID_INPUT, null, ref size, (uint)headerSize);
        if (size < headerSize + 8 || size > 256) return false;

        var buffer = new byte[size];
        if (GetRawInputData(rawInput, RID_INPUT, buffer, ref size, (uint)headerSize) != size) return false;

        return BitConverter.ToUInt32(buffer, 0) switch
        {
            // RAWMOUSE.usButtonFlags: the *_DOWN flags of the five buttons.
            RIM_TYPEMOUSE => (BitConverter.ToUInt16(buffer, headerSize + 4) & 0x0155) != 0,
            // RAWKEYBOARD.Flags without RI_KEY_BREAK.
            RIM_TYPEKEYBOARD => (BitConverter.ToUInt16(buffer, headerSize + 2) & 0x01) == 0,
            _ => false
        };
    }

    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, [System.Runtime.InteropServices.Out] byte[]? data, ref uint size, uint headerSize);

    /// <summary>One entry per half: its state or what clicking does, disabled with a clock while it is busy with a session.</summary>
    private void UpdateTrayMenuState(BackloggdMirror.ViewModels.MainWindowViewModel vm)
    {
        if (_trayMenu == null) return;

        if (_traySlotItems.Count != vm.Slots.Count) RebuildTraySlotItems(vm);

        for (int i = 0; i < _traySlotItems.Count; i++)
        {
            var slot = vm.Slots[i];
            var item = _traySlotItems[i];

            // Set only on change: on Linux every assignment is a D-Bus round trip, and the play time ticks each second.
            if (item.Header != slot.TrayMenuText) item.Header = slot.TrayMenuText;

            bool busy = slot.IsBusy;
            if (item.IsEnabled == busy) item.IsEnabled = !busy;

            var icon = busy ? _clockIconBitmap : slot.IsUserPaused ? _playIconBitmap : _pauseIconBitmap;
            if (!ReferenceEquals(item.Icon, icon)) item.Icon = icon;
        }
    }

    // Between "Open Apploggd" (Linux) and Exit; with two halves, a separator sets Exit apart.
    private void RebuildTraySlotItems(BackloggdMirror.ViewModels.MainWindowViewModel vm)
    {
        if (_trayMenu == null) return;

        foreach (var old in _traySlotItems) _trayMenu.Items.Remove(old);
        _traySlotItems.Clear();
        if (_trayExitSeparator != null) _trayMenu.Items.Remove(_trayExitSeparator);
        _trayExitSeparator = null;

        int at = OperatingSystem.IsLinux() ? 2 : 0;
        for (int i = 0; i < vm.Slots.Count; i++)
        {
            int index = i;
            var item = new NativeMenuItem(vm.Slots[i].TrayMenuText);
            item.Click += (s, e) =>
            {
                if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel current && index < current.Slots.Count)
                {
                    current.ToggleSlotDetection(current.Slots[index]);
                }
            };
            _trayMenu.Items.Insert(at + i, item);
            _traySlotItems.Add(item);
        }

        if (vm.Slots.Count > 1)
        {
            _trayExitSeparator = new NativeMenuItemSeparator();
            _trayMenu.Items.Insert(at + vm.Slots.Count, _trayExitSeparator);
        }
    }

    private void RestoreMainWindow()
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        Console.WriteLine("[MainWindow] ShowMainWindow called.");

        // Tray notices talk about a hidden window. The update one is not marked as dismissed, so the next silent start shows it again.
        _trayNotificationWindow?.Close();

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

        BringToFront();

        // Controller navigation needs the window active, which Windows may refuse to another process.
        DispatcherTimer.RunOnce(() => _logger?.Info($"[MainWindow] Window shown; active: {IsActive}."), TimeSpan.FromMilliseconds(500));
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
        // Exit from the tray menu, or the system shutting down (cancelling it here would block the
        // shutdown). Either way a session in progress is dropped.
        if (_canClose || e.CloseReason == WindowCloseReason.ApplicationShutdown)
        {
            base.OnClosing(e);
            return;
        }

        bool minimizeToTray = true; // Default
        if (DataContext is BackloggdMirror.ViewModels.MainWindowViewModel vm)
        {
            if (vm.IsLoggingOut || vm.IsApplyingUpdate)
            {
                // A logout must genuinely close this window so the login one can replace it, and an
                // update needs the process gone before Update.exe can replace the folder; the
                // setting does not apply to either.
                //
                // Skipping the minimise branch matters more than it looks during an update: it also
                // skips ShowTrayNotification, and a window created mid-shutdown is not in the list
                // Avalonia is closing, so it survives and keeps the process alive for ever.
                minimizeToTray = false;
            }
            else
            {
                minimizeToTray = vm.CanMinimizeToTray && vm.MinimizeToTray;
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

        _gamepadNavigator?.Dispose();
        _gamepadNavigator = null;

        if (OperatingSystem.IsLinux())
        {
            LinuxTrayHost.AvailabilityChanged -= OnLinuxTrayAvailabilityChanged;
        }

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
                (DataContext as BackloggdMirror.ViewModels.MainWindowViewModel)?.Gamepad?.Dispose();
                desktop.Shutdown();
            }
        }
    }

    private void ShowTrayNotification()
    {
        var notice = (DataContext as BackloggdMirror.ViewModels.MainWindowViewModel)?.BuildBackgroundNotice()
            ?? new TrayNotice(TrayNoticeKind.Detecting, LocalizationService.Instance["TrayNotice_BackgroundTitle"], string.Empty);

        ShowTrayNotice(notice);
    }

    private void ShowTrayNotice(TrayNotice notice)
    {
        _trayNotificationWindow?.Close();

        var window = new TrayNotificationWindow(notice, _logger);

        // Checked against the field: a notice replaced mid-fade closes after its successor is already in place.
        window.Closed += (s, ev) =>
        {
            if (_trayNotificationWindow == window) _trayNotificationWindow = null;
        };
        window.BodyClicked += ShowMainWindow;

        _trayNotificationWindow = window;
        window.Show();
    }

    /// <summary>
    /// Progress of an in-app update. Shares the ViewModel with this window, so the progress bar
    /// binds straight to it.
    /// </summary>
    private void ShowUpdateProgress()
    {
        if (_updateProgressWindow != null) return;

        _updateProgressWindow = new UpdateProgressWindow { DataContext = DataContext };
        _updateProgressWindow.Closed += (s, ev) => _updateProgressWindow = null;
        _updateProgressWindow.Show();

        _logger?.Info("[MainWindow] Update progress window shown.");
    }

    /// <summary>Only reached when the update fails: a successful one replaces the process instead.</summary>
    private void CloseUpdateProgress()
    {
        _updateProgressWindow?.Close();
        _updateProgressWindow = null;

        _logger?.Info("[MainWindow] Update progress window closed without applying an update.");
    }

    private async Task<string?> PickExecutableAsync()
    {
        var loc = LocalizationService.Instance;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = loc["Settings_Blacklist_PickerTitle"],
            AllowMultiple = false,
            // Linux executables have no extension to filter by; the ViewModel checks the file instead.
            FileTypeFilter = OperatingSystem.IsLinux() ? null : new[]
            {
                new FilePickerFileType(loc["Settings_Blacklist_PickerFilter"]) { Patterns = new[] { "*.exe" } }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// The new-version notice on a silent start, where the window was never shown and a toast would
    /// expire unseen.
    /// </summary>
    private void ShowTrayUpdateNotice(TrayNotice notice) => ShowTrayNotice(notice);

    private static readonly Avalonia.Animation.Easings.SplineEasing DialogEnterEasing = new(0.2, 0, 0, 1);

    // The arrival: the dialog grows into place, a pink ring spreads out once and fades, and the lasting glow comes in after it.
    private static readonly BoxShadows HaloStart = BoxShadows.Parse("0 0 0 0 #99EA377A, 0 20 50 0 #8C000000, 0 0 0 0 #00EA377A");
    private static readonly BoxShadows HaloEnd = BoxShadows.Parse("0 0 0 18 #00EA377A, 0 20 50 0 #8C000000, 0 0 0 0 #00EA377A");
    private static readonly BoxShadows DialogGlow = BoxShadows.Parse("0 0 0 0 #00EA377A, 0 20 50 0 #8C000000, 0 0 32 0 #80EA377A");

    private void OnSessionDialogAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Border dialog) return;

        if (ReducedMotion.IsOn)
        {
            dialog.BoxShadow = DialogGlow;
            return;
        }

        var scale = new ScaleTransform(0.97, 0.97);
        var shift = new TranslateTransform(0, 10);
        var enter = TimeSpan.FromMilliseconds(240);
        scale.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = enter, Easing = DialogEnterEasing },
            new Avalonia.Animation.DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = enter, Easing = DialogEnterEasing }
        };
        shift.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = TranslateTransform.YProperty, Duration = enter, Easing = DialogEnterEasing }
        };
        dialog.RenderTransform = new TransformGroup { Children = { scale, shift } };
        dialog.Opacity = 0;
        dialog.BoxShadow = HaloStart;
        dialog.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200), Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            new Avalonia.Animation.BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = TimeSpan.FromMilliseconds(900), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }
        };

        DispatcherTimer.RunOnce(() =>
        {
            scale.ScaleX = scale.ScaleY = 1;
            shift.Y = 0;
            dialog.Opacity = 1;
        }, TimeSpan.FromMilliseconds(16));
        DispatcherTimer.RunOnce(() => dialog.BoxShadow = HaloEnd, TimeSpan.FromMilliseconds(140));
        DispatcherTimer.RunOnce(() =>
        {
            dialog.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = TimeSpan.FromMilliseconds(700), Easing = new Avalonia.Animation.Easings.SineEaseInOut() }
            };
            dialog.BoxShadow = DialogGlow;
        }, TimeSpan.FromMilliseconds(1060));
    }

    /// <summary>Windows refuses SetForegroundWindow to a background process unless it shares the foreground thread's input queue.</summary>
    private void BringToFront()
    {
        var handle = OperatingSystem.IsWindows() ? TryGetPlatformHandle()?.Handle ?? IntPtr.Zero : IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            Activate();
        }
        else
        {
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint thisThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        Topmost = true;
        Topmost = false;
    }

    // IsActive can lag behind on Windows, so ask the system who really has the foreground.
    private bool OwnsForeground()
    {
        if (!OperatingSystem.IsWindows()) return IsActive;

        uint foregroundProcess = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), ref foregroundProcess);
        return foregroundProcess == (uint)Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fAttach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
                    if (_trayOpenItem != null)
                        _trayOpenItem.Header = LocalizationService.Instance["Tray_Open"];
                });
            }
        }
    }
}