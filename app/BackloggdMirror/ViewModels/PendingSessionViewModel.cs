using System;
using Avalonia.Media.Imaging;
using BackloggdMirror.Models;
using BackloggdMirror.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackloggdMirror.ViewModels;

/// <summary>One row of the pending sessions view.</summary>
public partial class PendingSessionViewModel : ViewModelBase
{
    public PendingSessionViewModel(PendingSession session)
    {
        Session = session;
    }

    public PendingSession Session { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoCoverVisible))]
    private Bitmap? _coverBitmap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoCoverVisible))]
    private bool _isCoverLoading;

    public bool IsNoCoverVisible => CoverBitmap == null && !IsCoverLoading;

    public string Title => Session.GameName;

    public bool IsIdentified => Session.IsIdentified;

    public bool SaveFailed => Session.SaveFailed;

    public string? SaveTooltip => IsIdentified ? null : LocalizationService.Instance["Pending_UnidentifiedTooltip"];

    public string DurationText => FormatDuration(Session.Duration);

    /// <summary>"Today", "Yesterday", or the date once it is older.</summary>
    public string WhenText
    {
        get
        {
            var loc = LocalizationService.Instance;
            var day = Session.StartedAt.Date;

            return day == DateTime.Today ? loc["Time_Today"]
                : day == DateTime.Today.AddDays(-1) ? loc["Time_Yesterday"]
                : Session.StartedAt.ToString(day.Year == DateTime.Today.Year ? loc["Pending_DateFormat"] : loc["Pending_DateFormatWithYear"], loc.CurrentCulture);
        }
    }

    /// <summary>Re-reads every text, after a language change or an edit to the session.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public static string FormatDuration(TimeSpan duration)
    {
        int hours = (int)duration.TotalHours;
        return hours > 0 ? $"{hours}h {duration.Minutes:00}m" : $"{duration.Minutes}m";
    }
}
