using System.Text.Json;
using LiveHelper.Core.Models;
using LiveHelper.Core.Serialization;

namespace LiveHelper.Core.Services;

public sealed record WorkspaceLoadResult(
    string Directory,
    string ConfigPath,
    PersistedState State,
    bool LoadedExistingConfig,
    bool MigratedLegacyState,
    string Notice);

/// <summary>
/// Keeps the portable configuration next to recordings. Only the location of the
/// last workspace is stored in the local application-data directory.
/// </summary>
public sealed class WorkspaceStateStore
{
    public const string ConfigFileName = "LiveHelper.config.json";

    private readonly string _pointerPath;
    private readonly string _legacyStatePath;

    public WorkspaceStateStore(
        string? pointerPath = null,
        string? legacyStatePath = null)
    {
        var localData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveHelper");
        _pointerPath = pointerPath ?? Path.Combine(localData, "workspace.json");
        _legacyStatePath = legacyStatePath ?? Path.Combine(localData, "state.json");
    }

    public string LastLoadError { get; private set; } = string.Empty;

    public string CurrentWorkspaceDirectory { get; private set; } = string.Empty;

    public static string GetConfigPath(string directory) =>
        Path.Combine(Path.GetFullPath(directory), ConfigFileName);

    public static bool HasWorkspaceConfig(string directory) =>
        File.Exists(GetConfigPath(directory));

    public async Task<WorkspaceLoadResult> LoadStartupAsync(
        CancellationToken cancellationToken = default)
    {
        LastLoadError = string.Empty;
        var pointer = await LoadPointerAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(pointer.CurrentWorkspace)
            && HasWorkspaceConfig(pointer.CurrentWorkspace))
        {
            return await LoadWorkspaceAsync(pointer.CurrentWorkspace, cancellationToken)
                .ConfigureAwait(false);
        }

        if (File.Exists(_legacyStatePath))
        {
            var legacyStore = new JsonStateStore(_legacyStatePath);
            var legacyState = await legacyStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(legacyStore.LastLoadError))
            {
                LastLoadError = legacyStore.LastLoadError;
            }

            var migrated = await OpenOrCreateAsync(
                legacyState.Settings.OutputDirectory,
                legacyState,
                cancellationToken).ConfigureAwait(false);
            return migrated with
            {
                MigratedLegacyState = true,
                Notice = "旧版配置已迁移到录制工作区，原 state.json 已保留作为备份。"
            };
        }

        var initial = new PersistedState();
        return await OpenOrCreateAsync(
            initial.Settings.OutputDirectory,
            initial,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceLoadResult> OpenOrCreateAsync(
        string directory,
        PersistedState fallbackState,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("工作区目录不能为空。", nameof(directory));
        }

        var fullDirectory = Path.GetFullPath(directory.Trim());
        Directory.CreateDirectory(fullDirectory);
        var configPath = GetConfigPath(fullDirectory);
        if (File.Exists(configPath))
        {
            var result = await LoadWorkspaceAsync(fullDirectory, cancellationToken)
                .ConfigureAwait(false);
            await SavePointerAsync(fullDirectory, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var state = CloneState(fallbackState);
        state.Settings.OutputDirectory = fullDirectory;
        state.Settings.Normalize();
        WorkspaceUpgrade.Apply(state);
        RelocateWorkspacePaths(state, fullDirectory, fallbackState.Settings.OutputDirectory);
        await new JsonStateStore(configPath).SaveAsync(state, cancellationToken)
            .ConfigureAwait(false);
        await SavePointerAsync(fullDirectory, cancellationToken).ConfigureAwait(false);
        CurrentWorkspaceDirectory = fullDirectory;

        return new WorkspaceLoadResult(
            fullDirectory,
            configPath,
            state,
            LoadedExistingConfig: false,
            MigratedLegacyState: false,
            Notice: "已创建新的可移动录制工作区。");
    }

    public async Task SaveAsync(
        PersistedState state,
        CancellationToken cancellationToken = default)
    {
        var directory = string.IsNullOrWhiteSpace(CurrentWorkspaceDirectory)
            ? state.Settings.OutputDirectory
            : CurrentWorkspaceDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("尚未选择录制工作区。");
        }

        state.Settings.OutputDirectory = Path.GetFullPath(directory);
        state.Settings.Normalize();
        Directory.CreateDirectory(directory);
        await new JsonStateStore(GetConfigPath(directory))
            .SaveAsync(state, cancellationToken)
            .ConfigureAwait(false);
        await SavePointerAsync(directory, cancellationToken).ConfigureAwait(false);
        CurrentWorkspaceDirectory = Path.GetFullPath(directory);
    }

    private async Task<WorkspaceLoadResult> LoadWorkspaceAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var configPath = GetConfigPath(fullDirectory);
        var store = new JsonStateStore(configPath);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(store.LastLoadError))
        {
            LastLoadError = store.LastLoadError;
        }

        RelocateWorkspacePaths(state, fullDirectory);
        var upgraded = WorkspaceUpgrade.Apply(state);
        if (upgraded)
        {
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        CurrentWorkspaceDirectory = fullDirectory;
        return new WorkspaceLoadResult(
            fullDirectory,
            configPath,
            state,
            LoadedExistingConfig: true,
            MigratedLegacyState: false,
            Notice: upgraded
                ? $"已升级工作区配置并载入：{fullDirectory}"
                : $"已载入工作区配置：{fullDirectory}");
    }

    private async Task<WorkspacePointer> LoadPointerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_pointerPath))
        {
            return new WorkspacePointer();
        }

        try
        {
            await using var stream = File.OpenRead(_pointerPath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                LiveHelperJsonContext.Default.WorkspacePointer,
                cancellationToken).ConfigureAwait(false) ?? new WorkspacePointer();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = $"读取最近工作区失败：{ex.Message}";
            return new WorkspacePointer();
        }
    }

    private async Task SavePointerAsync(string directory, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(_pointerPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var pointer = new WorkspacePointer { CurrentWorkspace = Path.GetFullPath(directory) };
        var temporaryPath = _pointerPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                pointer,
                LiveHelperJsonContext.Default.WorkspacePointer,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _pointerPath, overwrite: true);
    }

    private static void RelocateWorkspacePaths(PersistedState state, string workspaceDirectory)
    {
        var previousDirectory = state.Settings.OutputDirectory;
        RelocateWorkspacePaths(state, workspaceDirectory, previousDirectory);
    }

    private static void RelocateWorkspacePaths(
        PersistedState state,
        string workspaceDirectory,
        string previousDirectory)
    {
        state.Settings.OutputDirectory = workspaceDirectory;
        state.Settings.Normalize();
        if (string.IsNullOrWhiteSpace(previousDirectory)
            || string.Equals(
                Path.GetFullPath(previousDirectory),
                workspaceDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (var index = 0; index < state.Recordings.Count; index++)
        {
            var item = state.Recordings[index];
            state.Recordings[index] = item with
            {
                FilePath = RelocatePath(item.FilePath, previousDirectory, workspaceDirectory),
                Mp4Path = RelocatePath(item.Mp4Path, previousDirectory, workspaceDirectory)
            };
        }

        foreach (var room in state.Rooms)
        {
            room.LastRecordingPath = RelocatePath(
                room.LastRecordingPath,
                previousDirectory,
                workspaceDirectory);
        }
    }

    private static string RelocatePath(
        string path,
        string previousDirectory,
        string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(previousDirectory), path);
            if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative == ".."
                || Path.IsPathRooted(relative))
            {
                return path;
            }

            return Path.Combine(workspaceDirectory, relative);
        }
        catch
        {
            return path;
        }
    }

    private static PersistedState CloneState(PersistedState state) => new()
    {
        Version = PersistedState.CurrentVersion,
        Settings = state.Settings.Clone(),
        Rooms = state.Rooms.Select(CloneRoom).ToList(),
        Recordings = state.Recordings.ToList()
    };

    private static MonitoredRoom CloneRoom(MonitoredRoom room) => new()
    {
        Id = room.Id,
        Platform = room.Platform,
        Url = room.Url,
        DisplayName = room.DisplayName,
        AnchorName = room.AnchorName,
        PlatformRoomId = room.PlatformRoomId,
        IsMonitored = room.IsMonitored,
        AutoRecord = room.AutoRecord,
        CreatedAt = room.CreatedAt,
        LastCheckedAt = room.LastCheckedAt,
        IsLive = room.IsLive,
        LiveTitle = room.LiveTitle,
        ThumbnailUrl = room.ThumbnailUrl,
        ViewerCount = room.ViewerCount,
        LastError = room.LastError,
        LastWarning = room.LastWarning,
        LastRecordingPath = room.LastRecordingPath,
        LastLiveStartedAt = room.LastLiveStartedAt,
        ConsecutiveFailures = room.ConsecutiveFailures
    };
}
