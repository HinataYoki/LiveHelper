using LiveHelper.Core.Models;

namespace LiveHelper.Core.Services;

public sealed record RoomProbeUpdatedEvent(
    MonitoredRoom Room,
    ProbeResult Probe,
    bool RecordingStarted);

public sealed record MonitorStateChangedEvent(bool IsRunning, bool IsScanning);

public sealed class MonitoringService : IAsyncDisposable
{
    private readonly LiveProbeService _probeService;
    private readonly RecordingManager _recordingManager;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _roomsLock = new();
    private IReadOnlyList<MonitoredRoom> _rooms = [];
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public MonitoringService(LiveProbeService probeService, RecordingManager recordingManager)
    {
        _probeService = probeService;
        _recordingManager = recordingManager;
    }

    public event EventHandler<RoomProbeUpdatedEvent>? RoomUpdated;

    public event EventHandler<MonitorStateChangedEvent>? StateChanged;

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void SetRooms(IEnumerable<MonitoredRoom> rooms)
    {
        lock (_roomsLock)
        {
            _rooms = rooms.ToArray();
        }
    }

    public Task StartAsync(AppSettings settings)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _runCts = new CancellationTokenSource();
        _runTask = RunLoopAsync(settings, _runCts.Token);
        StateChanged?.Invoke(this, new MonitorStateChangedEvent(true, false));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _runCts;
        var task = _runTask;
        if (cts is null || task is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            cts.Dispose();
            _runCts = null;
            _runTask = null;
            StateChanged?.Invoke(this, new MonitorStateChangedEvent(false, false));
        }
    }

    public async Task<bool> ScanNowAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await ScanCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task<bool> CheckRoomNowAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await CheckRoomAsync(room, settings, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _scanGate.Dispose();
        await _recordingManager.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = DateTimeOffset.Now;
            if (await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ScanCoreAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _scanGate.Release();
                }
            }

            var interval = TimeSpan.FromSeconds(Math.Clamp(settings.MonitorIntervalSeconds, 10, 3600));
            var elapsed = DateTimeOffset.Now - startedAt;
            var delay = interval > elapsed ? interval - elapsed : TimeSpan.FromSeconds(2);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitoredRoom> snapshot;
        lock (_roomsLock)
        {
            snapshot = _rooms.Where(room => room.IsMonitored).ToArray();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        StateChanged?.Invoke(this, new MonitorStateChangedEvent(IsRunning, true));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(settings.MaxConcurrentChecks, 1, 12)
        };

        try
        {
            await Parallel.ForEachAsync(snapshot, options, async (room, token) =>
            {
                await CheckRoomAsync(room, settings, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            StateChanged?.Invoke(this, new MonitorStateChangedEvent(IsRunning, false));
        }
    }

    private async Task CheckRoomAsync(
        MonitoredRoom room,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ProbeResult probe;
        try
        {
            probe = await _probeService.ProbeAsync(room, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            probe = ProbeResult.Failure($"检查失败：{ex.Message}");
        }

        var wasLive = room.IsLive;
        room.LastCheckedAt = DateTimeOffset.Now;
        room.LiveTitle = string.IsNullOrWhiteSpace(probe.Title) ? room.LiveTitle : probe.Title;
        room.AnchorName = string.IsNullOrWhiteSpace(probe.AnchorName) ? room.AnchorName : probe.AnchorName;
        room.ThumbnailUrl = string.IsNullOrWhiteSpace(probe.ThumbnailUrl)
            ? room.ThumbnailUrl
            : probe.ThumbnailUrl;
        room.ViewerCount = probe.ViewerCount;
        room.LastWarning = probe.Warning;
        room.LastError = probe.State is ProbeState.Error or ProbeState.Unavailable ? probe.Error : string.Empty;

        switch (probe.State)
        {
            case ProbeState.Live:
                room.IsLive = true;
                room.ConsecutiveFailures = 0;
                if (!wasLive || room.LastLiveStartedAt is null)
                {
                    room.LastLiveStartedAt = DateTimeOffset.Now;
                }
                break;
            case ProbeState.Offline:
                room.IsLive = false;
                room.ConsecutiveFailures = 0;
                room.ViewerCount = null;
                room.LastError = string.Empty;
                break;
            case ProbeState.Error:
            case ProbeState.Unavailable:
                room.ConsecutiveFailures++;
                break;
        }

        var recordingStarted = false;
        if (room.IsLive && room.AutoRecord && settings.RecordingEnabled
            && !_recordingManager.IsRecording(room.Id))
        {
            if (probe.CanRecord)
            {
                var start = await _recordingManager.StartAsync(room, probe, settings, cancellationToken)
                    .ConfigureAwait(false);
                if (start.Started)
                {
                    recordingStarted = true;
                    room.LastRecordingPath = start.FilePath;
                }
                else
                {
                    room.LastWarning = Combine(room.LastWarning, start.Error);
                }
            }
            else if (string.IsNullOrWhiteSpace(room.LastWarning))
            {
                room.LastWarning = "直播间已开播，但未获得可录制的媒体流。";
            }
        }

        RoomUpdated?.Invoke(this, new RoomProbeUpdatedEvent(room, probe, recordingStarted));
    }

    private static string Combine(params string[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
}
