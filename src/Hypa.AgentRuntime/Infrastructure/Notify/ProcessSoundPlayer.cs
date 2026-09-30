using System.Diagnostics;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Notify;

/// <summary>
/// Plays a notification sound via a host helper. Missing files and helpers are silent.
/// Does not spawn a PTY.
/// </summary>
public sealed class ProcessSoundPlayer : INotificationSoundPlayer
{
    private readonly string _configDirectory;

    public ProcessSoundPlayer(string? configDirectory = null)
    {
        _configDirectory = configDirectory ?? Environment.CurrentDirectory;
    }

    public Task PlayAsync(string kind, string? path, CancellationToken ct = default)
    {
        _ = kind;
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_configDirectory, path));
        if (!File.Exists(resolved))
            return Task.CompletedTask;

        try
        {
            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("afplay", [resolved]) { RedirectStandardOutput = true, RedirectStandardError = true }
                : OperatingSystem.IsLinux()
                    ? new ProcessStartInfo("paplay", [resolved]) { RedirectStandardOutput = true, RedirectStandardError = true }
                    : null;
            if (start is null)
                return Task.CompletedTask;
            start.UseShellExecute = false;
            var proc = Process.Start(start);
            if (proc is null)
                return Task.CompletedTask;
            _ = WaitThenDisposeAsync(proc);
            return Task.CompletedTask;
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    private static async Task WaitThenDisposeAsync(Process proc)
    {
        try
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
        finally
        {
            proc.Dispose();
        }
    }
}
