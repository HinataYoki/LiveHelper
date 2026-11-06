namespace LiveHelper.Core.Services;

/// <summary>
/// Holds the private paths for the recording components extracted from the
/// application package. Callers never need to locate or configure executables.
/// </summary>
public static class RecordingEngine
{
    private static readonly object SyncRoot = new();
    private static string _mediaResolverPath = string.Empty;
    private static string _mediaProcessorPath = string.Empty;
    private static string _runtimeDirectory = string.Empty;

    public static bool IsReady =>
        IsAvailable(_mediaResolverPath) && IsAvailable(_mediaProcessorPath);

    public static string MediaResolverPath =>
        GetRequiredPath(_mediaResolverPath, "媒体解析组件");

    public static string MediaProcessorPath =>
        GetRequiredPath(_mediaProcessorPath, "媒体处理组件");

    public static string RuntimeDirectory => _runtimeDirectory;

    public static void Configure(
        string mediaResolverPath,
        string mediaProcessorPath,
        string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaResolverPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaProcessorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);

        if (!IsAvailable(mediaResolverPath) || !IsAvailable(mediaProcessorPath))
        {
            throw new InvalidOperationException("内置录制引擎尚未准备完成，请重新启动 LiveHelper。");
        }

        lock (SyncRoot)
        {
            _mediaResolverPath = mediaResolverPath;
            _mediaProcessorPath = mediaProcessorPath;
            _runtimeDirectory = runtimeDirectory;
        }
    }

    private static string GetRequiredPath(string path, string componentName)
    {
        if (IsAvailable(path))
        {
            return path;
        }

        throw new InvalidOperationException($"{componentName}尚未准备完成，请重新启动 LiveHelper。");
    }

    private static bool IsAvailable(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
