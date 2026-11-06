namespace LiveHelper.Core.Models;

public sealed class PersistedState
{
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    public AppSettings Settings { get; set; } = new();

    public List<MonitoredRoom> Rooms { get; set; } = [];

    public List<RecordingHistoryItem> Recordings { get; set; } = [];
}
