using LiveHelper.Core.Models;
using TKCode.Arc;

namespace LiveHelper.Desktop.ViewModels;

public sealed class RecordingItemViewModel : ObservableObject
{
    private RecordingHistoryItem _model;
    private bool _isConverting;
    private string _conversionError = string.Empty;

    public RecordingItemViewModel(RecordingHistoryItem model)
    {
        _model = model;
    }

    public RecordingHistoryItem Model => _model;

    public string FileName => Path.GetFileName(_model.FilePath);

    public string FilePath => _model.FilePath;

    public string RoomName => _model.RoomName;

    public string PlatformName => PlatformCatalog.Get(_model.Platform).DisplayName;

    public string StartedText => _model.StartedAt.ToString("MM-dd HH:mm");

    public string DurationText
    {
        get
        {
            var duration = _model.EndedAt - _model.StartedAt;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
        }
    }

    public string SizeText => _model.SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{_model.SizeBytes / 1024d / 1024 / 1024:F1} GB",
        >= 1024L * 1024 => $"{_model.SizeBytes / 1024d / 1024:F1} MB",
        >= 1024 => $"{_model.SizeBytes / 1024d:F0} KB",
        _ => $"{_model.SizeBytes} B"
    };

    public bool HasError => !string.IsNullOrWhiteSpace(_model.Error);

    public bool IsConverting
    {
        get => _isConverting;
        private set
        {
            if (_isConverting == value)
            {
                return;
            }

            _isConverting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConvertToMp4));
            OnPropertyChanged(nameof(ConversionButtonText));
        }
    }

    public bool SupportsMp4Conversion =>
        IsConvertibleSource(Path.GetExtension(_model.FilePath))
        && !HasConvertedMp4;

    public bool CanConvertToMp4 => SupportsMp4Conversion && !IsConverting;

    public bool HasConvertedMp4 =>
        !string.IsNullOrWhiteSpace(_model.Mp4Path) && File.Exists(_model.Mp4Path);

    public string? ConvertedMp4Path => HasConvertedMp4 ? _model.Mp4Path : null;

    public string ConversionButtonText => IsConverting ? "转换中…" : "转 MP4";

    public bool HasConversionError => !string.IsNullOrWhiteSpace(_conversionError);

    public string ConversionError => _conversionError;

    public void BeginConversion()
    {
        _conversionError = string.Empty;
        IsConverting = true;
        NotifyConversionState();
    }

    public void CompleteConversion(string outputPath)
    {
        _model = _model with { Mp4Path = outputPath };
        _conversionError = string.Empty;
        IsConverting = false;
        NotifyConversionState();
    }

    public void FailConversion(string error)
    {
        _conversionError = string.IsNullOrWhiteSpace(error)
            ? "MP4 转换失败。"
            : error.Trim();
        IsConverting = false;
        NotifyConversionState();
    }

    private void NotifyConversionState()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(SupportsMp4Conversion));
        OnPropertyChanged(nameof(CanConvertToMp4));
        OnPropertyChanged(nameof(HasConvertedMp4));
        OnPropertyChanged(nameof(ConvertedMp4Path));
        OnPropertyChanged(nameof(ConversionButtonText));
        OnPropertyChanged(nameof(HasConversionError));
        OnPropertyChanged(nameof(ConversionError));
    }

    private static bool IsConvertibleSource(string extension) =>
        extension.Equals(".flv", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
}
