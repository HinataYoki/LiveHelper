using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LiveHelper.Core.Services;

namespace LiveHelper.Desktop;

internal static class EmbeddedRecordingEngine
{
    private const string ResourcePrefix = "LiveHelper.Desktop.NativeTools.";

    private static readonly HashSet<string> ExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ffmpeg",
        "ffmpeg.exe",
        "yt-dlp",
        "yt-dlp.exe"
    };

    public static void EnsureReady()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var rid = GetRuntimeIdentifier();
        var prefix = $"{ResourcePrefix}{rid}.";
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (resources.Length == 0)
        {
            throw new InvalidOperationException("当前安装包没有包含录制引擎，请重新安装 LiveHelper。");
        }

        var cacheKey = BuildCacheKey(assembly, rid, resources);
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveHelper",
            "Runtime",
            "recording-engine",
            cacheKey);
        Directory.CreateDirectory(cacheDirectory);

        var markerPath = Path.Combine(cacheDirectory, ".ready");
        var mediaResolverPath = Path.Combine(cacheDirectory, ResolveExecutableName("yt-dlp"));
        var mediaProcessorPath = Path.Combine(cacheDirectory, ResolveExecutableName("ffmpeg"));
        if (!File.Exists(markerPath)
            || !File.Exists(mediaResolverPath)
            || !File.Exists(mediaProcessorPath))
        {
            ExtractResources(assembly, prefix, resources, cacheDirectory);
            File.WriteAllText(markerPath, cacheKey);
        }

        EnsureExecutable(mediaResolverPath);
        EnsureExecutable(mediaProcessorPath);
        RecordingEngine.Configure(mediaResolverPath, mediaProcessorPath, cacheDirectory);
    }

    private static void ExtractResources(
        Assembly assembly,
        string prefix,
        IEnumerable<string> resources,
        string cacheDirectory)
    {
        foreach (var resourceName in resources)
        {
            var bundledName = resourceName[prefix.Length..];
            var isCompressed = bundledName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            var targetName = isCompressed
                ? Path.GetFileNameWithoutExtension(bundledName)
                : bundledName;
            if (string.IsNullOrWhiteSpace(targetName)
                || !string.Equals(Path.GetFileName(targetName), targetName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("录制引擎资源名称无效。");
            }

            var targetPath = Path.Combine(cacheDirectory, targetName);
            var temporaryPath = targetPath + ".tmp";
            using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("录制引擎资源读取失败。");
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 128,
                       FileOptions.SequentialScan))
            {
                if (isCompressed)
                {
                    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
                    gzip.CopyTo(output);
                }
                else
                {
                    resource.CopyTo(output);
                }

                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            if (ExecutableNames.Contains(targetName))
            {
                EnsureExecutable(targetPath);
            }
        }
    }

    private static string BuildCacheKey(
        Assembly assembly,
        string rid,
        IReadOnlyCollection<string> resources)
    {
        var builder = new StringBuilder();
        builder.AppendLine(assembly.GetName().Version?.ToString() ?? "0.0.0");
        builder.AppendLine(rid);
        foreach (var resourceName in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            builder.Append(resourceName);
            builder.Append(':');
            builder.AppendLine(stream?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string ResolveExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    private static string GetRuntimeIdentifier()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(rid))
        {
            return rid;
        }

        var os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{architecture}";
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

#pragma warning disable CA1416
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
    }
}
