using Avalonia.Media;

namespace LiveHelper.Desktop.ViewModels;

public enum ActivityTone
{
    Info,
    Success,
    Warning,
    Error,
    Recording
}

public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(string message, ActivityTone tone, DateTimeOffset? at = null)
    {
        Message = message;
        Tone = tone;
        At = at ?? DateTimeOffset.Now;
    }

    public string Message { get; }

    public ActivityTone Tone { get; }

    public DateTimeOffset At { get; }

    public string TimeText => At.ToString("HH:mm:ss");

    public IBrush DotBrush => Tone switch
    {
        ActivityTone.Success => Brushes.SeaGreen,
        ActivityTone.Warning => Brushes.DarkOrange,
        ActivityTone.Error => Brushes.IndianRed,
        ActivityTone.Recording => Brushes.Coral,
        _ => Brushes.SlateGray
    };
}
