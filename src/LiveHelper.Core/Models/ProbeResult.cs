namespace LiveHelper.Core.Models;

public enum ProbeState
{
    Live,
    Offline,
    Unavailable,
    Error
}

public sealed record ProbeResult(
    ProbeState State,
    string Title = "",
    string AnchorName = "",
    string ThumbnailUrl = "",
    int? ViewerCount = null,
    string? StreamUrl = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string Error = "",
    string Warning = "",
    string VideoCodec = "",
    string AudioCodec = "")
{
    public bool IsLive => State == ProbeState.Live;

    public bool CanRecord => IsLive && !string.IsNullOrWhiteSpace(StreamUrl);

    public static ProbeResult Offline(string title = "", string anchor = "") =>
        new(ProbeState.Offline, title, anchor);

    public static ProbeResult Failure(string error) =>
        new(ProbeState.Error, Error: error);

    public static ProbeResult Missing(string error) =>
        new(ProbeState.Unavailable, Error: error);
}
