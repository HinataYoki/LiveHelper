namespace LiveHelper.Core.Models;

public enum LivePlatform
{
    Bilibili,
    Douyin,
    Huya,
    Douyu,
    Kuaishou,
    Twitch,
    YouTube,
    TikTok,
    Xiaohongshu,
    Chzzk,
    Direct,
    Generic
}

public sealed record PlatformDescriptor(
    LivePlatform Platform,
    string DisplayName,
    string Code,
    string AccentHex,
    string Hint);

public static class PlatformCatalog
{
    private static readonly IReadOnlyDictionary<LivePlatform, PlatformDescriptor> Descriptors =
        new Dictionary<LivePlatform, PlatformDescriptor>
        {
            [LivePlatform.Bilibili] = new(LivePlatform.Bilibili, "哔哩哔哩", "BILI", "#FF6B8A", "live.bilibili.com / b23.tv"),
            [LivePlatform.Douyin] = new(LivePlatform.Douyin, "抖音", "DY", "#1A1A1A", "live.douyin.com / v.douyin.com"),
            [LivePlatform.Huya] = new(LivePlatform.Huya, "虎牙", "HUYA", "#FF8A1F", "huya.com"),
            [LivePlatform.Douyu] = new(LivePlatform.Douyu, "斗鱼", "DOUYU", "#FF6A00", "douyu.com"),
            [LivePlatform.Kuaishou] = new(LivePlatform.Kuaishou, "快手", "KS", "#FF4D00", "live.kuaishou.com"),
            [LivePlatform.Twitch] = new(LivePlatform.Twitch, "Twitch", "TW", "#9146FF", "twitch.tv"),
            [LivePlatform.YouTube] = new(LivePlatform.YouTube, "YouTube", "YT", "#FF0033", "youtube.com / youtu.be"),
            [LivePlatform.TikTok] = new(LivePlatform.TikTok, "TikTok", "TT", "#111111", "tiktok.com"),
            [LivePlatform.Xiaohongshu] = new(LivePlatform.Xiaohongshu, "小红书", "RED", "#FF2442", "xiaohongshu.com / xhslink.com"),
            [LivePlatform.Chzzk] = new(LivePlatform.Chzzk, "CHZZK", "CZ", "#00E77B", "chzzk.naver.com"),
            [LivePlatform.Direct] = new(LivePlatform.Direct, "直连流", "URL", "#2F7D68", "m3u8 / flv / mp4 直链"),
            [LivePlatform.Generic] = new(LivePlatform.Generic, "通用平台", "WEB", "#52606D", "由内置媒体解析自动识别")
        };

    public static PlatformDescriptor Get(LivePlatform platform) =>
        Descriptors.TryGetValue(platform, out var descriptor)
            ? descriptor
            : Descriptors[LivePlatform.Generic];

    public static IReadOnlyList<PlatformDescriptor> All { get; } =
        Descriptors.Values.Where(item => item.Platform != LivePlatform.Generic).ToArray();
}
