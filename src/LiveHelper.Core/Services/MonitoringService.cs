using System.Diagnostics;
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
    private readonly Dictionary<Guid, DateTimeOffset> _nextCheckAt = [];
    private IReadOnlyList<MonitoredRoom> _rooms = [];
    private MacBackgroundActivity? _backgroundActivity;
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
            var roomIds = _rooms.Select(room => room.Id).ToHashSet();
            foreach (var staleId in _nextCheckAt.Keys.Where(id => !roomIds.Contains(id)).ToArray())
            {
                _nextCheckAt.Remove(staleId);
            }

            var firstCheckAt = DateTimeOffset.UtcNow.AddSeconds(1);
            foreach (var room in _rooms)
            {
                _nextCheckAt.TryAdd(
                    room.Id,
                    firstCheckAt.AddMilliseconds(Random.Shared.Next(0, 2000)));
            }
        }
    }

    public Task StartAsync(AppSettings settings)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        ResetSchedule();
        _backgroundActivity = MacBackgroundActivity.TryStart(settings.PreventSleepWhileMonitoring);
        _runCts = new CancellationTokenSource();
        _runTask = RunLoopAsync(settings, _runCts.Token);
        StateChanged?.Invoke(this, new MonitorStateChangedEvent(true, false));
        return Task.CompletedTask;
    }

    public DateTimeOffset? GetNextCheckAt()
    {
        lock (_roomsLock)
        {
            DateTimeOffset? next = null;
            foreach (var room in _rooms)
            {
                if (!room.IsMonitored
                    || !_nextCheckAt.TryGetValue(room.Id, out var dueAt))
                {
                    continue;
                }

                if (next is null || dueAt < next)
                {
                    next = dueAt;
                }
            }

            return next;
        }
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
            _backgroundActivity?.Dispose();
            _backgroundActivity = null;
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
            var rooms = GetRoomsSnapshot();
            await ScanCoreAsync(rooms, settings, cancellationToken).ConfigureAwait(false);
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
            ScheduleNextCheck(room, settings);
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
            try
            {
                var dueRooms = GetDueRooms(DateTimeOffset.UtcNow);
                if (await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (dueRooms.Count > 0)
                        {
                            await ScanCoreAsync(dueRooms, settings, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _scanGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Monitoring loop failed: {ex}");
            }

            try
            {
                await Task.Delay(GetWakeDelay(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ScanCoreAsync(
        IReadOnlyList<MonitoredRoom> rooms,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (rooms.Count == 0)
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
            await Parallel.ForEachAsync(rooms, options, async (room, token) =>
            {
                try
                {
                    await CheckRoomAsync(room, settings, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    MarkCheckFailure(room, ex);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                foreach (var room in rooms)
                {
                    ScheduleNextCheck(room, settings);
                }
            }

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

    private void MarkCheckFailure(MonitoredRoom room, Exception ex)
    {
        room.LastCheckedAt = DateTimeOffset.Now;
        room.LastError = $"检查失败：{ex.Message}";
        room.ConsecutiveFailures++;
        RoomUpdated?.Invoke(
            this,
            new RoomProbeUpdatedEvent(room, ProbeResult.Failure(room.LastError), false));
    }

    private IReadOnlyList<MonitoredRoom> GetRoomsSnapshot()
    {
        lock (_roomsLock)
        {
            return _rooms.Where(room => room.IsMonitored).ToArray();
        }
    }

    private IReadOnlyList<MonitoredRoom> GetDueRooms(DateTimeOffset now)
    {
        lock (_roomsLock)
        {
            return _rooms
                .Where(room => room.IsMonitored
                               && (!_nextCheckAt.TryGetValue(room.Id, out var dueAt) || dueAt <= now))
                .ToArray();
        }
    }

    private TimeSpan GetWakeDelay()
    {
        lock (_roomsLock)
        {
            DateTimeOffset? next = null;
            foreach (var room in _rooms)
            {
                if (!room.IsMonitored
                    || !_nextCheckAt.TryGetValue(room.Id, out var dueAt))
                {
                    continue;
                }

                if (next is null || dueAt < next)
                {
                    next = dueAt;
                }
            }

            if (next is null)
            {
                return TimeSpan.FromSeconds(5);
            }

            var delay = next.Value - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
            {
                return TimeSpan.FromSeconds(1);
            }

            return delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
        }
    }

    private void ResetSchedule()
    {
        lock (_roomsLock)
        {
            var firstCheckAt = DateTimeOffset.UtcNow.AddSeconds(1);
            foreach (var room in _rooms.Where(room => room.IsMonitored))
            {
                _nextCheckAt[room.Id] = firstCheckAt.AddMilliseconds(Random.Shared.Next(0, 1500));
            }
        }
    }

    private void ScheduleNextCheck(MonitoredRoom room, AppSettings settings)
    {
        var configured = Math.Clamp(settings.MonitorIntervalSeconds, 10, 3600);
        var baseSeconds = room.Platform == LivePlatform.Douyin
            ? Math.Max(configured, 90)
            : configured;
        var failures = Math.Clamp(room.ConsecutiveFailures, 0, 4);
        if (failures > 0)
        {
            baseSeconds = Math.Min(baseSeconds * (1 << (failures - 1)), 1800);
        }

        var jittered = baseSeconds * (0.85 + Random.Shared.NextDouble() * 0.30);
        var delay = TimeSpan.FromSeconds(Math.Clamp(jittered, 10, 3600));
        lock (_roomsLock)
        {
            _nextCheckAt[room.Id] = DateTimeOffset.UtcNow + delay;
        }
    }
}
