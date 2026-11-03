namespace LiveHelper.Core.Models;

public sealed record RecordingStartedEvent(
    Guid RoomId,
    string RoomName,
    LivePlatform Platform,
    string FilePath,
    DateTimeOffset StartedAt,
    int ProcessId);

/// <summary>
/// Raised once for each completed media file from a single recording session.
/// Segment mode emits one event per part when ffmpeg closes that part.
/// </summary>
public sealed record RecordingStoppedEvent(
    Guid RoomId,
    string RoomName,
    LivePlatform Platform,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ExitCode,
    string Error,
    string Title = "");

public sealed record RecordingHistoryItem(
    Guid Id,
    Guid RoomId,
    string RoomName,
    LivePlatform Platform,
    string Title,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long SizeBytes,
    string Error);

public sealed record ToolHealth(
    string Name,
    bool IsAvailable,
    string Version,
    string Error)
{
    public static ToolHealth Missing(string name, string error) =>
        new(name, false, string.Empty, error);
}
