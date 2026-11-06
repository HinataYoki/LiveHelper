using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using LiveHelper.Core.Models;
using LiveHelper.Core.Services;
using TKCode.Arc;

namespace LiveHelper.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const double CompactRecordingWidth = 1180;

    private enum Page
    {
        Overview,
        Rooms,
        Recordings,
        Settings
    }

    private readonly WorkspaceStateStore _workspaceStore = new();
    private readonly LiveProbeService _probeService = new();
    private readonly RecordingManager _recordingManager = new();
    private readonly RecordingConverter _recordingConverter = new();
    private readonly MonitoringService _monitoring;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, bool> _lastLiveStates = [];
    private readonly Dictionary<Guid, string> _lastErrors = [];
    private readonly Dictionary<Guid, string> _lastWarnings = [];
    private readonly DateTimeOffset _sessionStartedAt = DateTimeOffset.Now;
    private double _windowWidth = 1440;
    private PersistedState _state = new();
    private Page _activePage = Page.Overview;
    private bool _initialized;
    private bool _shuttingDown;
    private bool _uiActive = true;
    private bool _isMonitoring;
    private bool _isScanning;
    private bool _isAddRoomOpen;
    private DateTimeOffset? _nextScanAt;
    private DateTimeOffset? _lastScanCompletedAt;
    private DateTimeOffset? _feedbackUntil;
    private DateTimeOffset _lastActivationScanAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _conversionCts = new();
    private string _newRoomUrl = string.Empty;
    private string _newRoomName = string.Empty;
    private bool _newRoomAutoRecord = true;
    private string _newRoomPlatformName = "粘贴链接后自动识别";
    private string _newRoomPlatformHint = "支持 B 站、抖音、虎牙、斗鱼、快手、Twitch、YouTube 等";
    private string _addRoomError = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _workspaceConfigPath = string.Empty;
    private string _folderTemplate = AppSettings.DefaultFolderTemplate;
    private string _fileNameTemplate = AppSettings.DefaultFileNameTemplate;
    private bool _workspaceConfigExists;
    private string _monitorIntervalText = "30";
    private string _maxChecksText = "4";
    private string _maxRecordingsText = "3";
    private string _proxyUrl = string.Empty;
    private string _quality = "best";
    private string _recordingFormat = AppSettings.DefaultRecordingFormat;
    private string _segmentMinutesText = "0";
    private bool _saveThumbnail = true;
    private bool _saveDanmaku;
    private bool _launchAtStartup;
    private bool _startMinimized;
    private bool _recordingEnabled = true;
    private bool _startMonitoringOnLaunch = true;
    private bool _preventSleepWhileMonitoring = true;
    private string _settingsFeedback = string.Empty;
    private int _feedbackTone;
    private int _timerTicks;
    private Func<Task<string?>>? _pickFolderAsync;

    public MainWindowViewModel()
    {
        _monitoring = new MonitoringService(_probeService, _recordingManager);
        _monitoring.RoomUpdated += OnRoomUpdated;
        _monitoring.StateChanged += OnMonitoringStateChanged;
        _recordingManager.RecordingStarted += OnRecordingStarted;
        _recordingManager.RecordingStopped += OnRecordingStopped;
        _recordingManager.Warning += OnRecordingWarning;

        NavigateCommand = new RelayCommand(Navigate);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoringAsync);
        ScanNowCommand = new RelayCommand(ScanNowAsync);
        AddRoomCommand = new RelayCommand(OpenAddRoom);
        CancelAddRoomCommand = new RelayCommand(CloseAddRoom);
        ConfirmAddRoomCommand = new RelayCommand(ConfirmAddRoomAsync);
        RemoveRoomCommand = new RelayCommand(parameter => RemoveRoomAsync(parameter as RoomItemViewModel));
        CheckRoomCommand = new RelayCommand(parameter => CheckRoomAsync(parameter as RoomItemViewModel));
        RoomPrimaryActionCommand = new RelayCommand(parameter => RoomPrimaryActionAsync(parameter as RoomItemViewModel));
        OpenRoomUrlCommand = new RelayCommand(parameter => OpenRoomUrl(parameter as RoomItemViewModel));
        SaveSettingsCommand = new RelayCommand(SaveSettingsAsync);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        PickOutputFolderCommand = new RelayCommand(PickOutputFolderAsync);
        ReloadWorkspaceCommand = new RelayCommand(ReloadWorkspaceAsync);
        OpenRecordingFolderCommand = new RelayCommand(parameter => OpenRecordingFolder(parameter as RecordingItemViewModel));
        ConvertRecordingCommand = new RelayCommand(parameter => ConvertRecordingAsync(parameter as RecordingItemViewModel));
        OpenConvertedRecordingCommand = new RelayCommand(parameter => OpenConvertedRecording(parameter as RecordingItemViewModel));
        ClearActivitiesCommand = new RelayCommand(ClearActivities);

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnTimerTick);
        ClockText = DateTime.Now.ToString("HH:mm");
    }

    public ObservableCollection<RoomItemViewModel> Rooms { get; } = [];

    public ObservableCollection<ActivityItemViewModel> Activities { get; } = [];

    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = [];

    public RelayCommand NavigateCommand { get; }

    public RelayCommand ToggleMonitoringCommand { get; }

    public RelayCommand ScanNowCommand { get; }

    public RelayCommand AddRoomCommand { get; }

    public RelayCommand CancelAddRoomCommand { get; }

    public RelayCommand ConfirmAddRoomCommand { get; }

    public RelayCommand RemoveRoomCommand { get; }

    public RelayCommand CheckRoomCommand { get; }

    public RelayCommand RoomPrimaryActionCommand { get; }

    public RelayCommand OpenRoomUrlCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand OpenOutputFolderCommand { get; }

    public RelayCommand PickOutputFolderCommand { get; }

    public RelayCommand ReloadWorkspaceCommand { get; }

    public RelayCommand OpenRecordingFolderCommand { get; }

    public RelayCommand ConvertRecordingCommand { get; }

    public RelayCommand OpenConvertedRecordingCommand { get; }

    public RelayCommand ClearActivitiesCommand { get; }

    public AppSettings Settings { get; private set; } = new();

    public string ClockText { get; private set; }

    public string TodayText =>
        DateTime.Now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));

    public bool IsOverviewPage => _activePage == Page.Overview;

    public bool IsRoomsPage => _activePage == Page.Rooms;

    public bool IsRecordingsPage => _activePage == Page.Recordings;

    public bool IsSettingsPage => _activePage == Page.Settings;

    public bool ShowsRoomActions => _activePage is Page.Overview or Page.Rooms;

    public bool UsesCompactRecordingLayout => _windowWidth < CompactRecordingWidth;

    public void UpdateWindowWidth(double width)
    {
        if (Math.Abs(_windowWidth - width) < 0.5)
        {
            return;
        }

        _windowWidth = width;
        OnPropertyChanged(nameof(UsesCompactRecordingLayout));
    }

    public string ActivePageKey
    {
        get => _activePage switch
        {
            Page.Rooms => "rooms",
            Page.Recordings => "recordings",
            Page.Settings => "settings",
            _ => "overview"
        };
        set => Navigate(value);
    }

    public string PageEyebrow => _activePage switch
    {
        Page.Overview => "CONTROL / OVERVIEW",
        Page.Rooms => "ROOMS / MATRIX",
        Page.Recordings => "ARCHIVE / OUTPUT",
        Page.Settings => "SYSTEM / PREFERENCES",
        _ => string.Empty
    };

    public string PageTitle => _activePage switch
    {
        Page.Overview => "巡检总览",
        Page.Rooms => "直播间矩阵",
        Page.Recordings => "录制档案",
        Page.Settings => "系统设置",
        _ => string.Empty
    };

    public string PageDescription => _activePage switch
    {
        Page.Overview => "统一查看直播状态、自动录制与巡检节奏。",
        Page.Rooms => "管理各平台直播间，以及每个房间的监控和录制策略。",
        Page.Recordings => "已完成的录制文件、时长与占用空间。",
        Page.Settings => "调整巡检节奏、录制输出与网络连接。",
        _ => string.Empty
    };

    public int RoomCount => Rooms.Count;

    public int LiveRoomCount => Rooms.Count(room => room.IsLive && !room.HasStaleState);

    public int RecordingRoomCount => Rooms.Count(room => room.IsRecording);

    public int MonitoredRoomCount => Rooms.Count(room => room.IsMonitored);

    public int PausedRoomCount => Rooms.Count(room => !room.IsMonitored);

    public string RoomCountText => Rooms.Count.ToString("00");

    public string LiveRoomCountText => LiveRoomCount.ToString("00");

    public string RecordingRoomCountText => RecordingRoomCount.ToString("00");

    public string MonitoredRoomText => $"{MonitoredRoomCount}/{Rooms.Count}";

    public string RecordingHistoryCountText => Recordings.Count.ToString("00");

    public int RecordingCount => Recordings.Count;

    public double RecordedSizeGb =>
        Recordings.Sum(item => item.Model.SizeBytes) / 1024d / 1024 / 1024;

    public string TotalRecordedSizeText
    {
        get
        {
            var bytes = Recordings.Sum(item => item.Model.SizeBytes);
            return bytes switch
            {
                >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F1} GB",
                >= 1024L * 1024 => $"{bytes / 1024d / 1024:F1} MB",
                >= 1024 => $"{bytes / 1024d:F0} KB",
                _ => "0 MB"
            };
        }
    }

    public string SessionDurationText
    {
        get
        {
            var elapsed = DateTimeOffset.Now - _sessionStartedAt;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    public bool HasRooms => Rooms.Count > 0;

    public bool HasNoRooms => !HasRooms;

    public bool HasRecordings => Recordings.Count > 0;

    public bool HasNoRecordings => !HasRecordings;

    public bool HasActivities => Activities.Count > 0;

    public bool IsMonitoring => _isMonitoring;

    public bool IsScanning => _isScanning;

    public string MonitoringStatusText => _isMonitoring
        ? _isScanning ? "正在巡检" : "巡检运行中"
        : "巡检已暂停";

    public string GlobalStatusText => $"{MonitoringStatusText}  ·  {NextScanText}";

    public string MonitoringButtonText => _isMonitoring ? "暂停巡检" : "启动巡检";

    public ButtonVariant MonitoringButtonVariant =>
        _isMonitoring ? ButtonVariant.Secondary : ButtonVariant.Primary;

    public string ScanButtonText => _isScanning ? "巡检中" : "立即巡检";

    public IBrush MonitoringBrush => _isMonitoring ? Brush("#1C8B63") : Brush("#8A857A");

    public IBrush MonitoringSoftBrush => _isMonitoring ? SoftBrush("#1C8B63") : SoftBrush("#8A857A");

    public string NextScanText
    {
        get
        {
            if (!_isMonitoring)
            {
                return "等待启动";
            }

            if (_isScanning)
            {
                return "正在执行";
            }

            if (_nextScanAt is null)
            {
                return "即将开始";
            }

            var remaining = _nextScanAt.Value - DateTimeOffset.Now;
            return remaining <= TimeSpan.Zero
                ? "即将开始"
                : $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}";
        }
    }

    public string LastScanText
    {
        get
        {
            if (_lastScanCompletedAt is null)
            {
                return "尚未执行";
            }

            var elapsed = DateTimeOffset.Now - _lastScanCompletedAt.Value;
            return elapsed < TimeSpan.FromSeconds(20)
                ? "刚刚完成"
                : elapsed < TimeSpan.FromMinutes(60)
                    ? $"{(int)elapsed.TotalMinutes} 分钟前"
                    : _lastScanCompletedAt.Value.ToLocalTime().ToString("HH:mm");
        }
    }

    public bool IsAddRoomOpen
    {
        get => _isAddRoomOpen;
        set
        {
            if (SetProperty(ref _isAddRoomOpen, value) && !value)
            {
                AddRoomError = string.Empty;
            }
        }
    }

    public string NewRoomUrl
    {
        get => _newRoomUrl;
        set
        {
            if (SetProperty(ref _newRoomUrl, value))
            {
                AddRoomError = string.Empty;
                UpdatePlatformPreview();
            }
        }
    }

    public string NewRoomName
    {
        get => _newRoomName;
        set => SetProperty(ref _newRoomName, value);
    }

    public bool NewRoomAutoRecord
    {
        get => _newRoomAutoRecord;
        set => SetProperty(ref _newRoomAutoRecord, value);
    }

    public string NewRoomPlatformName
    {
        get => _newRoomPlatformName;
        private set => SetProperty(ref _newRoomPlatformName, value);
    }

    public string NewRoomPlatformHint
    {
        get => _newRoomPlatformHint;
        private set => SetProperty(ref _newRoomPlatformHint, value);
    }

    public IBrush NewRoomPlatformBrush { get; private set; } = Brush("#F06435");

    public IBrush NewRoomPlatformSoftBrush { get; private set; } = SoftBrush("#F06435");

    public string AddRoomError
    {
        get => _addRoomError;
        private set
        {
            if (SetProperty(ref _addRoomError, value))
            {
                OnPropertyChanged(nameof(HasAddRoomError));
            }
        }
    }

    public bool HasAddRoomError => !string.IsNullOrWhiteSpace(AddRoomError);

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public string FolderTemplate
    {
        get => _folderTemplate;
        set
        {
            if (SetProperty(ref _folderTemplate, value))
            {
                RefreshTemplatePreview();
            }
        }
    }

    public string FileNameTemplate
    {
        get => _fileNameTemplate;
        set
        {
            if (SetProperty(ref _fileNameTemplate, value))
            {
                RefreshTemplatePreview();
            }
        }
    }

    public string WorkspaceConfigPath
    {
        get => _workspaceConfigPath;
        private set
        {
            if (SetProperty(ref _workspaceConfigPath, value))
            {
                _workspaceConfigExists = !string.IsNullOrWhiteSpace(value) && File.Exists(value);
                OnPropertyChanged(nameof(WorkspaceModeText));
            }
        }
    }

    public string TemplatePreviewFolder => string.IsNullOrWhiteSpace(FolderTemplate)
        ? AppSettings.DefaultFolderTemplate
        : FolderTemplate;

    public string TemplatePreviewFileName =>
        $"{FileNameTemplate}.{_recordingFormat}";

    public string WorkspaceModeText => _workspaceConfigExists
        ? "可移动配置已启用"
        : "准备创建工作区";

    public string MonitorIntervalText
    {
        get => _monitorIntervalText;
        set => SetProperty(ref _monitorIntervalText, value);
    }

    public string MaxChecksText
    {
        get => _maxChecksText;
        set => SetProperty(ref _maxChecksText, value);
    }

    public string MaxRecordingsText
    {
        get => _maxRecordingsText;
        set => SetProperty(ref _maxRecordingsText, value);
    }

    public string ProxyUrl
    {
        get => _proxyUrl;
        set => SetProperty(ref _proxyUrl, value);
    }

    public string Quality
    {
        get => _quality;
        set => SetProperty(ref _quality, value);
    }

    public bool RecordingEnabled
    {
        get => _recordingEnabled;
        set => SetProperty(ref _recordingEnabled, value);
    }

    public bool StartMonitoringOnLaunch
    {
        get => _startMonitoringOnLaunch;
        set => SetProperty(ref _startMonitoringOnLaunch, value);
    }

    public bool PreventSleepWhileMonitoring
    {
        get => _preventSleepWhileMonitoring;
        set => SetProperty(ref _preventSleepWhileMonitoring, value);
    }

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string StartupSettingLabel => IsMacOS ? "登录时自动启动" : "Windows 开机自启";

    public IReadOnlyList<string> RecordingFormats { get; } = ["FLV", "MP4", "MKV", "TS"];

    public string SelectedRecordingFormat
    {
        get => _recordingFormat.ToUpperInvariant();
        set
        {
            var next = AppSettings.NormalizeRecordingFormat(value);
            if (string.Equals(_recordingFormat, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _recordingFormat = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingFormatText));
            OnPropertyChanged(nameof(TemplatePreviewFileName));
            OnPropertyChanged(nameof(RecordingStrategyHint));
        }
    }

    public string RecordingFormatText => _recordingFormat.ToUpperInvariant();

    public string RecordingStrategyHint => _recordingFormat switch
    {
        "flv" => "默认格式。录制时原流复制，后台 CPU 占用最低；完成后可在“录制档案”手动转为 MP4。",
        "mp4" => "录制时会把 FLV 直播源转为 H.264 + AAC，CPU 占用较高；仅在需要直接得到 MP4 时选择。",
        _ => "MKV / TS 直接复制原流，不转码；完成后可在“录制档案”手动转为 MP4。"
    };

    public string SegmentMinutesText
    {
        get => _segmentMinutesText;
        set
        {
            if (SetProperty(ref _segmentMinutesText, value))
            {
                OnPropertyChanged(nameof(SegmentSummaryText));
            }
        }
    }

    public string SegmentSummaryText
    {
        get
        {
            if (int.TryParse(_segmentMinutesText, out var minutes) && minutes > 0)
            {
                return $"每 {minutes} 分钟另起一个文件，单文件更便于传输和归档。";
            }

            return "不自动分段，整场直播保存为单个文件。";
        }
    }

    public bool SaveThumbnail
    {
        get => _saveThumbnail;
        set => SetProperty(ref _saveThumbnail, value);
    }

    public bool SaveDanmaku
    {
        get => _saveDanmaku;
        set => SetProperty(ref _saveDanmaku, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public string SettingsFeedback
    {
        get => _settingsFeedback;
        private set
        {
            if (SetProperty(ref _settingsFeedback, value))
            {
                OnPropertyChanged(nameof(HasSettingsFeedback));
            }
        }
    }

    public bool HasSettingsFeedback => !string.IsNullOrWhiteSpace(SettingsFeedback);

    public IBrush SettingsFeedbackBrush => _feedbackTone == 1 ? Brush("#1C8B63") : Brush("#D94A3A");

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var workspace = await _workspaceStore.LoadStartupAsync();
        _state = workspace.State;
        WorkspaceConfigPath = workspace.ConfigPath;
        Settings = _state.Settings;
        LoadSettingsFields();

        foreach (var room in _state.Rooms.OrderByDescending(room => room.CreatedAt))
        {
            // A new process cannot know whether a persisted live flag is still valid.
            // The first scheduled probe restores it and can start recording from fresh data.
            room.IsLive = false;
            AddRoomViewModel(room);
        }

        foreach (var recording in _state.Recordings.OrderByDescending(item => item.EndedAt))
        {
            Recordings.Add(new RecordingItemViewModel(recording));
        }

        if (!string.IsNullOrWhiteSpace(_workspaceStore.LastLoadError))
        {
            AddActivity(_workspaceStore.LastLoadError, ActivityTone.Error);
        }

        if (!string.IsNullOrWhiteSpace(workspace.Notice))
        {
            AddActivity(workspace.Notice, ActivityTone.Info);
        }

        AddActivity(
            Rooms.Count == 0
                ? "LiveHelper 已启动，添加直播间即可开始巡检。"
                : $"已载入 {Rooms.Count} 个直播间与 {Recordings.Count} 条录制记录。",
            ActivityTone.Info);

        _monitoring.SetRooms(Rooms.Select(item => item.Model));
        RefreshOverview();
        if (_uiActive)
        {
            _timer.Start();
        }

        if (Settings.StartMonitoringOnLaunch)
        {
            await StartMonitoringAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _uiActive = false;
        _timer.Stop();
        _conversionCts.Cancel();
        await _monitoring.DisposeAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await SaveStateAsync(logSuccess: false);
    }

    public async Task RefreshOnActivationAsync()
    {
        if (!_isMonitoring || _isScanning)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastActivationScanAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastActivationScanAt = now;
        await ScanNowAsync();
    }

    public void SetUiActive(bool active)
    {
        if (_uiActive == active)
        {
            return;
        }

        _uiActive = active;
        if (!active)
        {
            _timer.Stop();
            return;
        }

        if (_shuttingDown)
        {
            return;
        }

        _timer.Start();
        OnTimerTick(null, EventArgs.Empty);
    }

    private void Navigate(object? parameter)
    {
        _activePage = parameter?.ToString()?.ToLowerInvariant() switch
        {
            "rooms" => Page.Rooms,
            "recordings" => Page.Recordings,
            "settings" => Page.Settings,
            _ => Page.Overview
        };

        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsRoomsPage));
        OnPropertyChanged(nameof(IsRecordingsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowsRoomActions));
        OnPropertyChanged(nameof(ActivePageKey));
        OnPropertyChanged(nameof(PageEyebrow));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
    }

    private async Task ToggleMonitoringAsync()
    {
        if (_isMonitoring)
        {
            await _monitoring.StopAsync();
            AddActivity("自动巡检已暂停。", ActivityTone.Warning);
            return;
        }

        await StartMonitoringAsync();
    }

    private async Task StartMonitoringAsync()
    {
        _monitoring.SetRooms(Rooms.Select(item => item.Model));
        await _monitoring.StartAsync(Settings.Clone());
        _nextScanAt = _monitoring.GetNextCheckAt()
                      ?? DateTimeOffset.Now + TimeSpan.FromSeconds(Settings.MonitorIntervalSeconds);
        _lastActivationScanAt = DateTimeOffset.UtcNow;
        AddActivity("自动巡检已启动。", ActivityTone.Success);
        NotifyMonitoringProperties();
    }

    private async Task ScanNowAsync()
    {
        _monitoring.SetRooms(Rooms.Select(item => item.Model));
        var started = await _monitoring.ScanNowAsync(Settings.Clone());
        if (!started)
        {
            AddActivity("已有巡检正在执行，请稍后再试。", ActivityTone.Warning);
            return;
        }

        _lastActivationScanAt = DateTimeOffset.UtcNow;
    }

    private void OpenAddRoom()
    {
        AddRoomError = string.Empty;
        IsAddRoomOpen = true;
    }

    private void CloseAddRoom()
    {
        IsAddRoomOpen = false;
    }

    private async Task ConfirmAddRoomAsync()
    {
        try
        {
            var detected = PlatformDetector.Detect(NewRoomUrl);
            if (Rooms.Any(room =>
                    string.Equals(room.Url, detected.NormalizedUrl, StringComparison.OrdinalIgnoreCase)))
            {
                AddRoomError = "这个直播间已经在列表中。";
                return;
            }

            var room = new MonitoredRoom
            {
                Platform = detected.Platform,
                Url = detected.NormalizedUrl,
                DisplayName = NewRoomName.Trim(),
                PlatformRoomId = detected.ExternalId ?? string.Empty,
                IsMonitored = true,
                AutoRecord = NewRoomAutoRecord
            };

            AddRoomViewModel(room);
            _monitoring.SetRooms(Rooms.Select(item => item.Model));
            RefreshOverview();
            AddActivity(
                $"已添加 {PlatformCatalog.Get(room.Platform).DisplayName} 直播间：{room.EffectiveName}。",
                ActivityTone.Success);
            await SaveStateAsync();

            NewRoomUrl = string.Empty;
            NewRoomName = string.Empty;
            NewRoomAutoRecord = true;
            CloseAddRoom();

            if (_isMonitoring)
            {
                var item = Rooms.FirstOrDefault(candidate => candidate.Id == room.Id);
                if (item is not null)
                {
                    _ = CheckRoomAsync(item);
                }
            }
        }
        catch (Exception ex)
        {
            AddRoomError = $"添加直播间失败：{ex.Message}";
        }
    }

    private async Task RemoveRoomAsync(RoomItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (item.IsRecording)
            {
                await _recordingManager.StopAsync(item.Id);
            }

            Rooms.Remove(item);
            _lastLiveStates.Remove(item.Id);
            _lastErrors.Remove(item.Id);
            _lastWarnings.Remove(item.Id);
            _monitoring.SetRooms(Rooms.Select(room => room.Model));
            RefreshOverview();
            AddActivity($"已移除直播间：{item.DisplayName}。", ActivityTone.Info);
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            AddActivity($"移除直播间失败：{ex.Message}", ActivityTone.Error);
        }
    }

    private async Task CheckRoomAsync(RoomItemViewModel? item)
    {
        if (item is null || item.IsChecking)
        {
            return;
        }

        item.SetChecking(true);
        try
        {
            var started = await _monitoring.CheckRoomNowAsync(item.Model, Settings.Clone());
            if (!started)
            {
                AddActivity("当前巡检尚未结束，无法单独检查该直播间。", ActivityTone.Warning);
            }
        }
        catch (Exception ex)
        {
            AddActivity($"检查 {item.DisplayName} 失败：{ex.Message}", ActivityTone.Error);
        }
        finally
        {
            item.SetChecking(false);
        }
    }

    private async Task RoomPrimaryActionAsync(RoomItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRecording)
        {
            try
            {
                await _recordingManager.StopAsync(item.Id);
            }
            catch (Exception ex)
            {
                AddActivity($"停止录制失败：{ex.Message}", ActivityTone.Error);
            }

            return;
        }

        if (item.HasStaleState)
        {
            await CheckRoomAsync(item);
            return;
        }

        if (!item.IsLive)
        {
            await CheckRoomAsync(item);
            return;
        }

        if (item.IsChecking)
        {
            return;
        }

        item.SetChecking(true);
        try
        {
            var probe = await _probeService.ProbeAsync(item.Model, Settings.Clone(), CancellationToken.None);
            if (!probe.CanRecord)
            {
                AddActivity(
                    string.IsNullOrWhiteSpace(probe.Warning)
                        ? "未能解析出可录制的媒体流。"
                        : probe.Warning,
                    ActivityTone.Warning);
                return;
            }

            var result = await _recordingManager.StartAsync(
                item.Model,
                probe,
                Settings.Clone(),
                CancellationToken.None);
            if (!result.Started)
            {
                AddActivity($"无法开始录制：{result.Error}", ActivityTone.Error);
            }
        }
        catch (Exception ex)
        {
            AddActivity($"启动录制失败：{ex.Message}", ActivityTone.Error);
        }
        finally
        {
            item.SetChecking(false);
        }
    }

    private static void OpenRoomUrl(RoomItemViewModel? item)
    {
        if (item is not null)
        {
            OpenExternal(item.Url);
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(MonitorIntervalText, out var interval)
            || !int.TryParse(MaxChecksText, out var maxChecks)
            || !int.TryParse(MaxRecordingsText, out var maxRecordings)
            || !int.TryParse(SegmentMinutesText, out var segmentMinutes))
        {
            ShowSettingsFeedback("巡检间隔、并发数和分段时长必须是整数。", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            ShowSettingsFeedback("请填写录制输出目录。", isError: true);
            return;
        }

        var proxyUrl = AppSettings.NormalizeProxyUrl(ProxyUrl);
        if (proxyUrl.Length > 0
            && (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri)
                || string.IsNullOrWhiteSpace(proxyUri.Host)))
        {
            ShowSettingsFeedback(
                "代理 URL 无效，请填写类似 http://127.0.0.1:7890 的地址；不使用代理请留空。",
                isError: true);
            return;
        }

        var previousDirectory = Settings.OutputDirectory;
        var targetDirectory = OutputDirectory.Trim();
        var wasMonitoring = _isMonitoring;
        if (_isMonitoring)
        {
            await _monitoring.StopAsync();
        }

        var next = new AppSettings
        {
            OutputDirectory = targetDirectory,
            FolderTemplate = string.IsNullOrWhiteSpace(FolderTemplate)
                ? AppSettings.DefaultFolderTemplate
                : FolderTemplate,
            FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate)
                ? AppSettings.DefaultFileNameTemplate
                : FileNameTemplate,
            MonitorIntervalSeconds = interval,
            MaxConcurrentChecks = maxChecks,
            MaxConcurrentRecordings = maxRecordings,
            StartMonitoringOnLaunch = StartMonitoringOnLaunch,
            PreventSleepWhileMonitoring = PreventSleepWhileMonitoring,
            RecordingEnabled = RecordingEnabled,
            ProxyUrl = proxyUrl,
            RecordingFormat = _recordingFormat,
            Quality = Quality,
            SegmentMinutes = segmentMinutes,
            SaveThumbnail = SaveThumbnail,
            SaveDanmaku = SaveDanmaku,
            LaunchAtStartup = LaunchAtStartup,
            StartMinimized = StartMinimized
        };
        next.Normalize();

        try
        {
            var fallback = new PersistedState
            {
                Settings = next,
                Rooms = Rooms.Select(item => item.Model).ToList(),
                Recordings = Recordings.Select(item => item.Model).ToList()
            };
            var workspace = await _workspaceStore.OpenOrCreateAsync(targetDirectory, fallback);
            var directoryChanged = !string.Equals(
                Path.GetFullPath(previousDirectory),
                workspace.Directory,
                StringComparison.OrdinalIgnoreCase);
            if (directoryChanged)
            {
                ReplaceWorkspace(workspace.State);
            }
            else
            {
                _state.Settings = next;
                Settings = next;
            }

            Settings = _state.Settings;
            _state.Settings = Settings;
            WorkspaceConfigPath = workspace.ConfigPath;
            LoadSettingsFields();
            _monitoring.SetRooms(Rooms.Select(item => item.Model));
            await SaveStateAsync(logSuccess: false);

            var startup = StartupRegistration.Apply(Settings.LaunchAtStartup);
            if (!startup.Succeeded)
            {
                var actualStartup = StartupRegistration.IsEnabled();
                if (Settings.LaunchAtStartup != actualStartup)
                {
                    Settings.LaunchAtStartup = actualStartup;
                    LaunchAtStartup = actualStartup;
                    await SaveStateAsync(logSuccess: false);
                }

                ShowSettingsFeedback(startup.Message, isError: true);
                AddActivity(startup.Message, ActivityTone.Error);
            }

            if (wasMonitoring)
            {
                await StartMonitoringAsync();
            }

            if (startup.Succeeded)
            {
                ShowSettingsFeedback(
                    directoryChanged && workspace.LoadedExistingConfig
                        ? "已切换到所选工作区并载入其配置。"
                        : "设置已保存，录制策略与开机自启已应用。",
                    isError: false);
            }
            AddActivity(
                directoryChanged ? workspace.Notice : "系统设置已保存。",
                ActivityTone.Success);
        }
        catch (Exception ex)
        {
            if (wasMonitoring && !_isMonitoring)
            {
                await StartMonitoringAsync();
            }

            ShowSettingsFeedback($"保存设置失败：{ex.Message}", isError: true);
            AddActivity($"保存设置失败：{ex.Message}", ActivityTone.Error);
        }
    }

    public void SetFolderPicker(Func<Task<string?>> pickFolderAsync) =>
        _pickFolderAsync = pickFolderAsync;

    private async Task PickOutputFolderAsync()
    {
        if (_pickFolderAsync is null)
        {
            ShowSettingsFeedback("当前窗口不支持目录选择，请直接填写路径。", isError: true);
            return;
        }

        var selected = await _pickFolderAsync();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        OutputDirectory = selected;
        var configPath = WorkspaceStateStore.GetConfigPath(selected);
        if (File.Exists(configPath))
        {
            var workspace = await _workspaceStore.OpenOrCreateAsync(
                selected,
                new PersistedState { Settings = Settings.Clone() });
            ReplaceWorkspace(workspace.State);
            Settings = _state.Settings;
            WorkspaceConfigPath = workspace.ConfigPath;
            LoadSettingsFields();
            RefreshOverview();
            ShowSettingsFeedback(
                $"已自动载入 {Path.GetFileName(selected)} 中的工作区配置。",
                isError: false);
        }
        else
        {
            WorkspaceConfigPath = configPath;
            OnPropertyChanged(nameof(WorkspaceModeText));
            ShowSettingsFeedback("该目录尚未初始化，保存设置后会创建可移动配置。", isError: false);
        }
    }

    private async Task ReloadWorkspaceAsync()
    {
        try
        {
            var workspace = await _workspaceStore.OpenOrCreateAsync(
                OutputDirectory,
                new PersistedState { Settings = Settings.Clone() });
            ReplaceWorkspace(workspace.State);
            Settings = _state.Settings;
            WorkspaceConfigPath = workspace.ConfigPath;
            LoadSettingsFields();
            RefreshOverview();
            ShowSettingsFeedback(workspace.Notice, isError: false);
        }
        catch (Exception ex)
        {
            ShowSettingsFeedback($"载入工作区失败：{ex.Message}", isError: true);
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.OutputDirectory);
            OpenExternal(Settings.OutputDirectory);
        }
        catch (Exception ex)
        {
            AddActivity($"无法打开输出目录：{ex.Message}", ActivityTone.Error);
        }
    }

    private void OpenRecordingFolder(RecordingItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(item.FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                OpenExternal(directory);
            }
        }
        catch (Exception ex)
        {
            AddActivity($"无法打开录制目录：{ex.Message}", ActivityTone.Error);
        }
    }

    private async Task ConvertRecordingAsync(RecordingItemViewModel? item)
    {
        if (item is null || !item.CanConvertToMp4)
        {
            return;
        }

        item.BeginConversion();
        AddActivity(
            $"正在转换 MP4（原流复制）：{Path.GetFileName(item.FilePath)}",
            ActivityTone.Info);

        try
        {
            var result = await _recordingConverter
                .ConvertToMp4Async(item.FilePath, Settings.Clone(), _conversionCts.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                item.FailConversion(result.Error);
                AddActivity($"MP4 转换失败：{result.Error}", ActivityTone.Error);
                return;
            }

            item.CompleteConversion(result.OutputPath);
            await SaveStateAsync(logSuccess: false).ConfigureAwait(true);
            AddActivity(
                $"MP4 已生成：{Path.GetFileName(result.OutputPath)}",
                ActivityTone.Success);
        }
        catch (Exception ex)
        {
            item.FailConversion($"MP4 转换失败：{ex.Message}");
            AddActivity($"MP4 转换失败：{ex.Message}", ActivityTone.Error);
        }
    }

    private void OpenConvertedRecording(RecordingItemViewModel? item)
    {
        var path = item?.ConvertedMp4Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AddActivity("转换后的 MP4 文件不存在。", ActivityTone.Warning);
            return;
        }

        try
        {
            OpenExternal(path);
        }
        catch (Exception ex)
        {
            AddActivity($"无法打开 MP4：{ex.Message}", ActivityTone.Error);
        }
    }

    private void ClearActivities()
    {
        Activities.Clear();
        OnPropertyChanged(nameof(HasActivities));
    }

    private void OnRoomUpdated(object? sender, RoomProbeUpdatedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Rooms.FirstOrDefault(room => room.Id == e.Room.Id);
            if (item is null)
            {
                return;
            }

            _lastLiveStates.TryGetValue(item.Id, out var wasLive);
            _lastErrors.TryGetValue(item.Id, out var previousError);
            _lastWarnings.TryGetValue(item.Id, out var previousWarning);
            previousError ??= string.Empty;
            previousWarning ??= string.Empty;

            item.RefreshAll();
            _lastLiveStates[item.Id] = item.IsLive;
            _lastErrors[item.Id] = item.Model.LastError;
            _lastWarnings[item.Id] = item.Model.LastWarning;

            var changed = false;
            if (!wasLive && item.IsLive)
            {
                AddActivity($"{item.DisplayName} 已开播：{item.LiveTitle}", ActivityTone.Success);
                changed = true;
            }
            else if (wasLive && !item.IsLive)
            {
                AddActivity($"{item.DisplayName} 已下播。", ActivityTone.Info);
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(item.Model.LastError)
                && !string.Equals(previousError, item.Model.LastError, StringComparison.Ordinal))
            {
                AddActivity($"{item.DisplayName}：{item.Model.LastError}", ActivityTone.Error);
                changed = true;
            }
            else if (!string.IsNullOrWhiteSpace(item.Model.LastWarning)
                     && !string.Equals(previousWarning, item.Model.LastWarning, StringComparison.Ordinal))
            {
                AddActivity($"{item.DisplayName}：{item.Model.LastWarning}", ActivityTone.Warning);
            }

            RefreshOverview();
            if (changed)
            {
                _ = SaveStateAsync(logSuccess: false);
            }
        });
    }

    private void OnMonitoringStateChanged(object? sender, MonitorStateChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wasScanning = _isScanning;
            _isMonitoring = e.IsRunning;
            _isScanning = e.IsScanning;

            if (!wasScanning && e.IsScanning)
            {
                _nextScanAt = null;
            }
            else if (wasScanning && !e.IsScanning)
            {
                _lastScanCompletedAt = DateTimeOffset.Now;
                _nextScanAt = _monitoring.GetNextCheckAt()
                              ?? DateTimeOffset.Now + TimeSpan.FromSeconds(Settings.MonitorIntervalSeconds);
                RefreshRoomRelativeTimes();
            }

            NotifyMonitoringProperties();
        });
    }

    private void OnRecordingStarted(object? sender, RecordingStartedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Rooms.FirstOrDefault(room => room.Id == e.RoomId);
            item?.SetRecording(true, e.StartedAt);
            RefreshOverview();
            AddActivity($"开始录制：{e.RoomName}", ActivityTone.Recording);
            _ = SaveStateAsync(logSuccess: false);
        });
    }

    private void OnRecordingWarning(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() => AddActivity(message, ActivityTone.Warning));
    }

    private void OnRecordingStopped(object? sender, RecordingStoppedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = Rooms.FirstOrDefault(room => room.Id == e.RoomId);
            item?.SetRecording(false);

            long size = 0;
            try
            {
                if (File.Exists(e.FilePath))
                {
                    size = new FileInfo(e.FilePath).Length;
                }
            }
            catch
            {
                // The history entry remains useful even when the file metadata cannot be read.
            }

            var error = string.IsNullOrWhiteSpace(e.Error) ? string.Empty : e.Error.Trim();
            var history = new RecordingHistoryItem(
                Guid.NewGuid(),
                e.RoomId,
                e.RoomName,
                e.Platform,
                string.IsNullOrWhiteSpace(e.Title)
                    ? item?.LiveTitle ?? e.RoomName
                    : e.Title,
                e.FilePath,
                e.StartedAt,
                e.EndedAt,
                size,
                error);

            Recordings.Insert(0, new RecordingItemViewModel(history));
            RefreshOverview();
            if (error.Length > 0)
            {
                AddActivity($"录制结束，但存在异常：{error}", ActivityTone.Error);
            }
            else
            {
                AddActivity($"录制完成：{Path.GetFileName(e.FilePath)}", ActivityTone.Success);
            }
            _ = SaveStateAsync(logSuccess: false);
        });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_uiActive)
        {
            return;
        }

        ClockText = DateTime.Now.ToString("HH:mm");
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(TodayText));
        OnPropertyChanged(nameof(NextScanText));
        OnPropertyChanged(nameof(LastScanText));
        OnPropertyChanged(nameof(SessionDurationText));

        if (_isMonitoring && !_isScanning)
        {
            _nextScanAt = _monitoring.GetNextCheckAt();
        }

        foreach (var item in Rooms.Where(room => room.IsRecording))
        {
            item.RefreshRecordingDuration();
        }

        _timerTicks++;
        if (_timerTicks % 5 == 0)
        {
            RefreshRoomRelativeTimes();
        }

        if (_feedbackUntil is not null && DateTimeOffset.Now >= _feedbackUntil)
        {
            _feedbackUntil = null;
            SettingsFeedback = string.Empty;
        }
    }

    private void AddRoomViewModel(MonitoredRoom room)
    {
        var item = new RoomItemViewModel(room, OnRoomSettingsChanged);
        Rooms.Add(item);
        _lastLiveStates[room.Id] = room.IsLive;
        _lastErrors[room.Id] = room.LastError;
        _lastWarnings[room.Id] = room.LastWarning;
    }

    private void OnRoomSettingsChanged(RoomItemViewModel room)
    {
        _monitoring.SetRooms(Rooms.Select(item => item.Model));
        RefreshOverview();
        _ = SaveStateAsync(logSuccess: false);
    }

    private void UpdatePlatformPreview()
    {
        if (string.IsNullOrWhiteSpace(NewRoomUrl))
        {
            NewRoomPlatformName = "粘贴链接后自动识别";
            NewRoomPlatformHint = "支持 B 站、抖音、虎牙、斗鱼、快手、Twitch、YouTube 等";
            SetPlatformPreviewBrush("#F06435");
            return;
        }

        try
        {
            var detected = PlatformDetector.Detect(NewRoomUrl);
            var descriptor = PlatformCatalog.Get(detected.Platform);
            NewRoomPlatformName = $"{descriptor.DisplayName} · {descriptor.Code}";
            NewRoomPlatformHint = descriptor.Hint;
            SetPlatformPreviewBrush(descriptor.AccentHex);
        }
        catch (Exception)
        {
            NewRoomPlatformName = "等待有效链接";
            NewRoomPlatformHint = "请输入完整的 HTTP(S) 直播间地址";
            SetPlatformPreviewBrush("#8A857A");
        }
    }

    private void SetPlatformPreviewBrush(string hex)
    {
        NewRoomPlatformBrush = AccentBrushFactory.ForDarkSurface(hex);
        NewRoomPlatformSoftBrush = AccentBrushFactory.SoftForDarkSurface(hex);
        OnPropertyChanged(nameof(NewRoomPlatformBrush));
        OnPropertyChanged(nameof(NewRoomPlatformSoftBrush));
    }

    private void LoadSettingsFields()
    {
        Settings.Normalize();
        OutputDirectory = Settings.OutputDirectory;
        FolderTemplate = Settings.FolderTemplate;
        FileNameTemplate = Settings.FileNameTemplate;
        WorkspaceConfigPath = WorkspaceStateStore.GetConfigPath(Settings.OutputDirectory);
        MonitorIntervalText = Settings.MonitorIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        MaxChecksText = Settings.MaxConcurrentChecks.ToString(CultureInfo.InvariantCulture);
        MaxRecordingsText = Settings.MaxConcurrentRecordings.ToString(CultureInfo.InvariantCulture);
        ProxyUrl = Settings.ProxyUrl;
        Quality = Settings.Quality;
        _recordingFormat = Settings.RecordingFormat;
        OnPropertyChanged(nameof(SelectedRecordingFormat));
        OnPropertyChanged(nameof(RecordingFormatText));
        OnPropertyChanged(nameof(RecordingStrategyHint));
        SegmentMinutesText = Settings.SegmentMinutes.ToString(CultureInfo.InvariantCulture);
        SaveThumbnail = Settings.SaveThumbnail;
        SaveDanmaku = Settings.SaveDanmaku;
        LaunchAtStartup = Settings.LaunchAtStartup;
        StartMinimized = Settings.StartMinimized;
        RecordingEnabled = Settings.RecordingEnabled;
        StartMonitoringOnLaunch = Settings.StartMonitoringOnLaunch;
        PreventSleepWhileMonitoring = Settings.PreventSleepWhileMonitoring;
        OnPropertyChanged(nameof(IsMacOS));
        OnPropertyChanged(nameof(StartupSettingLabel));
        OnPropertyChanged(nameof(WorkspaceModeText));
    }

    private void ReplaceWorkspace(PersistedState state)
    {
        _state = state;
        Rooms.Clear();
        Recordings.Clear();
        _lastLiveStates.Clear();
        _lastErrors.Clear();
        _lastWarnings.Clear();
        foreach (var room in state.Rooms.OrderByDescending(room => room.CreatedAt))
        {
            room.IsLive = false;
            AddRoomViewModel(room);
        }

        foreach (var recording in state.Recordings.OrderByDescending(item => item.EndedAt))
        {
            Recordings.Add(new RecordingItemViewModel(recording));
        }

        _monitoring.SetRooms(Rooms.Select(item => item.Model));
    }

    private void RefreshOverview()
    {
        OnPropertyChanged(nameof(RoomCount));
        OnPropertyChanged(nameof(LiveRoomCount));
        OnPropertyChanged(nameof(RecordingRoomCount));
        OnPropertyChanged(nameof(MonitoredRoomCount));
        OnPropertyChanged(nameof(PausedRoomCount));
        OnPropertyChanged(nameof(RoomCountText));
        OnPropertyChanged(nameof(LiveRoomCountText));
        OnPropertyChanged(nameof(RecordingRoomCountText));
        OnPropertyChanged(nameof(MonitoredRoomText));
        OnPropertyChanged(nameof(RecordingHistoryCountText));
        OnPropertyChanged(nameof(RecordingCount));
        OnPropertyChanged(nameof(RecordedSizeGb));
        OnPropertyChanged(nameof(TotalRecordedSizeText));
        OnPropertyChanged(nameof(HasRooms));
        OnPropertyChanged(nameof(HasNoRooms));
        OnPropertyChanged(nameof(HasRecordings));
        OnPropertyChanged(nameof(HasNoRecordings));
        OnPropertyChanged(nameof(HasActivities));
    }

    private void NotifyMonitoringProperties()
    {
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(MonitoringStatusText));
        OnPropertyChanged(nameof(GlobalStatusText));
        OnPropertyChanged(nameof(MonitoringButtonText));
        OnPropertyChanged(nameof(MonitoringButtonVariant));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(MonitoringBrush));
        OnPropertyChanged(nameof(MonitoringSoftBrush));
        OnPropertyChanged(nameof(NextScanText));
        OnPropertyChanged(nameof(LastScanText));
    }

    private void RefreshRoomRelativeTimes()
    {
        foreach (var room in Rooms)
        {
            room.RefreshAll();
        }
    }

    private void AddActivity(string message, ActivityTone tone)
    {
        Activities.Insert(0, new ActivityItemViewModel(message, tone));
        while (Activities.Count > 80)
        {
            Activities.RemoveAt(Activities.Count - 1);
        }

        OnPropertyChanged(nameof(HasActivities));
    }

    private async Task SaveStateAsync(bool logSuccess = true)
    {
        if (_shuttingDown && logSuccess)
        {
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            var snapshot = new PersistedState
            {
                Settings = Settings.Clone(),
                Rooms = Rooms.Select(item => item.Model).ToList(),
                Recordings = Recordings.Select(item => item.Model).ToList()
            };
            await _workspaceStore.SaveAsync(snapshot);
            _state = snapshot;
            WorkspaceConfigPath = WorkspaceStateStore.GetConfigPath(Settings.OutputDirectory);
            if (logSuccess)
            {
                AddActivity("状态已保存。", ActivityTone.Info);
            }
        }
        catch (Exception ex)
        {
            if (!_shuttingDown)
            {
                AddActivity($"保存状态失败：{ex.Message}", ActivityTone.Error);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void ShowSettingsFeedback(string message, bool isError)
    {
        _feedbackTone = isError ? 2 : 1;
        SettingsFeedback = message;
        OnPropertyChanged(nameof(SettingsFeedbackBrush));
        _feedbackUntil = DateTimeOffset.Now + TimeSpan.FromSeconds(5);
    }

    private void RefreshTemplatePreview()
    {
        OnPropertyChanged(nameof(TemplatePreviewFolder));
        OnPropertyChanged(nameof(TemplatePreviewFileName));
    }

    private static void OpenExternal(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static IBrush SoftBrush(string hex)
    {
        var color = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
    }
}
