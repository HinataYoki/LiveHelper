using System.Text.Json;
using LiveHelper.Core.Models;
using LiveHelper.Core.Serialization;

namespace LiveHelper.Core.Services;

public sealed class JsonStateStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonStateStore(string? statePath = null)
    {
        StatePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveHelper",
            "state.json");
    }

    public string StatePath { get; }

    public string LastLoadError { get; private set; } = string.Empty;

    public async Task<PersistedState> LoadAsync(CancellationToken cancellationToken = default)
    {
        LastLoadError = string.Empty;
        if (!File.Exists(StatePath))
        {
            return new PersistedState();
        }

        try
        {
            await using var stream = File.OpenRead(StatePath);
            var state = await JsonSerializer.DeserializeAsync<PersistedState>(
                stream,
                LiveHelperJsonContext.Default.PersistedState,
                cancellationToken).ConfigureAwait(false) ?? new PersistedState();
            state.Settings ??= new AppSettings();
            state.Settings.Normalize();
            state.Rooms ??= [];
            state.Recordings ??= [];
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = $"读取配置失败：{ex.Message}";
            return new PersistedState();
        }
    }

    public async Task SaveAsync(PersistedState state, CancellationToken cancellationToken = default)
    {
        state.Settings.Normalize();
        var directory = Path.GetDirectoryName(StatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryPath = StatePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    LiveHelperJsonContext.Default.PersistedState,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
