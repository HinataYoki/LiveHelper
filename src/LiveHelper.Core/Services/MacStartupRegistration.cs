using System.Diagnostics;
using System.Security;
using System.Text;

namespace LiveHelper.Core.Services;

internal static class MacStartupRegistration
{
    private const string Label = "io.github.hinatayoki.livehelper";

    public static StartupRegistrationResult Apply(bool enabled)
    {
        try
        {
            var plistPath = GetPlistPath();
            if (!enabled)
            {
                RunLaunchctl(["bootout", $"gui/{GetUserId()}/{Label}"]);
                if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                }

                return StartupRegistrationResult.Ok("已关闭登录时自动启动。");
            }

            var target = GetLaunchTarget();
            if (target is null)
            {
                return StartupRegistrationResult.Failure(
                    "无法确定 LiveHelper 的应用路径；请把 LiveHelper.app 放进“应用程序”后再开启。");
            }

            if (target.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase))
            {
                return StartupRegistrationResult.Failure(
                    "当前应用运行在 Gatekeeper 的临时目录中。请把 LiveHelper.app 移到“应用程序”后重新打开。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            File.WriteAllText(plistPath, BuildPlist(target), new UTF8Encoding(false));

            var userId = GetUserId();
            RunLaunchctl(["bootout", $"gui/{userId}/{Label}"]);
            var result = RunLaunchctl(["bootstrap", $"gui/{userId}", plistPath]);
            if (!result.Succeeded)
            {
                // Older launchd versions still accept load -w for user agents.
                result = RunLaunchctl(["load", "-w", plistPath]);
            }

            return result.Succeeded
                ? StartupRegistrationResult.Ok("已启用登录时自动启动。")
                : StartupRegistrationResult.Failure(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "写入登录启动项失败。"
                        : $"写入登录启动项失败：{result.Error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return StartupRegistrationResult.Failure($"写入登录启动项失败：{ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(GetPlistPath()))
            {
                return false;
            }

            return RunLaunchctl(["print", $"gui/{GetUserId()}/{Label}"]).Succeeded;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetLaunchTarget()
    {
        var bundle = GetBundlePath();
        return bundle ?? Environment.ProcessPath;
    }

    private static string GetPlistPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            $"{Label}.plist");

    private static string? GetBundlePath()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var macOsDirectory = Path.GetDirectoryName(executable);
        var contentsDirectory = macOsDirectory is null ? null : Path.GetDirectoryName(macOsDirectory);
        var bundleDirectory = contentsDirectory is null ? null : Path.GetDirectoryName(contentsDirectory);
        return bundleDirectory is not null
               && bundleDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundleDirectory
            : null;
    }

    private static string BuildPlist(string target)
    {
        var arguments = target.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? $"""
                 <string>/usr/bin/open</string>
                 <string>-gj</string>
                 <string>{SecurityElement.Escape(target)}</string>
               """
            : $"""
                 <string>{SecurityElement.Escape(target)}</string>
               """;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{Label}</string>
              <key>ProgramArguments</key>
              <array>
            {arguments}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>LimitLoadToSessionType</key>
              <string>Aqua</string>
            </dict>
            </plist>
            """;
    }

    private static LaunchctlResult RunLaunchctl(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/launchctl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new LaunchctlResult(false, string.Empty, "launchctl 未能启动。");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            return new LaunchctlResult(false, output, "launchctl 超时。");
        }

        return new LaunchctlResult(process.ExitCode == 0, output.Trim(), error.Trim());
    }

    private static uint GetUserId()
    {
        using var result = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/id",
            ArgumentList = { "-u" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        if (result is not null)
        {
            var text = result.StandardOutput.ReadToEnd().Trim();
            if (result.WaitForExit(2000) && uint.TryParse(text, out var userId))
            {
                return userId;
            }
        }

        throw new InvalidOperationException("无法读取当前用户 ID。");
    }

    private readonly record struct LaunchctlResult(bool Succeeded, string Output, string Error);
}
