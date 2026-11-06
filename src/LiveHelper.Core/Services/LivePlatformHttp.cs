using System.Net;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

internal static class LivePlatformHttp
{
    public static HttpClient CreateClient(
        string? proxyUrl,
        CookieContainer? cookieContainer = null,
        TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookieContainer ?? new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12)
        };

        ConfigureProxy(handler, proxyUrl);

        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
    }

    public static void ConfigureProxy(SocketsHttpHandler handler, string? proxyUrl)
    {
        var value = AppSettings.NormalizeProxyUrl(proxyUrl);
        if (value.Length == 0)
        {
            // HttpClient otherwise inherits the macOS/Windows system proxy. Domestic
            // live pages can return anti-bot pages when they unexpectedly pass through
            // a proxy, while ffmpeg records the stream directly.
            handler.UseProxy = false;
            handler.Proxy = null;
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var proxyUri)
            || string.IsNullOrWhiteSpace(proxyUri.Host))
        {
            throw new ArgumentException("代理 URL 无效，请填写类似 http://127.0.0.1:7890 的地址。");
        }

        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxyUri);
    }
}
