using LiveHelper.Core.Models;

namespace LiveHelper.Desktop.ViewModels;

public sealed class RecordingItemViewModel
{
    public RecordingItemViewModel(RecordingHistoryItem model)
    {
        Model = model;
    }

    public RecordingHistoryItem Model { get; }

    public string FileName => Path.GetFileName(Model.FilePath);

    public string FilePath => Model.FilePath;

    public string RoomName => Model.RoomName;

    public string PlatformName => PlatformCatalog.Get(Model.Platform).DisplayName;

    public string StartedText => Model.StartedAt.ToString("MM-dd HH:mm");

    public string DurationText
    {
        get
        {
            var duration = Model.EndedAt - Model.StartedAt;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
        }
    }

    public string SizeText => Model.SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{Model.SizeBytes / 1024d / 1024 / 1024:F1} GB",
        >= 1024L * 1024 => $"{Model.SizeBytes / 1024d / 1024:F1} MB",
        >= 1024 => $"{Model.SizeBytes / 1024d:F0} KB",
        _ => $"{Model.SizeBytes} B"
    };

    public bool HasError => !string.IsNullOrWhiteSpace(Model.Error);
}
