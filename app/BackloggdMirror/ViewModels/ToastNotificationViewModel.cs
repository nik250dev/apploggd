using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackloggdMirror.Models;
using System;
using Avalonia.Threading;

namespace BackloggdMirror.ViewModels;

/// <summary>
/// A single toast, which owns its own countdown. <see cref="Progress"/> runs from 100 to 0 so the
/// view can bind a draining bar directly, and the toast removes itself through the dismiss callback
/// — either when the time runs out or when the user closes it.
/// </summary>
public partial class ToastNotificationViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private ToastType _type;

    [ObservableProperty]
    private double _progress = 100;

    private readonly DispatcherTimer _timer;
    private readonly Action<ToastNotificationViewModel> _onDismiss;
    private readonly TimeSpan _totalDuration;
    private DateTime _startTime;

    public ToastNotificationViewModel(string message, ToastType type, Action<ToastNotificationViewModel> onDismiss, TimeSpan? duration = null)
    {
        Message = message;
        Type = type;
        _onDismiss = onDismiss;
        _totalDuration = duration ?? TimeSpan.FromSeconds(7);

        // ~60 fps, because this drives an animated bar rather than just an expiry check.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, OnTimerTick);
        StartTimer();
    }

    public void StartTimer()
    {
        _startTime = DateTime.Now;
        _timer.Start();
    }

    // Time remaining is recomputed from the start timestamp rather than accumulated per tick, so a
    // stalled or coalesced timer cannot make the toast outlive its duration.
    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _startTime;
        var remaining = _totalDuration - elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            Progress = 0;
            _timer.Stop();
            _onDismiss(this);
        }
        else
        {
            Progress = (remaining.TotalMilliseconds / _totalDuration.TotalMilliseconds) * 100;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer.Stop();
        _onDismiss(this);
    }
}
