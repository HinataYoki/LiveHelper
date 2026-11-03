using System.Net;
using System.Text.Json;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed class BilibiliLiveResolver
{
    private const string ApiBase = "https://api.live.bilibili.com";
    private readonly HttpClient _httpClient;

    public BilibiliLiveResolver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<ProbeResult> ProbeMetadataAsync(string url, CancellationToken cancellationToken)
    {
        var roomId = await ResolveRoomIdAsync(url, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return ProbeResult.Failure("无法从 B 站地址中解析房间号。");
        }

        using var initJson = await GetApiDataAsync(
            $"{ApiBase}/room/v1/Room/room_init?id={Uri.EscapeDataString(roomId)}",
            cancellationToken).ConfigureAwait(false);

        var realRoomId = ReadString(initJson.RootElement, "room_id");
        if (string.IsNullOrWhiteSpace(realRoomId))
        {
            realRoomId = roomId;
        }

        using var infoJson = await GetApiDataAsync(
            $"{ApiBase}/room/v1/Room/get_info?room_id={Uri.EscapeDataString(realRoomId)}",
            cancellationToken).ConfigureAwait(false);

        var info = infoJson.RootElement;
        var liveStatus = ReadInt(info, "live_status");
        var title = ReadString(info, "title");
        var cover = NormalizeImageUrl(ReadString(info, "user_cover"));
        var viewerCount = ReadInt(info, "online");
        var anchor = await TryGetAnchorNameAsync(realRoomId, cancellationToken).ConfigureAwait(false);

        if (liveStatus != 1)
        {
            return ProbeResult.Offline(title, anchor);
        }

        return new ProbeResult(
            ProbeState.Live,
            title,
            anchor,
            cover,
            viewerCount);
    }

    private async Task<string?> ResolveRoomIdAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var roomId = PlatformDetector.ExtractBilibiliRoomId(uri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            return roomId;
        }

        if (uri.Host is not ("b23.tv" or "www.b23.tv"))
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return response.RequestMessage?.RequestUri is { } finalUri
            ? PlatformDetector.ExtractBilibiliRoomId(finalUri.AbsolutePath)
            : null;
    }

    private async Task<string> TryGetAnchorNameAsync(string roomId, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetApiDataAsync(
                $"{ApiBase}/live_user/v1/UserInfo/get_anchor_in_room?roomid={Uri.EscapeDataString(roomId)}",
                cancellationToken).ConfigureAwait(false);

            if (json.RootElement.TryGetProperty("info", out var info))
            {
                return ReadString(info, "uname");
            }
        }
        catch
        {
            // The room endpoint is enough to monitor; anchor metadata is best-effort.
        }

        return string.Empty;
    }

    private async Task<JsonDocument> GetApiDataAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new HttpRequestException("B 站接口触发了风控（HTTP 412），请降低检查频率或配置网络环境。");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var code = ReadInt(root, "code");
        if (code is not null and not 0)
        {
            var message = ReadString(root, "message");
            document.Dispose();
            throw new HttpRequestException($"B 站接口返回错误 {code}: {message}");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("B 站接口未返回直播间数据。");
        }

        return JsonDocument.Parse(data.GetRawText());
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(18)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }
}
