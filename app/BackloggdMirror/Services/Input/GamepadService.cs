using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace BackloggdMirror.Services.Input;

public enum GamepadAction { Up, Down, Left, Right, Accept, Back, Minimize }

/// <summary>Decides which button glyphs the UI shows.</summary>
public enum GamepadKind { Xbox, PlayStation, Nintendo }

/// <summary>Controller input through SDL2, loaded lazily and only acted on while listening; any failure leaves it inert.</summary>
public sealed class GamepadService : IDisposable
{
    private const int PollIntervalMs = 16;
    private const int IdlePumpIntervalMs = 250;
    private const long RepeatDelayMs = 400;
    private const long RepeatIntervalMs = 120;
    private const short StickThreshold = 16384;
    private const long Suppressed = long.MaxValue;

    private static readonly GamepadAction[] Directions = { GamepadAction.Up, GamepadAction.Down, GamepadAction.Left, GamepadAction.Right };

    private readonly IAppLogger? _logger;
    private readonly ManualResetEventSlim _listening = new(false);
    private readonly object _startLock = new();
    private readonly Dictionary<int, IntPtr> _controllers = new();
    private readonly Dictionary<GamepadAction, long> _heldUntil = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _unavailable;

    public GamepadService(IAppLogger? logger)
    {
        _logger = logger;
    }

    public event Action<GamepadAction, GamepadKind>? ActionReceived;

    public bool IsListening
    {
        get => _listening.IsSet;
        set
        {
            if (_disposed || _unavailable || value == _listening.IsSet) return;

            if (value)
            {
                EnsureThread();
                _listening.Set();
            }
            else
            {
                _listening.Reset();
            }
        }
    }

    private void EnsureThread()
    {
        lock (_startLock)
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Gamepad" };
            _thread.Start();
        }
    }

    // SDL has to be pumped from the thread that initialised it.
    private void Run()
    {
        if (!TryInitialize())
        {
            _unavailable = true;
            _listening.Reset();
            return;
        }

        bool wasListening = false;
        try
        {
            while (!_disposed)
            {
                if (!_listening.IsSet)
                {
                    wasListening = false;
                    // Left unread while a controller keeps reporting, this thread's queue stalls the UI thread's timers.
                    if (!_listening.Wait(IdlePumpIntervalMs))
                    {
                        while (Sdl2.SDL_PollEvent(out var idle) == 1)
                        {
                            HandleEvent(idle, resumed: true);
                        }
                    }
                    continue;
                }

                // Whatever was pressed while not listening went to another app.
                bool resumed = !wasListening;
                wasListening = true;

                while (Sdl2.SDL_PollEvent(out var e) == 1)
                {
                    HandleEvent(e, resumed);
                }
                PollDirections(resumed);

                Thread.Sleep(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _logger?.Error("[Gamepad] The controller loop failed. Controller navigation is off until the app restarts.", ex);
        }
        finally
        {
            foreach (var controller in _controllers.Values)
            {
                Sdl2.SDL_GameControllerClose(controller);
            }
            _controllers.Clear();
            Sdl2.SDL_Quit();
        }
    }

    private bool TryInitialize()
    {
        try
        {
            Sdl2.RegisterResolver();

            // No SDL window exists, so SDL would treat itself as always in the background.
            Sdl2.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            // Without it no press ever arrives on Windows: nothing pumps SDL's input window messages.
            Sdl2.SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
            // Positional buttons: south accepts, east goes back and north minimizes on every layout, Nintendo included.
            Sdl2.SDL_SetHint("SDL_GAMECONTROLLER_USE_BUTTON_LABELS", "0");
            // Rumble or the LED switch DualShock/DualSense to a report mode the frontend no longer reads.
            Sdl2.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE", "0");
            Sdl2.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "0");
            Sdl2.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5_PLAYER_LED", "0");
            // Otherwise SDL raises the system timer resolution to 1 ms.
            Sdl2.SDL_SetHint("SDL_TIMER_RESOLUTION", "0");
            // SDL would take over SIGINT/SIGTERM from the .NET runtime.
            Sdl2.SDL_SetHint("SDL_NO_SIGNAL_HANDLERS", "1");

            if (Sdl2.SDL_Init(Sdl2.InitGameController) != 0)
            {
                _logger?.Warning($"[Gamepad] SDL_Init failed: {Sdl2.GetError()}. Controller navigation is off.");
                return false;
            }

            Sdl2.SDL_GetVersion(out var version);
            _logger?.Info($"[Gamepad] SDL {version.Major}.{version.Minor}.{version.Patch} ready.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[Gamepad] SDL2 could not be loaded ({ex.GetType().Name}: {ex.Message}). Controller navigation is off.");
            return false;
        }
    }

    private void HandleEvent(Sdl2.Event e, bool resumed)
    {
        switch (e.Type)
        {
            case Sdl2.EventControllerDeviceAdded:
                OpenController(e.Which);
                break;

            case Sdl2.EventControllerDeviceRemoved:
                if (_controllers.Remove(e.Which, out var removed))
                {
                    Sdl2.SDL_GameControllerClose(removed);
                    _logger?.Info($"[Gamepad] Controller {e.Which} disconnected.");
                }
                break;

            case Sdl2.EventControllerButtonDown when !resumed:
                if (!_controllers.TryGetValue(e.Which, out var controller)) break;
                if (e.Button == Sdl2.ButtonA) Raise(GamepadAction.Accept, controller);
                else if (e.Button == Sdl2.ButtonB) Raise(GamepadAction.Back, controller);
                else if (e.Button == Sdl2.ButtonY) Raise(GamepadAction.Minimize, controller);
                break;
        }
    }

    private void OpenController(int deviceIndex)
    {
        if (Sdl2.SDL_IsGameController(deviceIndex) == 0) return;

        var controller = Sdl2.SDL_GameControllerOpen(deviceIndex);
        if (controller == IntPtr.Zero)
        {
            _logger?.Warning($"[Gamepad] Could not open controller {deviceIndex}: {Sdl2.GetError()}");
            return;
        }

        int instanceId = Sdl2.SDL_JoystickInstanceID(Sdl2.SDL_GameControllerGetJoystick(controller));
        if (!_controllers.TryAdd(instanceId, controller))
        {
            Sdl2.SDL_GameControllerClose(controller);
            return;
        }

        _logger?.Info($"[Gamepad] Controller {instanceId} connected: '{Sdl2.GetName(controller)}' ({KindOf(controller)}).");
    }

    // Read from state rather than events to drive the auto-repeat; the stick counts on its dominant axis only.
    private void PollDirections(bool resumed)
    {
        long now = _clock.ElapsedMilliseconds;

        foreach (var direction in Directions)
        {
            var source = FindControllerHolding(direction);
            if (source == IntPtr.Zero)
            {
                _heldUntil.Remove(direction);
                continue;
            }

            if (!_heldUntil.TryGetValue(direction, out var next))
            {
                // Held since before listening started: it has to be released first.
                if (resumed)
                {
                    _heldUntil[direction] = Suppressed;
                    continue;
                }

                Raise(direction, source);
                _heldUntil[direction] = now + RepeatDelayMs;
            }
            else if (next != Suppressed && now >= next)
            {
                Raise(direction, source);
                _heldUntil[direction] = now + RepeatIntervalMs;
            }
        }
    }

    private IntPtr FindControllerHolding(GamepadAction direction)
    {
        foreach (var controller in _controllers.Values)
        {
            if (IsHolding(controller, direction)) return controller;
        }
        return IntPtr.Zero;
    }

    private static bool IsHolding(IntPtr controller, GamepadAction direction)
    {
        int button = direction switch
        {
            GamepadAction.Up => Sdl2.ButtonDpadUp,
            GamepadAction.Down => Sdl2.ButtonDpadDown,
            GamepadAction.Left => Sdl2.ButtonDpadLeft,
            _ => Sdl2.ButtonDpadRight
        };
        if (Sdl2.SDL_GameControllerGetButton(controller, button) != 0) return true;

        int x = Sdl2.SDL_GameControllerGetAxis(controller, Sdl2.AxisLeftX);
        int y = Sdl2.SDL_GameControllerGetAxis(controller, Sdl2.AxisLeftY);
        bool horizontal = Math.Abs(x) >= Math.Abs(y);

        return direction switch
        {
            GamepadAction.Up => !horizontal && y <= -StickThreshold,
            GamepadAction.Down => !horizontal && y >= StickThreshold,
            GamepadAction.Left => horizontal && x <= -StickThreshold,
            _ => horizontal && x >= StickThreshold
        };
    }

    private static GamepadKind KindOf(IntPtr controller)
    {
        int type = Sdl2.SDL_GameControllerGetType(controller);
        return type switch
        {
            Sdl2.TypePs3 or Sdl2.TypePs4 or Sdl2.TypePs5 => GamepadKind.PlayStation,
            Sdl2.TypeSwitchPro or (>= Sdl2.TypeJoyConLeft and <= Sdl2.TypeJoyConPair) => GamepadKind.Nintendo,
            _ => GamepadKind.Xbox
        };
    }

    private void Raise(GamepadAction action, IntPtr controller)
    {
        var kind = KindOf(controller);
        Dispatcher.UIThread.Post(() => ActionReceived?.Invoke(action, kind));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Wakes the thread so it can close the controllers and quit SDL.
        _listening.Set();
    }
}
