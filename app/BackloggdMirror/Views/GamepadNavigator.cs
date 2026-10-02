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

/// <summary>Controller navigation of the session flow. The first input only focuses, so a press meant for the frontend cannot save or discard.</summary>
internal sealed class GamepadNavigator : IDisposable
{
    private const double PointerExitDistance = 4;

    private readonly MainWindow _window;
    private readonly MainWindowViewModel _vm;
    private readonly GamepadService _gamepad;
    private readonly IAppLogger? _logger;
    private Point? _pointerBaseline;
    private int _focusedResultIndex = -1;

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
        bool hasScope = _vm.IsSessionConfirmationVisible || _vm.ToastNotifications.Any(t => t.HasAction);
        _gamepad.IsListening = _vm.GamepadNavigationEnabled && _window.IsVisible && _window.IsActive && hasScope;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty || e.Property == Visual.IsVisibleProperty)
        {
            UpdateListening();
        }
    }

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateListening();

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
                // Each new confirmation starts without controller mode.
                ExitControllerMode();
                if (!_vm.IsSessionConfirmationVisible) _window.FocusManager?.ClearFocus();
                UpdateListening();
                break;

            case nameof(MainWindowViewModel.IsGameSelectorVisible):
                if (!IsControllerMode) break;
                _window.GameSearchResultsList.Classes.Remove("gp-dim");
                if (_vm.IsGameSelectorVisible) FocusDefaultLater();
                else FocusLater(() => _window.SessionCoverButton);
                break;

            case nameof(MainWindowViewModel.IsGameSearchLoading):
                if (IsControllerMode && _vm.IsGameSelectorVisible && !_vm.IsGameSearchLoading
                    && !IsSearchResult(FocusedButton))
                {
                    FocusDefaultLater();
                }
                break;
        }
    }

    private void OnAction(GamepadAction action, GamepadKind kind)
    {
        // Posted from the controller thread, so it can arrive just after listening stopped.
        if (!_gamepad.IsListening) return;

        _window.Classes.Set("gp-ps", kind == GamepadKind.PlayStation);
        _window.Classes.Set("gp-nintendo", kind == GamepadKind.Nintendo);

        var candidates = Candidates();
        if (candidates.Count == 0) return;

        var current = FocusedButton;
        if (!IsControllerMode || current == null || !candidates.Contains(current))
        {
            EnterControllerMode(kind);
            Focus(DefaultTarget(candidates));
            return;
        }

        switch (action)
        {
            case GamepadAction.Accept:
                Press(current);
                break;

            case GamepadAction.Back:
                // Never on the confirmation itself: a stray press must not throw a session away.
                if (_vm.IsGameSelectorVisible) _vm.CloseGameSelectorCommand.Execute(null);
                break;

            default:
                var next = FindNext(current, action, candidates);
                if (next != null) Focus(next);
                break;
        }
    }

    private List<Button> Candidates()
    {
        var candidates = new List<Button>();

        Control? dialog = _vm.IsGameSelectorVisible ? _window.GameSelectorDialog
            : _vm.IsSessionConfirmationVisible ? _window.SessionDialog
            : null;
        if (dialog != null)
        {
            candidates.AddRange(dialog.GetVisualDescendants().OfType<Button>().Where(IsNavigable));
        }

        candidates.AddRange(_window.ToastsLayer.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("toast-action") && IsNavigable(b)));

        return candidates;
    }

    // The search box and its button are left out: the picker opens with the search already run.
    private static bool IsNavigable(Button button) =>
        button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Focusable
        && button.FindAncestorOfType<TextBox>() == null;

    private Button DefaultTarget(List<Button> candidates)
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

        return candidates[0];
    }

    private bool IsSearchResult(Button? button) => button != null && _window.GameSearchResultsList.IsVisualAncestorOf(button);

    private Button? FocusedButton => _window.FocusManager?.GetFocusedElement() as Button;

    private void FocusDefaultLater() => FocusLater(() =>
    {
        var candidates = Candidates();
        return candidates.Count > 0 ? DefaultTarget(candidates) : null;
    });

    // After the layout pass, once the buttons that just appeared exist.
    private void FocusLater(Func<Button?> target)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsControllerMode) return;
            var button = target();
            if (button != null && IsNavigable(button)) Focus(button);
        }, DispatcherPriority.Background);
    }

    private void Focus(Button button)
    {
        _focusedResultIndex = IsSearchResult(button) && button.GetVisualParent() is Control container
            ? _window.GameSearchResultsList.IndexFromContainer(container)
            : -1;
        // Not :focus-within, which missed the refocus after a cover streamed in.
        _window.GameSearchResultsList.Classes.Set("gp-dim", _focusedResultIndex >= 0);
        button.Focus(NavigationMethod.Directional);
        button.BringIntoView();
    }

    // Wholly past the current button first; the in-line-only fallback keeps a held direction inside the grid.
    private Button? FindNext(Button from, GamepadAction direction, List<Button> candidates) =>
        FindNext(from, direction, candidates, wholly: true) ?? FindNext(from, direction, candidates, wholly: false);

    private Button? FindNext(Button from, GamepadAction direction, List<Button> candidates, bool wholly)
    {
        const double Tolerance = 1;
        var origin = BoundsOf(from);
        Button? best = null;
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

    private Rect BoundsOf(Button button)
    {
        var topLeft = button.TranslatePoint(new Point(0, 0), _window) ?? default;
        var bottomRight = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), _window) ?? topLeft;
        return new Rect(topLeft, bottomRight);
    }

    private void Press(Button button)
    {
        // The session cover draws its ring on the frame around it, so that is what gets pressed.
        Control pressed = button == _window.SessionCoverButton ? _window.SessionCoverHost : button;

        pressed.Classes.Add("gp-press");
        DispatcherTimer.RunOnce(() =>
        {
            pressed.Classes.Remove("gp-press");
            if (!button.IsEffectivelyEnabled || !button.IsEffectivelyVisible) return;

            if (ControlAutomationPeer.CreatePeerForElement(button) is IInvokeProvider invoker)
            {
                invoker.Invoke();
            }
            // Pending decision: after Save or Discard, maybe hide to the tray so the frontend regains the foreground.
        }, TimeSpan.FromMilliseconds(90));
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
        _window.Classes.Remove("gamepad");
        _window.GameSearchResultsList.Classes.Remove("gp-dim");

        // Focus stood in for the hover on these two; the pointer takes over again.
        var focused = FocusedButton;
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
