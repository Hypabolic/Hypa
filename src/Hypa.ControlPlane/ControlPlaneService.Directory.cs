using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    private readonly ProcessSidebarGitStatus _workspaceDirectoryGit;
    private readonly ITimer _workspaceDirectoryTimer;
    private int _directoryRefreshRunning;
    private long _directoryRefreshNotBeforeTicks;
    private readonly Dictionary<string, (string Cwd, string? PaneId, SidebarGitInfo Git)> _directoryPublished =
        new(StringComparer.Ordinal);

    private void StartWorkspaceDirectoryTracking()
    {
        if (IsShuttingDown)
            return;
        try
        {
            _workspaceDirectoryTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch (ObjectDisposedException) when (IsShuttingDown)
        {
        }
    }

    private void RequestWorkspaceDirectoryRefresh()
    {
        var now = _time.GetUtcNow().Ticks;
        if (IsShuttingDown || now < Volatile.Read(ref _directoryRefreshNotBeforeTicks)
            || Interlocked.CompareExchange(ref _directoryRefreshRunning, 1, 0) != 0)
            return;
        // A busy agent may emit hundreds of chunks per second. Observe at
        // most four times per second; the timer catches a skipped final report.
        Volatile.Write(ref _directoryRefreshNotBeforeTicks, now + TimeSpan.FromMilliseconds(250).Ticks);
        _ = Task.Run(RefreshWorkspaceDirectoriesAsync);
    }

    internal async Task RefreshWorkspaceDirectoriesForTestsAsync()
    {
        while (Interlocked.CompareExchange(ref _directoryRefreshRunning, 1, 0) != 0)
            await Task.Delay(1).ConfigureAwait(false);
        await RefreshWorkspaceDirectoriesAsync().ConfigureAwait(false);
    }

    private async Task RefreshWorkspaceDirectoriesAsync()
    {
        try
        {
            KeyValuePair<string, IPaneRuntime>[] runtimes;
            lock (_gate)
                runtimes = _runtimes.ToArray();
            var changed = false;
            foreach (var (id, runtime) in runtimes)
            {
                if (IsShuttingDown || !runtime.IsAlive)
                    continue;
                var pane = _state.GetPane(new PaneId(id));
                if (pane is null)
                    continue;
                var cwd = PaneWorkingDirectory.Validate(runtime.ReadWorkingDirectory()
                    ?? (runtime.Pid is { } pid ? _processInfoProbe.TryGetWorkingDirectory(pid) : null), Directory.Exists);
                if (cwd is null || cwd == pane.Cwd)
                    continue;
                await _bindingMutationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (IsShuttingDown || SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
                        continue;
                    _state.UpdatePane(pane.Id, current =>
                    {
                        if (current.OccupantGeneration != pane.OccupantGeneration
                            || !ShouldApplyOccupantStatus(runtime))
                            return current;
                        changed = true;
                        return current with { Cwd = cwd };
                    });
                }
                finally
                {
                    _bindingMutationGate.Release();
                }
            }
            if (changed)
                await PersistGraphAsync(requireDurable: false).ConfigureAwait(false);

            var session = _state.Snapshot();
            var liveIds = session.Workspaces.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _directoryPublished.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                _directoryPublished.Remove(removed);
            foreach (var workspace in session.Workspaces.Values)
            {
                if (IsShuttingDown || SessionLifecycle.IsFrozen(session.LifecycleState))
                    break;
                var source = WorkspaceDirectoryIdentity.Source(session, workspace);
                var cwd = source?.Cwd ?? workspace.Cwd;
                _workspaceDirectoryGit.RequestRefresh(cwd);
                var next = (cwd, source?.Id.Value, _workspaceDirectoryGit.Resolve(cwd));
                if (_directoryPublished.TryGetValue(workspace.Id.Value, out var previous) && previous == next)
                    continue;
                _directoryPublished[workspace.Id.Value] = next;
                await EmitWorkspaceLifecycleAsync(workspace.Id.Value, "directory_changed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Workspace directory refresh failed");
        }
        finally
        {
            Volatile.Write(ref _directoryRefreshRunning, 0);
        }
    }
}
