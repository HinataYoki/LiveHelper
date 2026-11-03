using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed record DetectedRoom(
    LivePlatform Platform,
    Uri Uri,
    string? ExternalId,
    string NormalizedUrl);

public static class PlatformDetector
{
    private static readonly string[] DirectExtensions = [".m3u8", ".flv", ".mp4", ".ts"];

    public static DetectedRoom Detect(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("直播间地址不能为空。", nameof(input));
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("请输入有效的 HTTP(S) 直播间地址。", nameof(input));
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        var platform = DetectPlatform(host, path);
        var externalId = TryExtractPlatformRoomId(platform, uri);

        return new DetectedRoom(platform, uri, externalId, uri.ToString());
    }

    public static string? ExtractBilibiliRoomId(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].All(char.IsAsciiDigit)
                && (index == segments.Length - 1
                    || !segments[index + 1].Contains('.')))
            {
                return segments[index];
            }
        }

        return null;
    }

    public static string? TryExtractPlatformRoomId(LivePlatform platform, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return TryExtractPlatformRoomId(platform, uri);
    }

    public static string? TryExtractPlatformRoomId(LivePlatform platform, Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return platform switch
        {
            LivePlatform.Bilibili => ExtractBilibiliRoomId(uri.AbsolutePath),
            LivePlatform.Douyin => FindDouyinRoomId(uri),
            LivePlatform.Huya => segments.FirstOrDefault(value => value.All(char.IsAsciiDigit)),
            LivePlatform.Douyu => segments.FirstOrDefault(value => value.All(char.IsAsciiDigit)),
            LivePlatform.Kuaishou => segments.Length >= 2
                && segments[0].Equals("u", StringComparison.OrdinalIgnoreCase)
                    ? segments[1]
                    : null,
            LivePlatform.Twitch => segments.FirstOrDefault(),
            LivePlatform.YouTube => FindYouTubeId(segments),
            LivePlatform.TikTok => FindTikTokId(uri.AbsolutePath),
            LivePlatform.Xiaohongshu => segments.LastOrDefault(),
            LivePlatform.Chzzk => segments.LastOrDefault(),
            LivePlatform.Direct => Path.GetFileNameWithoutExtension(uri.AbsolutePath),
            _ => null
        };
    }

    private static string? FindDouyinRoomId(Uri uri)
    {
        foreach (var key in new[] { "web_rid", "room_id", "rid" })
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0
                    || !pair.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
                if (value.Length is >= 6 and <= 24 && value.All(char.IsAsciiDigit))
                {
                    return value;
                }
            }
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments.FirstOrDefault(value =>
                value.Length is >= 6 and <= 24 && value.All(char.IsAsciiDigit));
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("live", StringComparison.OrdinalIgnoreCase)
                && segments[index + 1].Length is >= 6 and <= 24
                && segments[index + 1].All(char.IsAsciiDigit))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    private static string? FindYouTubeId(string[] segments)
    {
        var liveIndex = Array.FindIndex(segments, value =>
            value.Equals("live", StringComparison.OrdinalIgnoreCase));
        if (liveIndex >= 0 && liveIndex + 1 < segments.Length)
        {
            return segments[liveIndex + 1];
        }

        return segments.FirstOrDefault(value =>
            value.StartsWith("@", StringComparison.Ordinal)
            || value.StartsWith("channel/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("c/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindTikTokId(string path)
    {
        var marker = path.IndexOf("/live", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return null;
        }

        var handle = path[..marker].Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(handle) ? null : handle.TrimStart('@');
    }

    private static LivePlatform DetectPlatform(string host, string path)
    {
        if (host is "b23.tv" or "www.b23.tv"
            || host.EndsWith(".bilibili.com", StringComparison.Ordinal)
            || host == "bilibili.com")
        {
            return LivePlatform.Bilibili;
        }

        if (host.EndsWith(".douyin.com", StringComparison.Ordinal) || host == "douyin.com")
        {
            return LivePlatform.Douyin;
        }

        if (host.EndsWith(".huya.com", StringComparison.Ordinal) || host == "huya.com")
        {
            return LivePlatform.Huya;
        }

        if (host.EndsWith(".douyu.com", StringComparison.Ordinal) || host == "douyu.com")
        {
            return LivePlatform.Douyu;
        }

        if (host.EndsWith(".kuaishou.com", StringComparison.Ordinal) || host == "kuaishou.com")
        {
            return LivePlatform.Kuaishou;
        }

        if (host.EndsWith(".twitch.tv", StringComparison.Ordinal) || host == "twitch.tv")
        {
            return LivePlatform.Twitch;
        }

        if (host.EndsWith(".youtube.com", StringComparison.Ordinal)
            || host == "youtube.com"
            || host is "youtu.be" or "www.youtu.be")
        {
            return LivePlatform.YouTube;
        }

        if (host.EndsWith(".tiktok.com", StringComparison.Ordinal) || host == "tiktok.com")
        {
            return LivePlatform.TikTok;
        }

        if (host.EndsWith(".xiaohongshu.com", StringComparison.Ordinal)
            || host == "xiaohongshu.com"
            || host is "xhslink.com" or "www.xhslink.com")
        {
            return LivePlatform.Xiaohongshu;
        }

        if (host.EndsWith(".chzzk.naver.com", StringComparison.Ordinal) || host == "chzzk.naver.com")
        {
            return LivePlatform.Chzzk;
        }

        if (DirectExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return LivePlatform.Direct;
        }

        return LivePlatform.Generic;
    }
}
