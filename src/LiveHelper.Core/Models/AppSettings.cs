namespace LiveHelper.Core.Models;

public sealed class AppSettings
{
    public const string DefaultFolderTemplate = "{platform}_{roomId}";

    public const string DefaultFileNameTemplate = "{date}_{title}";

    public const string DefaultRecordingFormat = "mp4";

    public static readonly string[] SupportedRecordingFormats = ["ts", "mkv", "mp4", "flv"];

    public string OutputDirectory { get; set; } = GetDefaultOutputDirectory();

    public string FolderTemplate { get; set; } = DefaultFolderTemplate;

    public string FileNameTemplate { get; set; } = DefaultFileNameTemplate;

    public int MonitorIntervalSeconds { get; set; } = 30;

    public int MaxConcurrentChecks { get; set; } = 4;

    public int MaxConcurrentRecordings { get; set; } = 3;

    public bool StartMonitoringOnLaunch { get; set; } = true;

    public bool RecordingEnabled { get; set; } = true;

    public string YtDlpPath { get; set; } = "yt-dlp";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string ProxyUrl { get; set; } = string.Empty;

    public string RecordingFormat { get; set; } = DefaultRecordingFormat;

    public string Quality { get; set; } = "best";

    public int SegmentMinutes { get; set; }

    public bool SaveThumbnail { get; set; } = true;

    public bool SaveDanmaku { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool StartMinimized { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? GetDefaultOutputDirectory()
            : OutputDirectory.Trim();
        FolderTemplate = string.IsNullOrWhiteSpace(FolderTemplate)
            ? DefaultFolderTemplate
            : FolderTemplate.Trim();
        FileNameTemplate = RemoveKnownExtension(string.IsNullOrWhiteSpace(FileNameTemplate)
            ? DefaultFileNameTemplate
            : FileNameTemplate.Trim());
        MonitorIntervalSeconds = Math.Clamp(MonitorIntervalSeconds, 10, 3600);
        MaxConcurrentChecks = Math.Clamp(MaxConcurrentChecks, 1, 12);
        MaxConcurrentRecordings = Math.Clamp(MaxConcurrentRecordings, 1, 12);
        RecordingFormat = NormalizeRecordingFormat(RecordingFormat);
        Quality = string.IsNullOrWhiteSpace(Quality) ? "best" : Quality.Trim();
        SegmentMinutes = Math.Clamp(SegmentMinutes, 0, 1440);
        YtDlpPath = string.IsNullOrWhiteSpace(YtDlpPath) ? "yt-dlp" : YtDlpPath.Trim();
        FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim();
        ProxyUrl = ProxyUrl.Trim();
    }

    private static string RemoveKnownExtension(string template) =>
        SupportedRecordingFormats.Any(format =>
            template.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))
            ? Path.GetFileNameWithoutExtension(template)
            : template;

    public static string NormalizeRecordingFormat(string? format)
    {
        var normalized = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return SupportedRecordingFormats.Contains(normalized) ? normalized : DefaultRecordingFormat;
    }

    public static string GetFileExtension(string? format) =>
        "." + NormalizeRecordingFormat(format);

    public static string GetDefaultOutputDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(videos, "LiveHelper");
    }
}
