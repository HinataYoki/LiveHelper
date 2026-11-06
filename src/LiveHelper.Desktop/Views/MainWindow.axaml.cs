using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiveHelper.Desktop.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace LiveHelper.Desktop.Views;

public sealed partial class MainWindow : ArcWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _trayIcon;
    private bool _initialized;
    private bool _allowClose;
    private bool _startedHidden;
    private bool _isQuitting;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        _viewModel.SetFolderPicker(PickFolderAsync);
        DataContext = _viewModel;
        SizeChanged += OnWindowSizeChanged;
        Opened += OnOpened;
        Closing += OnClosing;
        _trayIcon = CreateTrayIcon();
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (!_startedHidden && WindowState == WindowState.Minimized && IsVisible)
            {
                HideToTray();
            }
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
        if (_viewModel.StartMinimized)
        {
            _startedHidden = true;
            HideToTray();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (e.CloseReason == WindowCloseReason.WindowClosing && !_isQuitting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        e.Cancel = true;
        await _viewModel.ShutdownAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _allowClose = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) =>
        _viewModel.UpdateWindowWidth(e.NewSize.Width);

    private TrayIcon CreateTrayIcon()
    {
        var iconUri = new Uri("avares://LiveHelper/Assets/livehelper.ico");
        using var iconStream = Avalonia.Platform.AssetLoader.Open(iconUri);
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "LiveHelper - 直播监控与录制",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("显示主界面")
                    {
                        Command = new RelayCommand(ShowFromTray)
                    },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("退出 LiveHelper")
                    {
                        Command = new RelayCommand(ExitFromTray)
                    }
                }
            }
        };
        trayIcon.Clicked += (_, _) => ShowFromTray();
        return trayIcon;
    }

    private void HideToTray()
    {
        _viewModel.SetUiActive(false);
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowFromTray()
    {
        if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatableLifetime)
        {
            activatableLifetime.TryLeaveBackground();
        }

        ShowInTaskbar = true;
        Show();
        _viewModel.SetUiActive(true);
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _startedHidden = false;
        _ = _viewModel.RefreshOnActivationAsync();
    }

    public void ShowFromActivation() => ShowFromTray();

    private void ExitFromTray()
    {
        _isQuitting = true;
        Close();
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 LiveHelper 录制工作区",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
