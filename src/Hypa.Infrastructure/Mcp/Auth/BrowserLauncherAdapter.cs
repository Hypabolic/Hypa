using System.ComponentModel;
using System.Diagnostics;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class BrowserLauncherAdapter : IBrowserLauncher
{
    private readonly string? _overrideCommand;

    public BrowserLauncherAdapter(string? overrideCommand = null)
    {
        _overrideCommand = overrideCommand;
    }

    public bool TryOpen(string url)
    {
        var command = _overrideCommand ?? GetBrowserCommand(DetectWsl());
        return TryLaunch(command, url);
    }

    internal static string GetBrowserCommand(bool isWsl)
    {
        if (OperatingSystem.IsWindows())
            return "explorer";

        if (OperatingSystem.IsMacOS())
            return "open";

        // Linux
        return isWsl ? "wslview" : "xdg-open";
    }

    private static bool DetectWsl()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var version = File.ReadAllText("/proc/version");
            return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLaunch(string command, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = argument,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }
}
