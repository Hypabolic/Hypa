using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Runs display-only <c>tab_bar_right</c> command entries on interval.</summary>
public sealed class TabBarCommandCache
{
    public const int DefaultIntervalSeconds = 5;
    public const int DefaultTimeoutSeconds = 2;
    public const int MaxTextChars = 32;

    private readonly Func<string, TimeSpan, CancellationToken, Task<string>> _run;
    private readonly Dictionary<string, CachedCommand> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task? _refreshing;

    public TabBarCommandCache(
        Func<string, TimeSpan, CancellationToken, Task<string>>? run = null)
    {
        _run = run ?? RunProcessAsync;
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (command, item) in _items)
                copy[command] = item.Text;
            return copy;
        }
    }

    /// <summary>
    /// Starts a display-only refresh if one is not already running.
    /// Callers must not wait on process I/O; read <see cref="Snapshot"/>
    /// or apply from <paramref name="onComplete"/>.
    /// </summary>
    public void KickRefresh(
        IReadOnlyList<AttachTabBarRightEntry> entries,
        TimeProvider time,
        CancellationToken ct,
        Action? onComplete = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(time);
        if (!HasCommandEntries(entries))
            return;

        lock (_gate)
        {
            if (_refreshing is { IsCompleted: false })
                return;
            _refreshing = RefreshInBackgroundAsync(entries, time, ct, onComplete);
        }
    }

    private async Task RefreshInBackgroundAsync(
        IReadOnlyList<AttachTabBarRightEntry> entries,
        TimeProvider time,
        CancellationToken ct,
        Action? onComplete)
    {
        try
        {
            await RefreshAsync(entries, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // display-only
        }

        if (ct.IsCancellationRequested)
            return;

        try
        {
            onComplete?.Invoke();
        }
        catch
        {
            // display-only
        }
    }

    public Task DrainAsync()
    {
        Task? pending;
        lock (_gate)
            pending = _refreshing;
        return pending ?? Task.CompletedTask;
    }

    public async Task RefreshAsync(
        IReadOnlyList<AttachTabBarRightEntry> entries,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(time);
        foreach (var entry in entries)
        {
            if (entry.Kind is not TabBarRightKind.Command)
                continue;
            if (string.IsNullOrWhiteSpace(entry.Command))
                continue;

            var command = entry.Command;
            var interval = TimeSpan.FromSeconds(
                entry.IntervalSeconds is > 0
                    ? entry.IntervalSeconds.Value
                    : entry.IntervalSeconds == 0
                        ? 0
                        : DefaultIntervalSeconds);
            var timeout = TimeSpan.FromSeconds(
                entry.TimeoutSeconds is > 0 ? entry.TimeoutSeconds.Value : DefaultTimeoutSeconds);
            var now = time.GetUtcNow();
            CachedCommand? existing;
            lock (_gate)
                _items.TryGetValue(command, out existing);
            if (existing is not null
                && interval > TimeSpan.Zero
                && now - existing.LastRun < interval)
            {
                continue;
            }

            string text;
            try
            {
                text = Sanitize(await _run(command, timeout, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                text = existing?.Text ?? "";
            }

            lock (_gate)
                _items[command] = new CachedCommand(text, now);
        }
    }

    private static bool HasCommandEntries(IReadOnlyList<AttachTabBarRightEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind is TabBarRightKind.Command && !string.IsNullOrWhiteSpace(entry.Command))
                return true;
        }

        return false;
    }

    internal static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var line = raw.Replace('\r', '\n');
        var cut = line.IndexOf('\n');
        if (cut >= 0)
            line = line[..cut];
        line = line.Trim();
        return SafeDisplayText.Clip(line, MaxTextChars);
    }

    internal static async Task<string> RunProcessAsync(
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
            return "";

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = DiscardAsync(process.StandardError);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            try { await stderrTask.ConfigureAwait(false); }
            catch
            {
                // discard
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested)
                throw;
            return "";
        }
        finally
        {
            if (!SafeHasExited(process))
                KillTree(process);
        }
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[256];
            while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
            {
                // discard so a noisy stderr pipe cannot stall stdout
            }
        }
        catch
        {
            // pipe closed after kill
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best-effort
        }

        try
        {
            process.WaitForExit(1000);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private sealed record CachedCommand(string Text, DateTimeOffset LastRun);
}
