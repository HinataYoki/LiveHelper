using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed class LiveProbeService
{
    private readonly BilibiliLiveResolver _bilibili = new();
    private readonly DouyinLiveResolver _douyin = new();
    private readonly HuyaLiveResolver _huya = new();
    private readonly YtDlpProbeService _ytDlp = new();

    public async Task<ProbeResult> ProbeAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (room.Platform == LivePlatform.Direct)
        {
            return new ProbeResult(
                ProbeState.Live,
                room.EffectiveName,
                room.AnchorName,
                StreamUrl: room.Url);
        }

        if (room.Platform == LivePlatform.Bilibili)
        {
            return await ProbeBilibiliAsync(room, settings, cancellationToken).ConfigureAwait(false);
        }

        if (room.Platform == LivePlatform.Douyin && DouyinLiveResolver.IsLiveRoomUrl(room.Url))
        {
            return await ProbeDouyinAsync(room, settings, cancellationToken).ConfigureAwait(false);
        }

        if (room.Platform == LivePlatform.Huya)
        {
            return await ProbeHuyaAsync(room, settings, cancellationToken).ConfigureAwait(false);
        }

        return await _ytDlp.ProbeAsync(room.Url, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult> ProbeHuyaAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var roomState = await _huya.ProbeRoomStateAsync(room.Url, cancellationToken).ConfigureAwait(false);
        if (roomState is { IsLive: false })
        {
            return ProbeResult.Offline(roomState.Title, roomState.AnchorName);
        }

        var stream = await _ytDlp.ProbeAsync(room.Url, settings, cancellationToken).ConfigureAwait(false);
        if (roomState is null)
        {
            return stream;
        }

        if (stream.State == ProbeState.Unavailable)
        {
            return new ProbeResult(
                ProbeState.Live,
                string.IsNullOrWhiteSpace(stream.Title) ? roomState.Title : stream.Title,
                string.IsNullOrWhiteSpace(stream.AnchorName) ? roomState.AnchorName : stream.AnchorName,
                string.IsNullOrWhiteSpace(stream.ThumbnailUrl) ? roomState.ThumbnailUrl : stream.ThumbnailUrl,
                roomState.ViewerCount,
                Warning: "虎牙直播间已开播，但缺少 yt-dlp，暂时只能监控、不能录制。");
        }

        if (!stream.CanRecord)
        {
            return new ProbeResult(
                ProbeState.Live,
                string.IsNullOrWhiteSpace(stream.Title) ? roomState.Title : stream.Title,
                string.IsNullOrWhiteSpace(stream.AnchorName) ? roomState.AnchorName : stream.AnchorName,
                string.IsNullOrWhiteSpace(stream.ThumbnailUrl) ? roomState.ThumbnailUrl : stream.ThumbnailUrl,
                roomState.ViewerCount,
                Warning: string.IsNullOrWhiteSpace(stream.Error)
                    ? "直播间已开播，但 yt-dlp 未解析出媒体流。"
                    : stream.Error);
        }

        return new ProbeResult(
            ProbeState.Live,
            string.IsNullOrWhiteSpace(stream.Title) ? roomState.Title : stream.Title,
            string.IsNullOrWhiteSpace(stream.AnchorName) ? roomState.AnchorName : stream.AnchorName,
            string.IsNullOrWhiteSpace(stream.ThumbnailUrl) ? roomState.ThumbnailUrl : stream.ThumbnailUrl,
            roomState.ViewerCount,
            stream.StreamUrl,
            stream.Headers,
            VideoCodec: stream.VideoCodec,
            AudioCodec: stream.AudioCodec);
    }

    private async Task<ProbeResult> ProbeDouyinAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var result = await _douyin.ProbeAsync(room.Url, cancellationToken).ConfigureAwait(false);
        if (result.State != ProbeState.Unavailable)
        {
            return result;
        }

        // Short links can also point at ordinary Douyin videos, which yt-dlp handles.
        var fallback = await _ytDlp.ProbeAsync(room.Url, settings, cancellationToken).ConfigureAwait(false);
        return fallback.State == ProbeState.Unavailable
            ? result
            : fallback with { Warning = CombineWarnings(fallback.Warning, result.Error) };
    }

    private async Task<ProbeResult> ProbeBilibiliAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ProbeResult metadata;
        try
        {
            metadata = await _bilibili.ProbeMetadataAsync(room.Url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = await _ytDlp.ProbeAsync(room.Url, settings, cancellationToken).ConfigureAwait(false);
            if (fallback.State != ProbeState.Unavailable)
            {
                return fallback with
                {
                    Warning = CombineWarnings(
                        fallback.Warning,
                        $"B 站元数据接口不可用，已回退到 yt-dlp：{ex.Message}")
                };
            }

            return ProbeResult.Failure($"B 站接口不可用，且未找到 yt-dlp：{ex.Message}");
        }

        if (!metadata.IsLive)
        {
            return metadata;
        }

        var stream = await _ytDlp.ProbeAsync(room.Url, settings, cancellationToken).ConfigureAwait(false);
        if (stream.State == ProbeState.Unavailable)
        {
            return metadata with
            {
                Warning = "检测到 B 站直播间正在开播，但缺少 yt-dlp，暂时只能监控、不能录制。"
            };
        }

        if (stream.CanRecord)
        {
            return metadata with
            {
                Title = string.IsNullOrWhiteSpace(metadata.Title) ? stream.Title : metadata.Title,
                AnchorName = string.IsNullOrWhiteSpace(metadata.AnchorName) ? stream.AnchorName : metadata.AnchorName,
                ThumbnailUrl = string.IsNullOrWhiteSpace(metadata.ThumbnailUrl)
                    ? stream.ThumbnailUrl
                    : metadata.ThumbnailUrl,
                ViewerCount = metadata.ViewerCount ?? stream.ViewerCount,
                StreamUrl = stream.StreamUrl,
                Headers = stream.Headers,
                Warning = CombineWarnings(metadata.Warning, stream.Warning),
                VideoCodec = stream.VideoCodec,
                AudioCodec = stream.AudioCodec
            };
        }

        return metadata with
        {
            Warning = CombineWarnings(
                "直播间正在开播，但 yt-dlp 未能解析出媒体流。",
                stream.Error,
                stream.Warning)
        };
    }

    private static string CombineWarnings(params string[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
}
