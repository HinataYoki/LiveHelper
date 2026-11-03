namespace LiveHelper.Core.Models;

public sealed class MonitoredRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public LivePlatform Platform { get; set; }

    public string Url { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AnchorName { get; set; } = string.Empty;

    public string PlatformRoomId { get; set; } = string.Empty;

    public bool IsMonitored { get; set; } = true;

    public bool AutoRecord { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastCheckedAt { get; set; }

    public bool IsLive { get; set; }

    public string LiveTitle { get; set; } = string.Empty;

    public string ThumbnailUrl { get; set; } = string.Empty;

    public int? ViewerCount { get; set; }

    public string LastError { get; set; } = string.Empty;

    public string LastWarning { get; set; } = string.Empty;

    public string LastRecordingPath { get; set; } = string.Empty;

    public DateTimeOffset? LastLiveStartedAt { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName
            : !string.IsNullOrWhiteSpace(AnchorName)
                ? AnchorName
                : $"{PlatformCatalog.Get(Platform).DisplayName}直播间";
}
