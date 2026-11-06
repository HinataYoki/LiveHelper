using System.Diagnostics;
using System.Text;
using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed record RecordingConversionResult(bool Succeeded, string OutputPath, string Error)
{
    public static RecordingConversionResult Failure(string error) =>
        new(false, string.Empty, error);
}

/// <summary>
/// Converts a completed recording to MP4 without touching the live recording
/// process. The stream is copied rather than re-encoded whenever possible.
/// </summary>
public sealed class RecordingConverter
{
    public async Task<RecordingConversionResult> ConvertToMp4Async(
        string inputPath,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return RecordingConversionResult.Failure("录制文件不存在。");
        }

        var extension = Path.GetExtension(inputPath);
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return RecordingConversionResult.Failure("该文件已经是 MP4 格式。");
        }

        var outputPath = RecordingFileNaming.EnsureUniquePath(
            Path.ChangeExtension(inputPath, ".mp4"));
        var temporaryPath = outputPath + ".part";
        var errorLog = new StringBuilder();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ToolLocator.Resolve(settings.FfmpegPath, "ffmpeg"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in BuildFfmpegArguments(inputPath, temporaryPath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
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
                    return RecordingConversionResult.Failure("ffmpeg 未能启动。");
                }
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return RecordingConversionResult.Failure($"启动 ffmpeg 失败：{ex.Message}");
            }

            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                return RecordingConversionResult.Failure(
                    ReadError(errorLog, "MP4 转换失败，源文件仍保持完整。"));
            }

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                return RecordingConversionResult.Failure(
                    ReadError(errorLog, "MP4 转换完成，但没有生成有效文件。"));
            }

            if (File.Exists(outputPath))
            {
                outputPath = RecordingFileNaming.EnsureUniquePath(outputPath);
            }

            File.Move(temporaryPath, outputPath);
            return new RecordingConversionResult(true, outputPath, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return RecordingConversionResult.Failure("MP4 转换已取消。");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return RecordingConversionResult.Failure($"MP4 转换失败：{ex.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static IReadOnlyList<string> BuildFfmpegArguments(
        string inputPath,
        string outputPath) =>
    [
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostdin",
        "-nostats",
        "-y",
        "-fflags",
        "+genpts",
        "-i",
        inputPath,
        "-map",
        "0:v?",
        "-map",
        "0:a?",
        "-c",
        "copy",
        "-avoid_negative_ts",
        "make_zero",
        "-movflags",
        "+faststart",
        "-f",
        "mp4",
        outputPath
    ];

    private static string ReadError(StringBuilder errorLog, string fallback)
    {
        lock (errorLog)
        {
            var error = errorLog.ToString().Trim();
            if (error.Length > 1_200)
            {
                error = error[^1_200..];
            }

            return string.IsNullOrWhiteSpace(error) ? fallback : error;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed conversion never modifies the source recording.
        }
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
            // Best-effort cancellation.
        }
    }
}
