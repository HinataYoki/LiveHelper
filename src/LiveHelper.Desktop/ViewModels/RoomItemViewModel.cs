using Avalonia.Media;
using LiveHelper.Core.Models;
using TKCode.Arc;

namespace LiveHelper.Desktop.ViewModels;

public sealed class RoomItemViewModel : ObservableObject
{
    private readonly Action<RoomItemViewModel> _settingsChanged;
    private bool _isChecking;
    private bool _isRecording;
    private DateTimeOffset? _recordingStartedAt;

    public RoomItemViewModel(MonitoredRoom model, Action<RoomItemViewModel> settingsChanged)
    {
        Model = model;
        _settingsChanged = settingsChanged;
        Descriptor = PlatformCatalog.Get(model.Platform);
    }

    public MonitoredRoom Model { get; }

    public PlatformDescriptor Descriptor { get; }

    public Guid Id => Model.Id;

    public string PlatformName => Descriptor.DisplayName;

    public string PlatformCode => Descriptor.Code;

    public string PlatformHint => Descriptor.Hint;

    public IBrush PlatformBrush => AccentBrushFactory.ForDarkSurface(Descriptor.AccentHex);

    public IBrush PlatformSoftBrush => AccentBrushFactory.SoftForDarkSurface(Descriptor.AccentHex);

    public string DisplayName => Model.EffectiveName;

    public string AnchorName =>
        string.IsNullOrWhiteSpace(Model.AnchorName) ? PlatformName + "直播间" : Model.AnchorName;

    public string Initial
    {
        get
        {
            var value = !string.IsNullOrWhiteSpace(Model.AnchorName)
                ? Model.AnchorName
                : DisplayName;
            return string.IsNullOrWhiteSpace(value) ? "L" : value.Trim()[..1].ToUpperInvariant();
        }
    }

    public string Url => Model.Url;

    public string LiveTitle =>
        string.IsNullOrWhiteSpace(Model.LiveTitle) ? DisplayName : Model.LiveTitle;

    public string RoomSummary
    {
        get
        {
            if (HasStaleState)
            {
                return "最近一次检查失败，状态待确认";
            }

            if (!string.IsNullOrWhiteSpace(Model.LiveTitle))
            {
                return Model.LiveTitle;
            }

            return IsMonitored ? "等待下一次直播巡检" : "监控已暂停";
        }
    }

    public bool IsMonitored
    {
        get => Model.IsMonitored;
        set
        {
            if (Model.IsMonitored == value)
            {
                return;
            }

            Model.IsMonitored = value;
            RefreshAll();
            _settingsChanged(this);
        }
    }

    public bool AutoRecord
    {
        get => Model.AutoRecord;
        set
        {
            if (Model.AutoRecord == value)
            {
                return;
            }

            Model.AutoRecord = value;
            OnPropertyChanged();
            _settingsChanged(this);
        }
    }

    public bool IsLive => Model.IsLive;

    public bool IsRecording => _isRecording;

    public bool IsChecking => _isChecking;

    public bool IsBusy => IsChecking || IsRecording || IsLive;

    public bool HasIssue => !string.IsNullOrWhiteSpace(IssueText);

    public bool HasStaleState =>
        Model.ConsecutiveFailures > 0 && !string.IsNullOrWhiteSpace(Model.LastError);

    public string SignalToolTip => $"{DisplayName} · {StatusText}";

    public string IssueText =>
        !string.IsNullOrWhiteSpace(Model.LastError)
            ? Model.LastError
            : Model.LastWarning;

    public string StatusText
    {
        get
        {
            if (_isRecording)
            {
                return "录制中";
            }

            if (_isChecking)
            {
                return "检查中";
            }

            if (HasStaleState)
            {
                return "状态未知";
            }

            if (Model.IsLive)
            {
                return "直播中";
            }

            if (!Model.IsMonitored)
            {
                return "已暂停";
            }

            if (!string.IsNullOrWhiteSpace(Model.LastError))
            {
                return "检查异常";
            }

            return "等待开播";
        }
    }

    public BadgeTone StatusTone
    {
        get
        {
            if (_isRecording)
            {
                return BadgeTone.Danger;
            }

            if (HasStaleState)
            {
                return BadgeTone.Warning;
            }

            if (Model.IsLive)
            {
                return BadgeTone.Success;
            }

            if (!string.IsNullOrWhiteSpace(Model.LastError))
            {
                return BadgeTone.Danger;
            }

            if (!Model.IsMonitored)
            {
                return BadgeTone.Neutral;
            }

            return BadgeTone.Neutral;
        }
    }

    public IBrush StatusBrush => StatusTone switch
    {
        BadgeTone.Success => Brush("#1C8B63"),
        BadgeTone.Danger => Brush("#D94A3A"),
        BadgeTone.Warning => Brush("#C47A15"),
        BadgeTone.Info => Brush("#F06435"),
        _ => Brush("#7B7B72")
    };

    public IBrush StatusSoftBrush => StatusTone switch
    {
        BadgeTone.Success => SoftBrush("#1C8B63"),
        BadgeTone.Danger => SoftBrush("#D94A3A"),
        BadgeTone.Warning => SoftBrush("#C47A15"),
        BadgeTone.Info => SoftBrush("#F06435"),
        _ => SoftBrush("#7B7B72")
    };

    public string ViewerText => Model.ViewerCount is > 0
        ? $"{FormatCount(Model.ViewerCount.Value)} 人气"
        : "人气 --";

    public string LastCheckedText
    {
        get
        {
            if (Model.LastCheckedAt is null)
            {
                return "尚未检查";
            }

            var elapsed = DateTimeOffset.Now - Model.LastCheckedAt.Value;
            if (elapsed < TimeSpan.FromSeconds(45))
            {
                return "刚刚检查";
            }

            if (elapsed < TimeSpan.FromMinutes(60))
            {
                return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
            }

            if (elapsed < TimeSpan.FromHours(24))
            {
                return $"{(int)elapsed.TotalHours} 小时前";
            }

            return Model.LastCheckedAt.Value.ToLocalTime().ToString("MM-dd HH:mm");
        }
    }

    public string RecordingDurationText
    {
        get
        {
            if (!_isRecording || _recordingStartedAt is null)
            {
                return string.Empty;
            }

            var duration = DateTimeOffset.Now - _recordingStartedAt.Value;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
        }
    }

    public string RecordingDurationDisplayText =>
        _isRecording ? RecordingDurationText : "未录制";

    public string PrimaryActionText => _isRecording
        ? "停止录制"
        : _isChecking
            ? "检查中"
        : HasStaleState
            ? "重新检查"
        : Model.IsLive
            ? "立即录制"
            : "立即检查";

    public string LastRecordingPath => Model.LastRecordingPath;

    public void SetRecording(bool isRecording, DateTimeOffset? startedAt = null)
    {
        _isRecording = isRecording;
        _recordingStartedAt = isRecording ? startedAt ?? DateTimeOffset.Now : null;
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SignalToolTip));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusSoftBrush));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(RecordingDurationText));
        OnPropertyChanged(nameof(RecordingDurationDisplayText));
    }

    public void SetChecking(bool isChecking)
    {
        _isChecking = isChecking;
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SignalToolTip));
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    public void RefreshRecordingDuration()
    {
        if (_isRecording)
        {
            OnPropertyChanged(nameof(RecordingDurationText));
        }
    }

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AnchorName));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(LiveTitle));
        OnPropertyChanged(nameof(RoomSummary));
        OnPropertyChanged(nameof(IsMonitored));
        OnPropertyChanged(nameof(AutoRecord));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(HasStaleState));
        OnPropertyChanged(nameof(SignalToolTip));
        OnPropertyChanged(nameof(IssueText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusSoftBrush));
        OnPropertyChanged(nameof(ViewerText));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(RecordingDurationText));
        OnPropertyChanged(nameof(RecordingDurationDisplayText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(LastRecordingPath));
    }

    private static IBrush Brush(string hex) =>
        new SolidColorBrush(Color.Parse(hex));

    private static IBrush SoftBrush(string hex)
    {
        var color = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
    }

    private static string FormatCount(int value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:F1} 亿",
        >= 10_000 => $"{value / 10_000d:F1} 万",
        >= 1_000 => $"{value / 1_000d:F1} 千",
        _ => value.ToString()
    };
}
