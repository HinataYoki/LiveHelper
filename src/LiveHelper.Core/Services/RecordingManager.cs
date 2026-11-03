using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed record RecordingStartResult(bool Started, string FilePath, string Error)
{
    public static RecordingStartResult Failure(string error) => new(false, string.Empty, error);
}

public sealed class RecordingManager : IAsyncDisposable
{
    private sealed class ActiveRecording(
        MonitoredRoom room,
        string filePath,
        Process process,
        DateTimeOffset startedAt,
        int segmentMinutes,
        string title)
    {
        public MonitoredRoom Room { get; } = room;
        public string FilePath { get; } = filePath;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public int SegmentMinutes { get; } = segmentMinutes;
        public string Title { get; } = title;
        public StringBuilder ErrorLog { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DanmakuStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BilibiliDanmakuService.BilibiliDanmakuSession? Danmaku { get; set; }
        public int CompletionClaimed;
    }

    private readonly ConcurrentDictionary<Guid, ActiveRecording> _active = new();
    private readonly ToolLocator _tools = new();
    private readonly BilibiliDanmakuService _danmaku = new();
    private static readonly HttpClient ThumbnailClient = CreateThumbnailClient();

    public event EventHandler<RecordingStartedEvent>? RecordingStarted;

    public event EventHandler<RecordingStoppedEvent>? RecordingStopped;

    public event EventHandler<string>? Warning;

    public bool IsRecording(Guid roomId) => _active.ContainsKey(roomId);

    public int ActiveCount => _active.Count;

    public async Task<RecordingStartResult> StartAsync(
        MonitoredRoom room,
        ProbeResult probe,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!probe.CanRecord || string.IsNullOrWhiteSpace(probe.StreamUrl))
        {
            return RecordingStartResult.Failure("当前没有可录制的媒体流。");
        }

        if (_active.ContainsKey(room.Id))
        {
            return RecordingStartResult.Failure("该直播间已经在录制。");
        }

        if (_active.Count >= settings.MaxConcurrentRecordings)
        {
            return RecordingStartResult.Failure(
                $"已达到并发录制上限（{settings.MaxConcurrentRecordings}）。");
        }

        var ffmpeg = await _tools.CheckFfmpegAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!ffmpeg.IsAvailable)
        {
            return RecordingStartResult.Failure("未找到 ffmpeg，请在设置中填写可执行文件路径。");
        }

        var startedAt = DateTimeOffset.Now;
        var segmented = settings.SegmentMinutes > 0;
        var desiredPath = segmented
            ? RecordingFileNaming.BuildSegmentPattern(settings, room, startedAt)
            : RecordingFileNaming.BuildOutputPath(settings, room, startedAt);
        var outputDirectory = Path.GetDirectoryName(desiredPath)
            ?? settings.OutputDirectory;
        Directory.CreateDirectory(outputDirectory);
        var filePath = segmented
            ? RecordingFileNaming.EnsureUniqueSegmentPattern(desiredPath)
            : EnsureUniquePath(desiredPath);
        var sessionPath = RecordingFileNaming.GetCompanionBasePath(filePath)
                          + Path.GetExtension(filePath);
        var title = string.IsNullOrWhiteSpace(probe.Title) ? room.LiveTitle : probe.Title;

        var startInfo = new ProcessStartInfo
        {
            FileName = ToolLocator.Resolve(settings.FfmpegPath, "ffmpeg"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildFfmpegArguments(probe, settings, filePath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errorLog = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            lock (errorLog)
            {
                if (errorLog.Length > 12_000)
                {
                    errorLog.Remove(0, 4_000);
                }

                errorLog.AppendLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return RecordingStartResult.Failure("ffmpeg 进程未能启动。");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return RecordingStartResult.Failure($"启动 ffmpeg 失败：{ex.Message}");
        }

        var active = new ActiveRecording(
            room,
            filePath,
            process,
            startedAt,
            segmented ? settings.SegmentMinutes : 0,
            title);
        if (!_active.TryAdd(room.Id, active))
        {
            TryKill(process);
            process.Dispose();
            return RecordingStartResult.Failure("该直播间已经在录制。");
        }

        process.Exited += (_, _) => _ = CompleteAsync(active);
        process.BeginErrorReadLine();

        RecordingStarted?.Invoke(this, new RecordingStartedEvent(
            room.Id,
            room.EffectiveName,
            room.Platform,
            sessionPath,
            startedAt,
            process.Id));

        if (settings.SaveThumbnail && !string.IsNullOrWhiteSpace(probe.ThumbnailUrl))
        {
            _ = SaveThumbnailAsync(active, probe.ThumbnailUrl, probe.Headers);
        }

        if (settings.SaveDanmaku && BilibiliDanmakuService.CanRecord(room))
        {
            _ = StartDanmakuAsync(room, active, cancellationToken);
        }
        else
        {
            active.DanmakuStarted.TrySetResult();
        }

        return new RecordingStartResult(
            true,
            sessionPath,
            string.Empty);
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(
        ProbeResult probe,
        AppSettings settings,
        string outputPath)
    {
        var arguments = new List<string>();
        void Add(string argument) => arguments.Add(argument);
        void AddPair(string key, string value)
        {
            Add(key);
            Add(value);
        }

        Add("-hide_banner");
        Add("-loglevel");
        Add("warning");
        Add("-nostats");
        Add("-y");
        var isHttp = Uri.TryCreate(probe.StreamUrl, UriKind.Absolute, out var streamUri)
                     && (streamUri.Scheme == Uri.UriSchemeHttp
                         || streamUri.Scheme == Uri.UriSchemeHttps);
        if (isHttp)
        {
            Add("-reconnect");
            Add("1");
            Add("-reconnect_streamed");
            Add("1");
            Add("-reconnect_at_eof");
            Add("1");
            Add("-reconnect_delay_max");
            Add("10");
            Add("-rw_timeout");
            Add("15000000");
        }

        Add("-protocol_whitelist");
        Add("file,http,https,tcp,tls,crypto");

        var headers = probe.Headers ?? new Dictionary<string, string>();
        if (TryGetHeader(headers, "User-Agent", out var userAgent))
        {
            AddPair("-user_agent", userAgent);
        }

        if (TryGetHeader(headers, "Referer", out var referer))
        {
            AddPair("-referer", referer);
        }

        var otherHeaders = headers
            .Where(pair => !pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                           && !pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var header in otherHeaders)
        {
            AddPair("-headers", $"{header.Key}: {header.Value}\r\n");
        }

        Add("-i");
        Add(probe.StreamUrl!);
        Add("-map");
        Add("0:v?");
        Add("-map");
        Add("0:a?");
        Add("-c");
        Add("copy");
        Add("-avoid_negative_ts");
        Add("make_zero");

        var format = AppSettings.NormalizeRecordingFormat(settings.RecordingFormat);
        var segmented = settings.SegmentMinutes > 0
                        && outputPath.Contains("%03d", StringComparison.Ordinal);
        if (segmented)
        {
            Add("-f");
            Add("segment");
            AddPair("-segment_time", (settings.SegmentMinutes * 60).ToString());
            AddPair("-reset_timestamps", "1");
            AddPair("-segment_format", GetMuxerName(format));
            if (format == "flv")
            {
                AddPair("-segment_format_options", "flvflags=no_duration_filesize");
            }
        }
        else
        {
            Add("-f");
            Add(GetMuxerName(format));
        }

        if (ShouldTranscode(probe, format))
        {
            // MP4 and FLV cannot store every live codec combination. Unknown metadata is
            // treated conservatively; TS/MKV always keep the original stream.
            Add("-c:v");
            Add("libx264");
            Add("-preset");
            Add("veryfast");
            Add("-c:a");
            Add("aac");
            Add("-b:a");
            Add("192k");
            if (format == "mp4")
            {
                AddPair("-movflags", "+faststart");
            }
        }

        Add(outputPath);
        return arguments;
    }

    public static bool ShouldTranscode(ProbeResult probe, string? format)
    {
        var normalized = AppSettings.NormalizeRecordingFormat(format);
        if (normalized is not ("mp4" or "flv"))
        {
            return false;
        }

        if (normalized == "mp4" && IsFlvStream(probe.StreamUrl))
        {
            // FLV live CDNs can jump DTS mid-stream. Stream-copying that timeline
            // into MP4 produces files whose duration is far longer than real time.
            return true;
        }

        var video = (probe.VideoCodec ?? string.Empty).ToLowerInvariant();
        var audio = (probe.AudioCodec ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(video) || video == "none")
        {
            return true;
        }

        var videoCompatible = normalized == "mp4"
            ? video.StartsWith("avc", StringComparison.Ordinal)
              || video.StartsWith("h264", StringComparison.Ordinal)
              || video.StartsWith("hevc", StringComparison.Ordinal)
              || video.StartsWith("h265", StringComparison.Ordinal)
              || video.StartsWith("av1", StringComparison.Ordinal)
              || video.StartsWith("vp9", StringComparison.Ordinal)
            : video.StartsWith("avc", StringComparison.Ordinal)
              || video.StartsWith("h264", StringComparison.Ordinal);
        var audioCompatible = string.IsNullOrWhiteSpace(audio)
            || audio == "none"
            || audio.StartsWith("aac", StringComparison.Ordinal)
            || audio.StartsWith("mp4a", StringComparison.Ordinal);
        return !videoCompatible || !audioCompatible;
    }

    private static bool IsFlvStream(string? streamUrl) =>
        Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri)
        && (uri.AbsolutePath.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("live-bvc", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("flv", StringComparison.OrdinalIgnoreCase));

    private static async Task SaveThumbnailAsync(
        ActiveRecording active,
        string thumbnailUrl,
        IReadOnlyDictionary<string, string>? headers)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, thumbnailUrl);
            if (headers is not null && TryGetHeader(headers, "User-Agent", out var userAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }

            if (headers is not null && TryGetHeader(headers, "Referer", out var referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }

            using var response = await ThumbnailClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                            ?? string.Empty;
            var extension = mediaType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/avif" => ".avif",
                _ => ".jpg"
            };
            var path = RecordingFileNaming.BuildCompanionPath(active.FilePath, extension);
            await using var output = File.Create(path);
            await response.Content.CopyToAsync(output).ConfigureAwait(false);
        }
        catch
        {
            // Covers are a convenience; recording must never fail because of one.
        }
    }

    private async Task StartDanmakuAsync(
        MonitoredRoom room,
        ActiveRecording active,
        CancellationToken cancellationToken)
    {
        try
        {
            active.Danmaku = await _danmaku
                .StartAsync(
                    room,
                    active.FilePath,
                    cancellationToken,
                    session => active.Danmaku = session)
                .ConfigureAwait(false);
        }
        catch
        {
            // Danmaku recording is optional; the media file remains the primary output.
        }
        finally
        {
            active.DanmakuStarted.TrySetResult();
        }

        if (active.Danmaku is null)
        {
            Warning?.Invoke(this, "弹幕录制未能连接，视频录制不受影响。");
        }
    }

    private static HttpClient CreateThumbnailClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        return client;
    }

    public static string GetMuxerName(string? format) =>
        AppSettings.NormalizeRecordingFormat(format) switch
        {
            "mkv" => "matroska",
            "mp4" => "mp4",
            "flv" => "flv",
            _ => "mpegts"
        };

    public async Task StopAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue(roomId, out var active))
        {
            return;
        }

        try
        {
            await active.Process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await active.Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If ffmpeg is no longer reading stdin, fall through to the wait and kill path.
        }

        try
        {
            await active.Exit.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(active.Process);
            await active.Exit.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAllAsync()
    {
        var roomIds = _active.Keys.ToArray();
        await Task.WhenAll(roomIds.Select(id => StopAsync(id))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        await _danmaku.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CompleteAsync(ActiveRecording active)
    {
        if (Interlocked.Exchange(ref active.CompletionClaimed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseDanmakuAsync(active).ConfigureAwait(false);
            NotifyStopped(active);
        }
        finally
        {
            _active.TryRemove(active.Room.Id, out _);
            active.Exit.TrySetResult(SafeExitCode(active.Process));
            active.Completed.TrySetResult();
            active.Process.Dispose();
        }
    }

    private void NotifyStopped(ActiveRecording active)
    {
        if (active.Danmaku is { HasFaulted: true } danmaku
            && !string.IsNullOrWhiteSpace(danmaku.LastError))
        {
            Warning?.Invoke(this, $"弹幕录制中断：{danmaku.LastError}");
        }

        var exitCode = SafeExitCode(active.Process);

        string error;
        lock (active.ErrorLog)
        {
            error = active.ErrorLog.ToString().Trim();
        }

        if (error.Length > 600)
        {
            error = error[^600..];
        }

        var endedAt = DateTimeOffset.Now;
        if (active.SegmentMinutes > 0)
        {
            foreach (var (filePath, partStartedAt, partEndedAt) in EnumerateSegments(active))
            {
                var partError = error;
                if (string.IsNullOrWhiteSpace(partError) && !IsUsableRecording(filePath))
                {
                    partError = "录制文件未完整写入，可能是直播流中断或封装失败。";
                }

                RecordingStopped?.Invoke(this, new RecordingStoppedEvent(
                    active.Room.Id,
                    active.Room.EffectiveName,
                    active.Room.Platform,
                    filePath,
                    partStartedAt,
                    partEndedAt,
                    exitCode,
                    partError,
                    active.Title));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(error) && !IsUsableRecording(active.FilePath))
            {
                error = "录制文件未完整写入，可能是直播流中断或封装失败。";
            }

            RecordingStopped?.Invoke(this, new RecordingStoppedEvent(
                active.Room.Id,
                active.Room.EffectiveName,
                active.Room.Platform,
                active.FilePath,
                active.StartedAt,
                endedAt,
                exitCode,
                error,
                active.Title));
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task CloseDanmakuAsync(ActiveRecording active)
    {
        await Task.WhenAny(
            active.DanmakuStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);

        if (active.Danmaku is null)
        {
            return;
        }

        try
        {
            await active.Danmaku.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A failed danmaku socket must not lose the video history entry.
        }
    }

    internal static bool IsUsableRecording(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                return false;
            }

            if (file.Extension is not (".mp4" or ".m4v"))
            {
                return true;
            }

            using var stream = file.OpenRead();
            var offset = 0L;
            var box = new byte[8];
            var extended = new byte[8];
            while (offset + 8 <= stream.Length)
            {
                stream.Position = offset;
                if (stream.Read(box) < box.Length)
                {
                    return false;
                }

                var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan());
                var type = System.Text.Encoding.ASCII.GetString(box, 4, 4);
                if (type is "moov" or "moof")
                {
                    return true;
                }

                if (size < 8)
                {
                    return false;
                }

                if (size == 1)
                {
                    if (stream.Read(extended) < extended.Length)
                    {
                        return false;
                    }

                    var extendedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
                        extended.AsSpan());
                    if (extendedSize > long.MaxValue)
                    {
                        return false;
                    }

                    offset += (long)extendedSize;
                }
                else
                {
                    offset += size;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<(string FilePath, DateTimeOffset StartedAt, DateTimeOffset EndedAt)>
        EnumerateSegments(ActiveRecording active)
    {
        var directory = Path.GetDirectoryName(active.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        var pattern = Path.GetFileName(active.FilePath);
        var marker = "%03d";
        var markerIndex = pattern.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            yield break;
        }

        var prefix = pattern[..markerIndex];
        var suffix = pattern[(markerIndex + marker.Length)..];
        var files = Directory
            .EnumerateFiles(directory, prefix + "*" + suffix)
            .Where(path => path.Length >= prefix.Length + suffix.Length)
            .Where(path => IsSegmentFile(Path.GetFileName(path), prefix, suffix))
            .Where(path =>
            {
                try
                {
                    return new FileInfo(path).Length > 0;
                }
                catch
                {
                    return false;
                }
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            yield break;
        }

        for (var index = 0; index < files.Length; index++)
        {
            var endedAt = SafeLastWriteTime(files[index]);
            var startedAt = index == 0
                ? active.StartedAt
                : SafeLastWriteTime(files[index - 1]);
            if (endedAt < startedAt)
            {
                endedAt = startedAt;
            }

            yield return (files[index], startedAt, endedAt);
        }
    }

    private static bool IsSegmentFile(string fileName, string prefix, string suffix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return digits.Length >= 3 && digits[..3].ToString().All(char.IsAsciiDigit);
    }

    private static DateTimeOffset SafeLastWriteTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToLocalTime();
        }
        catch
        {
            return DateTimeOffset.Now;
        }
    }

    private static bool TryGetHeader(
        IReadOnlyDictionary<string, string> headers,
        string name,
        out string value)
    {
        foreach (var pair in headers)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string EnsureUniquePath(string path)
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

        throw new IOException("无法生成唯一的录制文件名。");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
