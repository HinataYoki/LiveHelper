using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

/// <summary>
/// Resolves Douyin live rooms without any external tool: the room page already embeds a
/// server-rendered store that carries live status, metadata and pull URLs.
/// </summary>
public sealed class DouyinLiveResolver
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

    private const string HomeUrl = "https://live.douyin.com/";

    // Inside the page this appears as \"roomStore\":{\"roomInfo\":{ ...
    private const string RoomStoreKey = """\"roomStore\":""";
    private const string RoomStoreMarker = RoomStoreKey + """{\"roomInfo\":{""";
    private const int RoomStoreWindowLength = 400_000;

    private static readonly string[] StreamQualityOrder = ["FULL_HD1", "HD1", "SD1", "SD2", "LD1"];

    private readonly HttpClient _httpClient;
    private bool _cookiesPrimed;

    public DouyinLiveResolver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    /// <summary>
    /// True when the address points at a live room (or a short link that may do so).
    /// Other Douyin addresses are left to yt-dlp.
    /// </summary>
    public static bool IsLiveRoomUrl(string url)
    {
        if (!TryCreateUri(url, out var uri))
        {
            return false;
        }

        return ExtractRoomId(uri) is not null || IsShortLinkHost(uri.Host);
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveRoomAsync(url, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } room)
            {
                return ProbeResult.Missing("无法从该抖音地址中解析直播间号，请使用直播间分享链接。");
            }

            var html = await DownloadAsync(room.PageUrl, cancellationToken).ConfigureAwait(false);
            var result = ParseRoomPage(html, room.RoomId);
            if (result.State != ProbeState.Error)
            {
                return result;
            }

            // A cold cookie jar occasionally returns a shell page; prime cookies once and retry.
            await PrimeCookiesAsync(cancellationToken).ConfigureAwait(false);
            html = await DownloadAsync(room.PageUrl, cancellationToken).ConfigureAwait(false);
            var retry = ParseRoomPage(html, room.RoomId);
            return retry.State == ProbeState.Error ? result : retry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return ProbeResult.Failure($"抖音直播间页面请求失败：{ex.Message}");
        }
    }

    public static ProbeResult ParseRoomPage(string html, string roomId)
    {
        var markerIndex = html.LastIndexOf(RoomStoreMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return ProbeResult.Failure("抖音页面未返回直播间数据，请确认链接有效或稍后重试。");
        }

        var jsonStart = markerIndex + RoomStoreKey.Length;
        var windowLength = Math.Min(RoomStoreWindowLength, html.Length - jsonStart);
        var text = JsUnescape(html.AsSpan(jsonStart, windowLength));
        var jsonEnd = FindJsonEnd(text, 0);
        if (jsonEnd <= 0)
        {
            return ProbeResult.Failure("抖音页面返回的直播间数据不完整。");
        }

        try
        {
            using var document = JsonDocument.Parse(text[..jsonEnd]);
            return BuildResult(document.RootElement, roomId);
        }
        catch (JsonException)
        {
            return ProbeResult.Failure("抖音页面结构已变化，无法解析直播间数据。");
        }
    }

    private static ProbeResult BuildResult(JsonElement roomStore, string roomId)
    {
        if (!roomStore.TryGetProperty("roomInfo", out var roomInfo) || roomInfo.ValueKind != JsonValueKind.Object)
        {
            return ProbeResult.Failure("抖音页面缺少直播间信息。");
        }

        if (roomInfo.TryGetProperty("room", out var room) && room.ValueKind == JsonValueKind.Object)
        {
            var title = GetString(room, "title");
            var anchor = GetNestedString(room, "owner", "nickname");
            if (string.IsNullOrWhiteSpace(anchor))
            {
                // Ended rooms drop the owner block but keep the anchor on the store.
                anchor = GetNestedString(roomInfo, "anchor", "nickname");
            }

            var status = GetInt(room, "status");
            if (status != 2)
            {
                return ProbeResult.Offline(title, anchor);
            }

            var cover = GetFirstArrayString(room, "cover", "url_list");
            var viewers = ParseViewerCount(GetString(room, "user_count_str"));
            var streamUrl = FindStreamUrl(room);
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                return new ProbeResult(
                    ProbeState.Live,
                    title,
                    anchor,
                    cover,
                    viewers,
                    Warning: "直播间已开播，但未从页面解析到可录制的媒体流。");
            }

            return new ProbeResult(
                ProbeState.Live,
                title,
                anchor,
                cover,
                viewers,
                streamUrl,
                CreateStreamHeaders());
        }

        // Not live: the store only carries the room shell (no room object).
        var offlineAnchor = GetNestedString(roomInfo, "anchor", "nickname");
        if (string.Equals(offlineAnchor, roomId, StringComparison.Ordinal))
        {
            offlineAnchor = string.Empty;
        }

        return ProbeResult.Offline(string.Empty, offlineAnchor);
    }

    private static string? FindStreamUrl(JsonElement room)
    {
        if (!room.TryGetProperty("stream_url", out var stream) || stream.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var mapName in new[] { "flv_pull_url", "hls_pull_url_map" })
        {
            if (!stream.TryGetProperty(mapName, out var map) || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var quality in StreamQualityOrder)
            {
                if (map.TryGetProperty(quality, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && IsHttpUrl(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }

        return IsHttpUrl(GetString(stream, "hls_pull_url")) ? GetString(stream, "hls_pull_url") : null;
    }

    private static IReadOnlyDictionary<string, string> CreateStreamHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Referer"] = HomeUrl
        };

    private async Task<DouyinRoomTarget?> ResolveRoomAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryCreateUri(url, out var uri))
        {
            return null;
        }

        var roomId = ExtractRoomId(uri);
        if (roomId is null && IsShortLinkHost(uri.Host))
        {
            using var response = await _httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var target = response.RequestMessage?.RequestUri;
            roomId = target is null ? null : ExtractRoomId(target);
        }

        return roomId is null ? null : new DouyinRoomTarget(roomId, $"{HomeUrl}{roomId}");
    }

    private async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(HomeUrl);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PrimeCookiesAsync(CancellationToken cancellationToken)
    {
        if (_cookiesPrimed)
        {
            return;
        }

        _cookiesPrimed = true;
        try
        {
            using var response = await _httpClient
                .GetAsync(HomeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            // Cookie priming is best-effort only.
        }
    }

    public static string? ExtractRoomId(string url) =>
        TryCreateUri(url, out var uri) ? ExtractRoomId(uri) : null;

    private static bool TryCreateUri(string? url, out Uri uri)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            uri = null!;
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out uri!);
    }

    private static string? ExtractRoomId(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!host.Equals("douyin.com", StringComparison.Ordinal)
            && !host.EndsWith(".douyin.com", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var key in new[] { "web_rid", "room_id", "rid" })
        {
            if (TryGetQueryValue(uri.Query, key, out var value) && IsRoomId(value))
            {
                return value;
            }
        }

        if (IsShortLinkHost(host))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host.Equals("live.douyin.com", StringComparison.Ordinal))
        {
            return segments.FirstOrDefault(IsRoomId);
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("live", StringComparison.OrdinalIgnoreCase) && IsRoomId(segments[index + 1]))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    private static bool IsShortLinkHost(string host) =>
        host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.v.douyin.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("m.douyin.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsRoomId(string? value) =>
        value is { Length: >= 6 and <= 24 } && value.All(char.IsAsciiDigit);

    private static bool TryGetQueryValue(string query, string key, out string value)
    {
        value = string.Empty;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!pair.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return true;
        }

        return false;
    }

    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static int? ParseViewerCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        if (text.Contains('万', StringComparison.Ordinal))
        {
            number *= 10_000;
        }

        return number > int.MaxValue ? int.MaxValue : (int)number;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string GetNestedString(JsonElement element, string parentName, string propertyName) =>
        element.TryGetProperty(parentName, out var parent) && parent.ValueKind == JsonValueKind.Object
            ? GetString(parent, propertyName)
            : string.Empty;

    private static string GetFirstArrayString(JsonElement element, string parentName, string arrayName)
    {
        if (!element.TryGetProperty(parentName, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(arrayName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                return item.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Undoes one layer of JavaScript string escaping. Structural quotes arrive as \" while
    /// quotes inside values arrive as \\\" and stay escaped for the JSON parser.
    /// </summary>
    private static string JsUnescape(ReadOnlySpan<char> source)
    {
        var builder = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (current != '\\')
            {
                builder.Append(current);
                continue;
            }

            if (index + 1 >= source.Length)
            {
                break;
            }

            var next = source[index + 1];
            index++;
            switch (next)
            {
                case '"':
                    builder.Append('"');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '/':
                    builder.Append('/');
                    break;
                case '\'':
                    builder.Append('\'');
                    break;
                case 'n':
                case 'r':
                case 't':
                case 'b':
                case 'f':
                case 'u':
                    // Keep JSON escapes intact so the JSON parser decodes them.
                    builder.Append('\\').Append(next);
                    break;
                default:
                    builder.Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    private static int FindJsonEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return index + 1;
                    }

                    break;
            }
        }

        return -1;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        return client;
    }

    private readonly record struct DouyinRoomTarget(string RoomId, string PageUrl);
}
