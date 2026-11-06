using Avalonia;
using Avalonia.Media;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Motion;

namespace LiveHelper.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        EmbeddedRecordingEngine.EnsureReady();
        EnsureNativeRuntime("win-x64", "av_libglesv2.dll", "libonigwrap.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray());
    }

    private static void EnsureNativeRuntime(string rid, params string[] libraryNames)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveHelper", "Runtime", rid);
        Directory.CreateDirectory(cacheRoot);

        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string libraryName in libraryNames)
        {
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("." + libraryName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                continue;

            string targetPath = Path.Combine(cacheRoot, libraryName);
            using var resource = assembly.GetManifestResourceStream(resourceName);
            if (resource is null)
                continue;

            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != resource.Length)
            {
                using FileStream output = File.Create(targetPath);
                resource.CopyTo(output);
            }

            try
            {
                NativeLibrary.Load(targetPath);
            }
            catch
            {
                // If an external copy is already available the app keeps working;
                // the embedded runtime is only needed for a standalone package.
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithArcFonts()
            .UseArc(arc => arc
                .UseArcControls()
                .UseArcBlocks()
                .WithTheme(theme => theme
                    .Appearance(ArcAppearance.Dark)
                    .Accent(Color.Parse("#F06435"))
                    .MotionLevel(ArcMotionLevel.Full)))
            .LogToTrace();
}
