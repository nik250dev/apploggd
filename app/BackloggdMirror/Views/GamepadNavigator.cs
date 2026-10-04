using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BackloggdMirror.Services;
using BackloggdMirror.Services.Input;
using BackloggdMirror.ViewModels;

namespace BackloggdMirror.Views;

/// <summary>Controller navigation of the whole window. The first input only focuses, so a press meant for the frontend cannot save or discard.</summary>
internal sealed class GamepadNavigator : IDisposable
{
    private const double PointerExitDistance = 4;
    private const double ScrollStep = 80;

    private readonly MainWindow _window;
    private readonly MainWindowViewModel _vm;
    private readonly GamepadService _gamepad;
    private readonly IAppLogger? _logger;
    private Point? _pointerBaseline;
    private int _focusedResultIndex = -1;
    // Where focus goes back to when a dialog closes.
    private Control? _pageFocus;
    private ComboBox? _openComboBox;
    private int _comboBoxIndex;

    public GamepadNavigator(MainWindow window, MainWindowViewModel vm, GamepadService gamepad, IAppLogger? logger)
    {
        _window = window;
        _vm = vm;
        _gamepad = gamepad;
        _logger = logger;

        _gamepad.ActionReceived += OnAction;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ToastNotifications.CollectionChanged += OnToastsChanged;
        _vm.GameSearchResults.CollectionChanged += OnSearchResultsChanged;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        UpdateListening();
    }

    public bool IsControllerMode { get; private set; }

    private void UpdateListening()
    {
        _gamepad.IsListening = _vm.GamepadNavigationEnabled && _window.IsVisible && _window.IsActive;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty || e.Property == Visual.IsVisibleProperty)
        {
            // Coming back from another app, the first press only focuses again.
            if (!_window.IsActive) ExitControllerMode();
            UpdateListening();
        }
    }

    // A toast that expires under the focus takes it along.
    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RestoreFocusLater();

    // Each cover that streams in replaces its item, which recreates the button under the focus.
    private void OnSearchResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsControllerMode || e.Action != NotifyCollectionChangedAction.Replace || e.NewStartingIndex != _focusedResultIndex) return;

        int index = e.NewStartingIndex;
        FocusLater(() => _window.GameSearchResultsList.ContainerFromIndex(index)?.GetVisualDescendants().OfType<Button>().FirstOrDefault());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.GamepadNavigationEnabled):
                if (!_vm.GamepadNavigationEnabled) ExitControllerMode();
                UpdateListening();
                break;

            case nameof(MainWindowViewModel.IsSessionConfirmationVisible):
                if (_vm.IsSessionConfirmationVisible)
                {
                    // Each new confirmation starts without controller mode.
                    ExitControllerMode();
                }
                else if (IsControllerMode)
                {
                    RestoreFocusLater();
                }
                else
                {
                    _window.FocusManager?.ClearFocus();
                }
                break;

            case nameof(MainWindowViewModel.IsGameSelectorVisible):
                if (!IsControllerMode) break;
                _window.GameSearchResultsList.Classes.Remove("gp-dim");
                if (_vm.IsGameSelectorVisible) FocusDefaultLater();
                else if (_vm.IsSessionConfirmationVisible) FocusLater(() => _window.SessionCoverButton);
                else RestoreFocusLater();
                break;

            case nameof(MainWindowViewModel.IsGameSearchLoading):
                if (IsControllerMode && _vm.IsGameSelectorVisible && !_vm.IsGameSearchLoading
                    && !IsSearchResult(FocusedControl))
                {
                    FocusDefaultLater();
                }
                break;

            case nameof(MainWindowViewModel.IsChangelogVisible):
            case nameof(MainWindowViewModel.IsClearDataConfirmationVisible):
            case nameof(MainWindowViewModel.IsNoBrowserWarningVisible):
                RestoreFocusLater();
                break;
        }
    }

    private void OnAction(GamepadAction action, GamepadKind kind)
    {
        // Posted from the controller thread, so it can arrive just after listening stopped.
        if (!_gamepad.IsListening) return;

        _window.Classes.Set("gp-ps", kind == GamepadKind.PlayStation);
        _window.Classes.Set("gp-nintendo", kind == GamepadKind.Nintendo);

        if (_openComboBox is { IsDropDownOpen: true } && IsControllerMode)
        {
            OnDropDownAction(_openComboBox, action);
            return;
        }
        _openComboBox = null;

        var candidates = Candidates();
        if (!IsControllerMode)
        {
            EnterControllerMode(kind);
            if (candidates.Count > 0) Focus(DefaultTarget(candidates));
            return;
        }

        if (action == GamepadAction.Minimize)
        {
            Minimize();
            return;
        }

        var current = FocusedControl;
        if (current == null || !candidates.Contains(current))
        {
            if (candidates.Count > 0) Focus(DefaultTarget(candidates));
            return;
        }

        switch (action)
        {
            case GamepadAction.Accept:
                Press(current);
                break;

            case GamepadAction.Back:
                Back(current);
                break;

            default:
                var next = FindNext(current, action, candidates);
                if (next != null) Focus(next);
                else if (action is GamepadAction.Up or GamepadAction.Down) Scroll(current, action);
                break;
        }
    }

    private void Back(Control current)
    {
        // Never on the confirmation itself: a stray press must not throw a session away.
        if (_vm.IsGameSelectorVisible) _vm.CloseGameSelectorCommand.Execute(null);
        else if (_vm.IsSessionConfirmationVisible) return;
        else if (_vm.IsNoBrowserWarningVisible) _vm.CloseNoBrowserWarningCommand.Execute(null);
        else if (_vm.IsClearDataConfirmationVisible) _vm.CloseClearDataConfirmationCommand.Execute(null);
        else if (_vm.IsChangelogVisible) _vm.CloseChangelogCommand.Execute(null);
        else if (!_window.Sidebar.IsVisualAncestorOf(current) && ActiveSidebarButton is { } sidebar && IsNavigable(sidebar)) Focus(sidebar);
    }

    private void Minimize()
    {
        _logger?.Info("[Gamepad] Window minimized from the controller.");
        _window.WindowState = WindowState.Minimized;
    }

    /// <summary>The topmost dialog, in the order MainWindow.axaml stacks them; null when the pages are in front.</summary>
    private Control? ActiveDialog =>
        _vm.IsGameSelectorVisible ? _window.GameSelectorDialog
        : _vm.IsSessionConfirmationVisible ? _window.SessionDialog
        : _vm.IsNoBrowserWarningVisible ? _window.NoBrowserDialog
        : _vm.IsClearDataConfirmationVisible ? _window.ClearDataDialog
        : _vm.IsChangelogVisible ? _window.ChangelogDialog
        : null;

    private List<Control> Candidates()
    {
        var candidates = new List<Control>();

        var dialog = ActiveDialog;
        if (dialog != null) candidates.AddRange(NavigableIn(dialog));
        // The update runs in a window of its own; the blurred pages behind it stay out of reach.
        // Logging out leads to a login a controller cannot fill in.
        else if (!_vm.IsUpdateProgressVisible) candidates.AddRange(NavigableIn(_window.MainContent).Where(c => c != _window.SidebarLogoutButton));

        candidates.AddRange(_window.ToastsLayer.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("toast-action") && IsNavigable(b)));

        return candidates;
    }

    private static IEnumerable<Control> NavigableIn(Visual root) =>
        root.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ComboBox && IsNavigable(c));

    // The search box and its button are left out: the picker opens with the search already run.
    private static bool IsNavigable(Control control) =>
        control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Focusable
        && control.FindAncestorOfType<TextBox>() == null;

    private Control DefaultTarget(List<Control> candidates)
    {
        if (_vm.IsGameSelectorVisible)
        {
            return candidates.FirstOrDefault(IsSearchResult)
                ?? candidates.FirstOrDefault(b => b == _window.GameSelectorCancelButton)
                ?? candidates[0];
        }

        if (_vm.IsSessionConfirmationVisible)
        {
            // An unidentified game cannot be saved; picking it is the way forward.
            return candidates.FirstOrDefault(b => b == _window.SessionSaveButton)
                ?? candidates.FirstOrDefault(b => b == _window.SessionCoverButton)
                ?? candidates[0];
        }

        // Dialogs list their safe way out first.
        if (ActiveDialog != null) return candidates[0];

        if (_pageFocus != null && candidates.Contains(_pageFocus)) return _pageFocus;
        return candidates.FirstOrDefault(c => c == ActiveSidebarButton) ?? candidates[0];
    }

    private Button? ActiveSidebarButton =>
        _vm.IsPendingVisible ? _window.SidebarPendingButton
        : _vm.IsSettingsVisible ? _window.SidebarSettingsButton
        : _window.SidebarHomeButton;

    private bool IsSearchResult(Control? control) => control != null && _window.GameSearchResultsList.IsVisualAncestorOf(control);

    private bool IsToast(Control control) => _window.ToastsLayer.IsVisualAncestorOf(control);

    private Control? FocusedControl => _window.FocusManager?.GetFocusedElement() as Control;

    private void FocusDefaultLater() => FocusLater(() =>
    {
        var candidates = Candidates();
        return candidates.Count > 0 ? DefaultTarget(candidates) : null;
    });

    // After the layout pass, once the controls that just appeared exist.
    private void FocusLater(Func<Control?> target)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsControllerMode) return;
            var control = target();
            if (control != null && IsNavigable(control)) Focus(control);
        }, DispatcherPriority.Background);
    }

    /// <summary>Once the layout settles, moves a focus that vanished or fell out of scope; near the old spot if it was on a page.</summary>
    private void RestoreFocusLater(Rect? near = null)
    {
        if (!IsControllerMode) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsControllerMode || (_openComboBox?.IsDropDownOpen ?? false)) return;

            var candidates = Candidates();
            var current = FocusedControl;
            if (candidates.Count == 0 || (current != null && candidates.Contains(current))) return;

            Control? target = null;
            if (near is { } spot && ActiveDialog == null)
            {
                // On the same side of the sidebar edge: a page button that went away is replaced by another of the page.
                bool inSidebar = spot.Center.X < BoundsOf(_window.Sidebar).Right;
                target = candidates.Where(c => !IsToast(c) && _window.Sidebar.IsVisualAncestorOf(c) == inSidebar)
                    .OrderBy(c => Distance(BoundsOf(c).Center, spot.Center))
                    .FirstOrDefault();
            }
            Focus(target ?? DefaultTarget(candidates));
        }, DispatcherPriority.Background);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void Focus(Control control)
    {
        _focusedResultIndex = IsSearchResult(control) && control.GetVisualParent() is Control container
            ? _window.GameSearchResultsList.IndexFromContainer(container)
            : -1;
        if (_window.MainContent.IsVisualAncestorOf(control)) _pageFocus = control;
        // Not :focus-within, which missed the refocus after a cover streamed in.
        _window.GameSearchResultsList.Classes.Set("gp-dim", _focusedResultIndex >= 0);
        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
    }

    // Wholly past the current control first; the in-line-only fallback keeps a held direction inside the grid.
    private Control? FindNext(Control from, GamepadAction direction, List<Control> candidates) =>
        FindNext(from, direction, candidates, wholly: true) ?? FindNext(from, direction, candidates, wholly: false);

    private Control? FindNext(Control from, GamepadAction direction, List<Control> candidates, bool wholly)
    {
        const double Tolerance = 1;
        var origin = BoundsOf(from);
        Control? best = null;
        double bestScore = double.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate == from) continue;
            var r = BoundsOf(candidate);

            double along, across;
            switch (direction)
            {
                case GamepadAction.Up:
                    if (wholly ? r.Bottom > origin.Top + Tolerance : r.Center.Y >= origin.Center.Y - Tolerance) continue;
                    along = Math.Max(0, origin.Top - r.Bottom);
                    across = Gap(origin.Left, origin.Right, r.Left, r.Right);
                    break;
                case GamepadAction.Down:
                    if (wholly ? r.Top < origin.Bottom - Tolerance : r.Center.Y <= origin.Center.Y + Tolerance) continue;
                    along = Math.Max(0, r.Top - origin.Bottom);
                    across = Gap(origin.Left, origin.Right, r.Left, r.Right);
                    break;
                case GamepadAction.Left:
                    if (wholly ? r.Right > origin.Left + Tolerance : r.Center.X >= origin.Center.X - Tolerance) continue;
                    along = Math.Max(0, origin.Left - r.Right);
                    across = Gap(origin.Top, origin.Bottom, r.Top, r.Bottom);
                    break;
                default:
                    if (wholly ? r.Left < origin.Right - Tolerance : r.Center.X <= origin.Center.X + Tolerance) continue;
                    along = Math.Max(0, r.Left - origin.Right);
                    across = Gap(origin.Top, origin.Bottom, r.Top, r.Bottom);
                    break;
            }

            if (!wholly && across > 0) continue;

            double score = along + 2 * across;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static double Gap(double aStart, double aEnd, double bStart, double bEnd) =>
        Math.Max(0, Math.Max(aStart, bStart) - Math.Min(aEnd, bEnd));

    private Rect BoundsOf(Control control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), _window) ?? default;
        var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), _window) ?? topLeft;
        return new Rect(topLeft, bottomRight);
    }

    // Past the last control up or down, the content scrolls instead: the changelog has nothing else to move through.
    private void Scroll(Control from, GamepadAction direction)
    {
        var scope = (Visual?)ActiveDialog ?? _window.MainContent;
        var viewer = from.FindAncestorOfType<ScrollViewer>() is { } own && scope.IsVisualAncestorOf(own)
            ? own
            : scope.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(v => v.IsEffectivelyVisible && v.Extent.Height > v.Viewport.Height);
        if (viewer == null) return;

        double max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        double y = viewer.Offset.Y + (direction == GamepadAction.Up ? -ScrollStep : ScrollStep);
        viewer.Offset = viewer.Offset.WithY(Math.Clamp(y, 0, max));
    }

    private void Press(Control control)
    {
        if (control is ComboBox comboBox)
        {
            OpenDropDown(comboBox);
            return;
        }

        // The session cover draws its ring on the frame around it, so that is what gets pressed.
        Control pressed = control == _window.SessionCoverButton ? _window.SessionCoverHost : control;
        Rect? spot = IsToast(control) ? null : BoundsOf(control);

        pressed.Classes.Add("gp-press");
        DispatcherTimer.RunOnce(() =>
        {
            pressed.Classes.Remove("gp-press");
            if (!control.IsEffectivelyEnabled || !control.IsEffectivelyVisible) return;

            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            if (peer is IToggleProvider toggle) toggle.Toggle();
            else if (peer is IInvokeProvider invoker) invoker.Invoke();
            // Pending decision: after Save, Discard or Later, maybe hide to the tray so the frontend regains the foreground.

            // Pause/Resume, a discarded pending row and the like take the focused button away with them.
            RestoreFocusLater(spot);
        }, TimeSpan.FromMilliseconds(90));
    }

    // The items live in the popup, outside the window's focus scope, so the highlight is driven by hand.
    private void OpenDropDown(ComboBox comboBox)
    {
        _openComboBox = comboBox;
        _comboBoxIndex = Math.Max(0, comboBox.SelectedIndex);
        comboBox.IsDropDownOpen = true;
        Dispatcher.UIThread.Post(() => FocusDropDownItem(comboBox), DispatcherPriority.Background);
    }

    private void OnDropDownAction(ComboBox comboBox, GamepadAction action)
    {
        switch (action)
        {
            case GamepadAction.Up:
            case GamepadAction.Down:
                int count = comboBox.ItemCount;
                if (count == 0) return;
                _comboBoxIndex = Math.Clamp(_comboBoxIndex + (action == GamepadAction.Up ? -1 : 1), 0, count - 1);
                FocusDropDownItem(comboBox);
                break;

            case GamepadAction.Accept:
                comboBox.SelectedIndex = _comboBoxIndex;
                CloseDropDown(comboBox);
                break;

            case GamepadAction.Back:
                CloseDropDown(comboBox);
                break;

            case GamepadAction.Minimize:
                CloseDropDown(comboBox);
                Minimize();
                break;
        }
    }

    private void FocusDropDownItem(ComboBox comboBox)
    {
        if (comboBox.ContainerFromIndex(_comboBoxIndex) is Control item)
        {
            item.Focus(NavigationMethod.Directional);
            item.BringIntoView();
        }
    }

    private void CloseDropDown(ComboBox comboBox)
    {
        comboBox.IsDropDownOpen = false;
        _openComboBox = null;
        if (IsNavigable(comboBox)) Focus(comboBox);
    }

    private void EnterControllerMode(GamepadKind kind)
    {
        if (IsControllerMode) return;
        IsControllerMode = true;
        _pointerBaseline = null;
        _window.Classes.Add("gamepad");
        _logger?.Info($"[Gamepad] Controller mode on ({kind}).");
    }

    private void ExitControllerMode()
    {
        if (!IsControllerMode) return;
        IsControllerMode = false;
        _openComboBox = null;
        _window.Classes.Remove("gamepad");
        _window.GameSearchResultsList.Classes.Remove("gp-dim");

        // Focus stood in for the hover on these two; the pointer takes over again.
        var focused = FocusedControl;
        if (focused == _window.SessionCoverButton) _vm.OnCoverPointerExited();
        else if (focused == _window.InfoButton) _vm.OnInfoIconExited();
    }

    // A window appearing under a still cursor gets pointer events too; only a real move counts.
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsControllerMode) return;

        var position = e.GetPosition(_window);
        if (_pointerBaseline is not { } baseline)
        {
            _pointerBaseline = position;
            return;
        }

        if (Math.Abs(position.X - baseline.X) > PointerExitDistance || Math.Abs(position.Y - baseline.Y) > PointerExitDistance)
        {
            ExitControllerMode();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => ExitControllerMode();

    private void OnKeyDown(object? sender, KeyEventArgs e) => ExitControllerMode();

    public void Dispose()
    {
        _gamepad.ActionReceived -= OnAction;
        _gamepad.IsListening = false;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.ToastNotifications.CollectionChanged -= OnToastsChanged;
        _vm.GameSearchResults.CollectionChanged -= OnSearchResultsChanged;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _window.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _window.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }
}
