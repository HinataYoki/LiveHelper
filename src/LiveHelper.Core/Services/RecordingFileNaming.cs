using System.Text;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public static class RecordingFileNaming
{
    public const string SegmentPattern = "_part-%03d";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string BuildOutputPath(
        AppSettings settings,
        MonitoredRoom room,
        DateTimeOffset startedAt)
    {
        var platform = PlatformCatalog.Get(room.Platform);
        var roomId = ResolveRoomId(room);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{platform}"] = platform.Code,
            ["{platformName}"] = platform.DisplayName,
            ["{roomId}"] = roomId,
            ["{roomName}"] = room.EffectiveName,
            ["{anchor}"] = room.AnchorName,
            ["{title}"] = string.IsNullOrWhiteSpace(room.LiveTitle) ? room.EffectiveName : room.LiveTitle,
            ["{date}"] = startedAt.ToString("yyyyMMdd_HHmmss"),
            ["{time}"] = startedAt.ToString("HHmmss"),
            ["{year}"] = startedAt.ToString("yyyy"),
            ["{month}"] = startedAt.ToString("MM"),
            ["{day}"] = startedAt.ToString("dd")
        };

        var folderPath = BuildRelativeFolderPath(settings.FolderTemplate, values);
        var fileName = SanitizeSegment(
            RenderTemplate(settings.FileNameTemplate, values),
            $"{startedAt:yyyyMMdd_HHmmss}_{room.EffectiveName}",
            maxLength: 160);
        var extension = AppSettings.GetFileExtension(settings.RecordingFormat);
        return Path.Combine(settings.OutputDirectory, folderPath, fileName + extension);
    }

    /// <summary>
    /// Builds the ffmpeg segment filename pattern. Numbering keeps every part unique
    /// without relying on the template placeholders.
    /// </summary>
    public static string BuildSegmentPattern(
        AppSettings settings,
        MonitoredRoom room,
        DateTimeOffset startedAt)
    {
        var firstPath = BuildOutputPath(settings, room, startedAt);
        var directory = Path.GetDirectoryName(firstPath) ?? settings.OutputDirectory;
        var extension = Path.GetExtension(firstPath);
        var stem = SanitizeSegment(
            $"{Path.GetFileNameWithoutExtension(firstPath)}{SegmentPattern}",
            $"segment_%03d_{startedAt:yyyyMMdd_HHmmss}",
            maxLength: 150);
        return Path.Combine(directory, stem + extension);
    }

    /// <summary>
    /// Resolves the shared base path used by cover and danmaku sidecar files. Segment
    /// recordings use a printf-style pattern, so the pattern suffix is removed before
    /// appending the sidecar extension.
    /// </summary>
    public static string GetCompanionBasePath(string recordingPath)
    {
        var directory = Path.GetDirectoryName(recordingPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(recordingPath);
        var markerIndex = name.IndexOf("%03d", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return Path.Combine(directory, name);
        }

        name = name[..markerIndex];
        var partIndex = name.LastIndexOf("_part-", StringComparison.OrdinalIgnoreCase);
        if (partIndex >= 0)
        {
            name = name[..partIndex];
        }

        return Path.Combine(directory, name);
    }

    public static string BuildCompanionPath(string recordingPath, string extension) =>
        Path.ChangeExtension(GetCompanionBasePath(recordingPath), extension);

    public static string BuildDanmakuPath(string recordingPath) =>
        GetCompanionBasePath(recordingPath) + ".danmaku.jsonl";

    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}_{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法生成唯一的文件名。");
    }

    /// <summary>
    /// Returns a segment pattern whose numbering does not collide with an earlier
    /// recording in the same folder.
    /// </summary>
    public static string EnsureUniqueSegmentPattern(string pattern)
    {
        const string marker = "%03d";
        var directory = Path.GetDirectoryName(pattern) ?? string.Empty;
        var fileName = Path.GetFileName(pattern);
        var markerIndex = fileName.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return pattern;
        }

        var rawPrefix = fileName[..markerIndex];
        var prefix = rawPrefix.EndsWith("_part-", StringComparison.OrdinalIgnoreCase)
            ? rawPrefix[..^"_part-".Length]
            : rawPrefix;
        var suffix = fileName[(markerIndex + marker.Length)..];
        if (!HasExistingSegments(directory, rawPrefix, suffix))
        {
            return pattern;
        }

        for (var index = 2; index < 10_000; index++)
        {
            var stem = $"{prefix}_{index}_part-";
            if (!HasExistingSegments(directory, stem, suffix))
            {
                return Path.Combine(directory, stem + marker + suffix);
            }
        }

        throw new IOException("无法生成唯一的分段录制文件名。");
    }

    private static bool HasExistingSegments(string directory, string prefix, string suffix) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, prefix + "*" + suffix)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Any(name => TryParseSegmentIndex(name, prefix, suffix) is not null);

    private static int? TryParseSegmentIndex(string fileName, string prefix, string suffix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var length = fileName.Length - prefix.Length - suffix.Length;
        if (length <= 0)
        {
            return null;
        }

        var digits = fileName.AsSpan(prefix.Length, length);
        return int.TryParse(
            digits,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var index)
            ? index
            : null;
    }

    public static string RenderTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : template.Trim();
        foreach (var pair in values)
        {
            result = result.Replace(pair.Key, pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    public static string SanitizeSegment(string value, string fallback, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
            {
                continue;
            }

            var normalized = char.IsWhiteSpace(ch) ? ' ' : ch;
            if (normalized == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(normalized);
        }

        var result = builder.ToString().Trim(' ', '.');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd(' ', '.');
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            return fallback;
        }

        return ReservedNames.Contains(result)
            ? "_" + result
            : result;
    }

    public static string ResolveRoomId(MonitoredRoom room)
    {
        if (!string.IsNullOrWhiteSpace(room.PlatformRoomId))
        {
            return SanitizeSegment(room.PlatformRoomId, room.Id.ToString("N")[..8], maxLength: 64);
        }

        var parsed = PlatformDetector.TryExtractPlatformRoomId(room.Platform, room.Url);
        return SanitizeSegment(parsed ?? string.Empty, room.Id.ToString("N")[..8], maxLength: 64);
    }

    private static string BuildRelativeFolderPath(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        var rendered = RenderTemplate(template, values);
        var segments = rendered
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select((segment, index) => SanitizeSegment(
                segment,
                index == 0 ? "未分类" : "直播间",
                maxLength: 100))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Take(6)
            .ToArray();
        return segments.Length == 0
            ? "未分类"
            : Path.Combine(segments);
    }
}
