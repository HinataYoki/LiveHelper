using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LiveHelper.Desktop.ViewModels;
using TKCode.Arc.Icons.Lucide;

namespace LiveHelper.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _trayIcon;
    private bool _initialized;
    private bool _allowClose;
    private bool _startedHidden;

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeIcon();
        _viewModel = new MainWindowViewModel();
        _viewModel.SetFolderPicker(PickFolderAsync);
        DataContext = _viewModel;
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
            UpdateMaximizeIcon();
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

        e.Cancel = true;
        await _viewModel.ShutdownAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _allowClose = true;
        Close();
    }

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
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _startedHidden = false;
    }

    private void ExitFromTray()
    {
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        HideToTray();

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ToggleMaximize();

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null)
        {
            return;
        }

        MaximizeIcon.Kind = WindowState == Avalonia.Controls.WindowState.Maximized
            ? LucideIconKind.Copy
            : LucideIconKind.Maximize2;
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
