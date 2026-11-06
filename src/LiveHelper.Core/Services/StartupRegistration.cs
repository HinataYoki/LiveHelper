using Microsoft.Win32;

namespace LiveHelper.Core.Services;

public sealed record StartupRegistrationResult(bool Succeeded, string Message)
{
    public static StartupRegistrationResult Ok(string message) => new(true, message);

    public static StartupRegistrationResult Failure(string message) => new(false, message);
}

/// <summary>
/// Registers the current executable in the per-user Windows startup key. No administrator
/// rights are required, and uninstalling or changing the setting removes the value.
/// </summary>
public static class StartupRegistration
{
    public const string ValueName = "LiveHelper";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static StartupRegistrationResult Apply(
        bool enabled,
        string? executablePath = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacStartupRegistration.Apply(enabled);
        }

        if (!OperatingSystem.IsWindows())
        {
            return StartupRegistrationResult.Failure("开机自启目前仅支持 Windows 和 macOS。");
        }

        try
        {
            var path = executablePath ?? GetCurrentExecutablePath();
            if (enabled
                && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            {
                return StartupRegistrationResult.Failure("当前程序路径不完整，无法注册开机自启。");
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return StartupRegistrationResult.Failure("无法打开当前用户的启动项注册表。");
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return StartupRegistrationResult.Ok("已关闭开机自启。");
            }

            key.SetValue(ValueName, Quote(path), RegistryValueKind.String);
            return StartupRegistrationResult.Ok("已启用开机自启。");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return StartupRegistrationResult.Failure($"写入开机启动项失败：{ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacStartupRegistration.IsEnabled();
        }

        return TryGetRegisteredCommand(out _);
    }

    public static bool TryGetRegisteredCommand(out string command)
    {
        command = string.Empty;
        if (OperatingSystem.IsMacOS())
        {
            command = MacStartupRegistration.GetLaunchTarget() ?? string.Empty;
            return command.Length > 0 && MacStartupRegistration.IsEnabled();
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            command = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetCurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "LiveHelper.exe");
    }

    private static string Quote(string value) => $"\"{value}\"";
}
