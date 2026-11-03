using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed class ToolLocator
{
    public async Task<ToolHealth> CheckYtDlpAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            Resolve(settings.YtDlpPath, "yt-dlp"),
            ["--version"],
            TimeSpan.FromSeconds(12),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? new ToolHealth("yt-dlp", true, FirstLine(result.StandardOutput), string.Empty)
            : ToolHealth.Missing("yt-dlp", FriendlyError(result, settings.YtDlpPath));
    }

    public async Task<ToolHealth> CheckFfmpegAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            Resolve(settings.FfmpegPath, "ffmpeg"),
            ["-version"],
            TimeSpan.FromSeconds(12),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? new ToolHealth("ffmpeg", true, FirstLine(result.StandardOutput), string.Empty)
            : ToolHealth.Missing("ffmpeg", FriendlyError(result, settings.FfmpegPath));
    }

    public async Task<(ToolHealth YtDlp, ToolHealth Ffmpeg)> CheckAllAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var ytDlpTask = CheckYtDlpAsync(settings, cancellationToken);
        var ffmpegTask = CheckFfmpegAsync(settings, cancellationToken);
        await Task.WhenAll(ytDlpTask, ffmpegTask).ConfigureAwait(false);
        return (await ytDlpTask, await ffmpegTask);
    }

    /// <summary>
    /// Prefer an explicitly configured file path, then fall back to a bundled tool
    /// placed under <c>tools/</c> beside the executable.
    /// </summary>
    public static string Resolve(string configuredPath, string toolName)
    {
        if (IsExistingFile(configuredPath))
        {
            return configuredPath;
        }

        var fileName = OperatingSystem.IsWindows() ? $"{toolName}.exe" : toolName;
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", fileName);
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        // The settings default is a bare command name. Also look beside the process
        // so development builds and portable folders find their bundled tools even
        // when the working directory is different from AppContext.BaseDirectory.
        var besideProcess = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty,
            fileName);
        return File.Exists(besideProcess) ? besideProcess : configuredPath;
    }

    private static bool IsExistingFile(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                   && (Path.IsPathRooted(path) || path.Contains('/') || path.Contains('\\'))
                   && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string FirstLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "已安装";

    private static string FriendlyError(ProcessResult result, string command)
    {
        if (result.TimedOut)
        {
            return $"{command} 响应超时";
        }

        var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        if (error.Contains("An error occurred trying to start process", StringComparison.OrdinalIgnoreCase)
            || error.Contains("系统找不到", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return $"未找到或无法启动 {command}";
        }

        var firstLine = FirstLine(error);
        return string.IsNullOrWhiteSpace(firstLine) ? $"未找到或无法启动 {command}" : firstLine;
    }
}
