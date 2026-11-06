using System.Globalization;
using System.Text.Json;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed class YtDlpProbeService
{
    public async Task<ProbeResult> ProbeAsync(
        string url,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--dump-single-json",
            "--no-warnings",
            "--no-playlist",
            "--skip-download",
            "--no-progress",
            "--socket-timeout", "15",
            "--retries", "1",
            "--extractor-retries", "1"
        };

        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl))
        {
            arguments.Add("--proxy");
            arguments.Add(settings.ProxyUrl);
        }
        else
        {
            // yt-dlp can otherwise pick up http_proxy/https_proxy from the
            // environment, bypassing LiveHelper's deliberate direct connection.
            arguments.Add("--proxy");
            arguments.Add(string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(settings.Quality) && settings.Quality != "best")
        {
            arguments.Add("--format");
            arguments.Add(settings.Quality);
        }

        arguments.Add(url);

        var result = await ProcessRunner.RunAsync(
            RecordingEngine.MediaResolverPath,
            arguments,
            TimeSpan.FromSeconds(45),
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            return ProbeResult.Failure("媒体解析超时，请检查网络或代理设置。");
        }

        if (!result.Succeeded)
        {
            return ProbeResult.Failure("媒体解析暂时失败，请稍后重试。");
        }

        return ParseJson(result.StandardOutput, settings.Quality);
    }

    public static ProbeResult ParseJson(string json, string requestedQuality = "best")
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var title = GetString(root, "title");
            var anchor = FirstNonEmpty(
                GetString(root, "uploader"),
                GetString(root, "channel"),
                GetString(root, "creator"),
                GetString(root, "uploader_id"));
            var thumbnail = GetString(root, "thumbnail");
            var viewerCount = GetInt(root, "concurrent_view_count")
                              ?? GetInt(root, "view_count")
                              ?? GetInt(root, "concurrent_viewers");
            var liveStatus = GetString(root, "live_status");
            var liveFlag = GetBool(root, "is_live");
            var isLive = liveFlag ?? (liveStatus is "is_live" or "live" ? true
                : string.IsNullOrWhiteSpace(liveStatus) ? (bool?)null
                : false);

            var stream = ExtractStream(root, requestedQuality);
            if (isLive is null)
            {
                return new ProbeResult(
                    ProbeState.Live,
                    title,
                    anchor,
                    thumbnail,
                    viewerCount,
                    stream.Url,
                    stream.Headers,
                    Warning: string.IsNullOrWhiteSpace(stream.Url)
                        ? "媒体解析未返回明确的开播状态，也未返回媒体流。"
                        : "媒体解析未返回明确的开播状态，已按页面存在媒体流判断。",
                    VideoCodec: stream.VideoCodec,
                    AudioCodec: stream.AudioCodec);
            }

            if (!isLive.Value)
            {
                return ProbeResult.Offline(title, anchor);
            }

            return new ProbeResult(
                ProbeState.Live,
                title,
                anchor,
                thumbnail,
                viewerCount,
                stream.Url,
                stream.Headers,
                Warning: string.IsNullOrWhiteSpace(stream.Url)
                    ? "已开播，但媒体解析未返回可录制的媒体流。"
                    : string.Empty,
                VideoCodec: stream.VideoCodec,
                AudioCodec: stream.AudioCodec);
        }
        catch (JsonException)
        {
            return ProbeResult.Failure("媒体解析返回了无法识别的数据。");
        }
        catch (Exception)
        {
            return ProbeResult.Failure("媒体解析失败，请稍后重试。");
        }
    }

    public static string? ExtractStreamUrl(string json, string requestedQuality = "best") =>
        ExtractStreamFromDocument(json, requestedQuality).Url;

    private static StreamingFormat ExtractStreamFromDocument(
        string json,
        string requestedQuality)
    {
        using var document = JsonDocument.Parse(json);
        return ExtractStream(document.RootElement, requestedQuality);
    }

    internal sealed record StreamingFormat(
        string Url,
        IReadOnlyDictionary<string, string>? Headers,
        string VideoCodec,
        string AudioCodec);

    private static StreamingFormat ExtractStream(
        JsonElement root,
        string requestedQuality)
    {
        JsonElement? bestFormat = null;
        long bestScore = long.MinValue;
        JsonElement? bestCopyableFormat = null;
        long bestCopyableScore = long.MinValue;

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray())
            {
                var formatUrl = GetString(format, "url");
                if (!IsHttpUrl(formatUrl))
                {
                    continue;
                }

                var protocol = GetString(format, "protocol");
                var extension = GetString(format, "ext");
                if (protocol.Contains("mhtml", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals("mhtml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var videoCodec = GetString(format, "vcodec");
                if (videoCodec is "none")
                {
                    continue;
                }

                var audioCodec = GetString(format, "acodec");
                var hasAudio = !audioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
                var score = (GetLong(format, "height") ?? 0) * 1_000_000
                            + (GetLong(format, "tbr") ?? 0)
                            + (GetLong(format, "quality") ?? 0);
                if (hasAudio)
                {
                    score += 5_000_000_000;
                }

                if (extension is "fmp4")
                {
                    // fMP4 live variants carry fragmented MP4 headers. Handing one
                    // to a raw MP4 muxer produces an unplayable file, while FLV
                    // variants can be copied safely.
                    continue;
                }

                if (videoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)
                    || videoCodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase))
                {
                    score += 100_000;
                }

                if (requestedQuality != "best" && format.TryGetProperty("format_id", out var id)
                    && requestedQuality.Contains(id.ToString(), StringComparison.Ordinal))
                {
                    score += 100_000_000_000;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFormat = format;
                }

                if (IsMp4Friendly(videoCodec)
                    && (string.IsNullOrWhiteSpace(audioCodec)
                        || audioCodec.StartsWith("aac", StringComparison.OrdinalIgnoreCase)
                        || audioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase))
                    && score > bestCopyableScore)
                {
                    bestCopyableScore = score;
                    bestCopyableFormat = format;
                }
            }
        }

        var directUrl = GetString(root, "url");
        var copyable = bestCopyableFormat is not null && bestCopyableScore >= bestScore - 5_000_000_000;
        var chosen = copyable ? bestCopyableFormat : bestFormat;
        var selected = IsHttpUrl(directUrl) && bestFormat is null
            ? root
            : chosen ?? root;
        var url = IsHttpUrl(GetString(selected, "url")) ? GetString(selected, "url") : directUrl;
        if (!IsHttpUrl(url))
        {
            return new StreamingFormat(string.Empty, null, string.Empty, string.Empty);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(root, headers);
        AddHeaders(selected, headers);
        return new StreamingFormat(
            url,
            headers.Count > 0 ? headers : null,
            GetString(selected, "vcodec"),
            GetString(selected, "acodec"));
    }

    private static bool IsMp4Friendly(string videoCodec) =>
        videoCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)
        || videoCodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase)
        || videoCodec.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)
        || videoCodec.StartsWith("h265", StringComparison.OrdinalIgnoreCase)
        || videoCodec.StartsWith("av1", StringComparison.OrdinalIgnoreCase)
        || videoCodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase);

    private static void AddHeaders(JsonElement element, IDictionary<string, string> target)
    {
        if (!element.TryGetProperty("http_headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var header in headers.EnumerateObject())
        {
            if (header.Value.ValueKind == JsonValueKind.String)
            {
                target[header.Name] = header.Value.GetString() ?? string.Empty;
            }
        }
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        };
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        var value = GetLong(element, propertyName);
        return value is > int.MaxValue ? int.MaxValue : (int?)value;
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) => number,
            _ => null
        };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
