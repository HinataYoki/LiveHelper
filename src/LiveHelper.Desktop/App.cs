using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LiveHelper.Desktop.Views;

namespace LiveHelper.Desktop;

public sealed class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window can be hidden to the tray. Keep the process alive and let
            // MainWindow perform an explicit shutdown when the user quits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatableLifetime)
            {
                activatableLifetime.Activated += (_, args) =>
                {
                    if (args.Kind == ActivationKind.Reopen)
                    {
                        Dispatcher.UIThread.Post(mainWindow.ShowFromActivation);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
