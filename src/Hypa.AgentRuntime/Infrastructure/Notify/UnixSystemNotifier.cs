using System.Diagnostics;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Notify;

/// <summary>
/// Desktop notification via osascript (macOS) or notify-send (Linux).
/// Missing helpers are silent and do not fail attach.
/// </summary>
public sealed class UnixSystemNotifier : ISystemNotifier
{
    public Task ShowAsync(string title, string? body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.CompletedTask;

        try
        {
            ProcessStartInfo? start = null;
            if (OperatingSystem.IsMacOS())
            {
                var text = string.IsNullOrWhiteSpace(body)
                    ? $"display notification \"{EscapeAppleScript(title)}\""
                    : $"display notification \"{EscapeAppleScript(body)}\" with title \"{EscapeAppleScript(title)}\"";
                start = new ProcessStartInfo("osascript", ["-e", text])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                start = new ProcessStartInfo("notify-send", [title, body ?? ""])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
            }

            if (start is null)
                return Task.CompletedTask;
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

    private static string EscapeAppleScript(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
