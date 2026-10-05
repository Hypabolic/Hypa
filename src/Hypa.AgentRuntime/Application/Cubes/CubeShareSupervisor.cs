using Hypa.AgentRuntime.Domain.Cubes;

namespace Hypa.AgentRuntime.Application.Cubes;

/// <summary>
/// Keeps the share listener alive for the life of the mux. A listener that
/// exits or fails to start is started again with backoff while share is
/// enabled. Enabled settings persist so the next mux resumes share.
/// </summary>
public sealed class CubeShareSupervisor : ICubeShareHost, IAsyncDisposable
{
    internal static readonly TimeSpan StartWait = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>A listener that ran this long resets the backoff.</summary>
    internal static readonly TimeSpan StableRun = TimeSpan.FromMinutes(1);

    private readonly ICubeShareListenerLauncher _launcher;
    private readonly ICubeShareSettingsStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    internal const string NotSavedDetail =
        "share is on, but share.json could not be written, so it will not resume after a mux restart";
    internal const string NotClearedDetail =
        "share is off, but share.json could not be removed, so the next mux will share again";

    private CubeShareStatus _status = CubeShareStatus.Stopped;
    private string? _persistError;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private bool _disposed;

    public CubeShareSupervisor(
        ICubeShareListenerLauncher launcher,
        ICubeShareSettingsStore store,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(store);
        _launcher = launcher;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public CubeShareStatus Status
    {
        get
        {
            lock (_statusGate)
                return _status;
        }
    }

    public async Task<CubeShareStatus> StartAsync(CubeShareSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Task settled;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = Status;
            // A retrying share starts again now instead of waiting out its backoff.
            if (_loop is not null
                && current.State == CubeShareStates.Running
                && Equals(current.Settings, settings))
            {
                return current;
            }

            await StopLoopAsync().ConfigureAwait(false);
            _persistError = _store.SaveEnabled(settings) ? null : NotSavedDetail;
            settled = StartLoop(settings);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await settled.WaitAsync(StartWait, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return Status;
    }

    public async Task<CubeShareStatus> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopLoopAsync().ConfigureAwait(false);
            _persistError = _store.Clear() ? null : NotClearedDetail;
            SetStatus(CubeShareStatus.Stopped);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Start share again when the last mux left it enabled. Does not wait.</summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || _loop is not null)
                return;
            if (_store.LoadEnabled() is { } settings)
                _ = StartLoop(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the listener with the mux. Share stays enabled for the next mux.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopLoopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static TimeSpan Backoff(int failures)
    {
        if (failures <= 1)
            return TimeSpan.FromSeconds(1);
        var seconds = Math.Pow(2, Math.Min(failures - 1, 10));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    private Task StartLoop(CubeShareSettings settings)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        SetStatus(new CubeShareStatus
        {
            Enabled = true,
            State = CubeShareStates.Starting,
            Settings = settings,
        });
        _loopCts = cts;
        _loop = Task.Run(() => RunAsync(settings, settled, cts.Token));
        return settled.Task;
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is not { } cts || _loop is not { } loop)
            return;
        _loopCts = null;
        _loop = null;
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunAsync(
        CubeShareSettings settings,
        TaskCompletionSource settled,
        CancellationToken ct)
    {
        var restarts = 0;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            string error;
            ICubeShareListener? listener = null;
            try
            {
                var launched = await _launcher.LaunchAsync(settings, ct).ConfigureAwait(false);
                if (launched.IsOk)
                    listener = launched.Value;
                error = launched.IsOk ? "" : launched.Error;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = string.IsNullOrWhiteSpace(ex.Message) ? "share listener did not start" : ex.Message;
            }

            if (listener is not null)
            {
                await using (listener.ConfigureAwait(false))
                {
                    SetStatus(new CubeShareStatus
                    {
                        Enabled = true,
                        State = CubeShareStates.Running,
                        Settings = settings,
                        Listen = listener.Listen,
                        Restarts = restarts,
                    });
                    settled.TrySetResult();
                    var startedAt = _time.GetUtcNow();
                    try
                    {
                        error = await listener.Exited.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_time.GetUtcNow() - startedAt >= StableRun)
                        failures = 0;
                }
            }

            failures++;
            restarts++;
            SetStatus(new CubeShareStatus
            {
                Enabled = true,
                State = CubeShareStates.Retrying,
                Settings = settings,
                Error = string.IsNullOrWhiteSpace(error) ? "share listener stopped" : error,
                Restarts = restarts,
            });
            settled.TrySetResult();
            try
            {
                await Task.Delay(Backoff(failures), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetStatus(CubeShareStatus status)
    {
        lock (_statusGate)
            _status = status with { PersistError = _persistError };
    }
}
