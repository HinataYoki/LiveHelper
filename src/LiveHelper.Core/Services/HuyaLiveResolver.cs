using System.Net;
using System.Text.Json;

namespace LiveHelper.Core.Services;

/// <summary>
/// Resolves the live/offline state that the Huya extractor omits from its metadata.
/// The room page carries TT_ROOM_DATA with explicit isOn/isOff/state fields.
/// </summary>
public sealed class HuyaLiveResolver
{
    private const string RoomDataMarker = "var TT_ROOM_DATA = ";
    private readonly HttpClient _httpClient;

    public HuyaLiveResolver(HttpClient? httpClient = null, string? proxyUrl = null)
    {
        _httpClient = httpClient ?? CreateHttpClient(proxyUrl);
    }

    public async Task<HuyaRoomState?> ProbeRoomStateAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return ParseRoomData(html);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns null when the page does not contain a recognizable room state.
    /// </summary>
    public static HuyaRoomState? ParseRoomData(string html)
    {
        var markerIndex = html.IndexOf(RoomDataMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var jsonStart = markerIndex + RoomDataMarker.Length;
        var jsonEnd = FindJsonEnd(html, jsonStart);
        if (jsonEnd <= jsonStart)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(html[jsonStart..jsonEnd]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var isOn = GetBool(root, "isOn");
            var isOff = GetBool(root, "isOff");
            var isReplay = GetBool(root, "isReplay");
            var state = GetString(root, "state");
            var title = GetString(root, "introduction");
            var anchor = GetString(root, "privateHost");
            var viewers = GetInt(root, "totalCount");
            var cover = NormalizeImageUrl(GetString(root, "screenshot"));

            if (isOn is null && isOff is null && string.IsNullOrWhiteSpace(state))
            {
                return null;
            }

            var isLive = isOn == true
                         || (isOn is null
                             && isOff != true
                             && isReplay != true
                             && state?.Equals("ON", StringComparison.OrdinalIgnoreCase) == true);
            return new HuyaRoomState(
                isLive,
                title,
                anchor,
                cover,
                viewers is > int.MaxValue ? int.MaxValue : (int?)viewers);
        }
        catch (JsonException)
        {
            return null;
        }
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
                }
                else if (current == '"')
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
                    depth++;
                    break;
                case '}':
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

    private static long? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string NormalizeImageUrl(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;
    }

    private static HttpClient CreateHttpClient(string? proxyUrl)
    {
        var client = LivePlatformHttp.CreateClient(
            proxyUrl,
            timeout: TimeSpan.FromSeconds(18));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.huya.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        return client;
    }
}

public sealed record HuyaRoomState(
    bool IsLive,
    string Title,
    string AnchorName,
    string ThumbnailUrl,
    int? ViewerCount);
