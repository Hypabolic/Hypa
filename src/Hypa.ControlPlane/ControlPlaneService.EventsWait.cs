using System.Text.Json;
using System.Threading.Channels;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    /// <summary>
    /// Pulse only for pane-gone, occupant replace, and timeout.
    /// Match comes from the status event stream.
    /// </summary>
    private static readonly TimeSpan EventsWaitPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _agentStatusWaitGate = new();
    private readonly List<AgentStatusWaitObserver> _agentStatusWaitObservers = [];

    /// <summary>
    /// Optional test injection after the wait pins occupant generation.
    /// </summary>
    internal Func<Task>? EventsWaitAfterPinAsync { get; set; }

    /// <summary>
    // / One-shot wait.
    /// Every <c>pane.agent_status_changed</c> emit (report, detection, process
    /// exit) publishes onto this waiter. This method does not create a lasting
    /// <c>events.subscribe</c> registration.
    /// </summary>
    internal async Task<JsonElement> HandleEventsWaitAsync(
        EventsWaitParams p, CancellationToken ct)
    {
        var match = p.MatchEvent
            ?? throw WaitFault(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));

        if (!IsSupportedAgentStatusWait(match.Event))
        {
            throw WaitFault(
                ProtocolErrorCodes.InvalidParams,
                EventsWaitErrors.UnsupportedMatch,
                EventsWaitErrors.UnsupportedMatchMessage);
        }

        var paneId = RequireField(match.PaneId, "pane_id");
        var wanted = RequireWaitAgentStatus(match.AgentStatus);
        if (p.TimeoutMs is < 0)
        {
            throw WaitFault(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.InvalidParams,
                ProtocolErrors.MeaningOf(ProtocolErrorCodes.InvalidParams));
        }

        var observer = RegisterAgentStatusWait(paneId);
        try
        {
            PaneState paneAtStart;
            try
            {
                paneAtStart = RefreshAgentPane(paneId);
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
            {
                throw PaneGone(ex.Message);
            }

            var pinnedGen = NormalizeOccupantGeneration(paneAtStart.OccupantGeneration);
            if (HasUncommittedReplacement(paneId))
            {
                throw WaitFault(
                    ProtocolErrorCodes.OccupantReplaced,
                    EventsWaitErrors.AgentNotRunning,
                    EventsWaitErrors.AgentNotRunningMessage);
            }

            if (EventsWaitAfterPinAsync is not null)
                await EventsWaitAfterPinAsync().ConfigureAwait(false);

            var startStatus = paneAtStart.AgentStatus.ToString().ToLowerInvariant();
            if (string.Equals(startStatus, wanted, StringComparison.Ordinal))
            {
                return OkTyped(
                    BuildWaitMatched(
                        paneAtStart.Id.Value,
                        paneAtStart.WorkspaceId.Value,
                        paneAtStart.AgentKind,
                        pinnedGen,
                        startStatus),
                    ProtocolJsonContext.Default.EventsWaitResult);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (p.TimeoutMs is int timeoutMs)
                cts.CancelAfter(timeoutMs);

            while (true)
            {
                while (observer.Events.Reader.TryRead(out var ev))
                {
                    if (!string.Equals(ev.PaneId, paneId, StringComparison.Ordinal))
                        continue;
                    if (IsOccupantReplaced(pinnedGen, ev.OccupantGeneration)
                        || HasUncommittedReplacement(paneId))
                    {
                        throw WaitFault(
                            ProtocolErrorCodes.OccupantReplaced,
                            EventsWaitErrors.AgentNotRunning,
                            EventsWaitErrors.AgentNotRunningMessage);
                    }

                    try
                    {
                        ThrowIfWaitPaneUnusable(paneId, pinnedGen);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
                    {
                        throw PaneGone(ex.Message);
                    }

                    if (string.Equals(ev.AgentStatus, wanted, StringComparison.Ordinal))
                    {
                        return OkTyped(
                            BuildWaitMatched(
                                ev.PaneId,
                                ev.WorkspaceId,
                                ev.Agent,
                                pinnedGen,
                                ev.AgentStatus),
                            ProtocolJsonContext.Default.EventsWaitResult);
                    }
                }

                try
                {
                    ThrowIfWaitPaneUnusable(paneId, pinnedGen);
                }
                catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
                {
                    throw PaneGone(ex.Message);
                }

                if (cts.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                        ct.ThrowIfCancellationRequested();
                    throw WaitFault(
                        ProtocolErrorCodes.InvalidState,
                        EventsWaitErrors.Timeout,
                        EventsWaitErrors.TimeoutMessage);
                }

                try
                {
                    await WaitForStatusEventOrPulseAsync(observer, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw WaitFault(
                        ProtocolErrorCodes.InvalidState,
                        EventsWaitErrors.Timeout,
                        EventsWaitErrors.TimeoutMessage);
                }
            }
        }
        finally
        {
            UnregisterAgentStatusWait(observer);
        }
    }

    private AgentStatusWaitObserver RegisterAgentStatusWait(string paneId)
    {
        var observer = new AgentStatusWaitObserver { PaneId = paneId };
        lock (_agentStatusWaitGate)
            _agentStatusWaitObservers.Add(observer);
        return observer;
    }

    private void UnregisterAgentStatusWait(AgentStatusWaitObserver observer)
    {
        lock (_agentStatusWaitGate)
            _agentStatusWaitObservers.Remove(observer);
        observer.Events.Writer.TryComplete();
    }

    private void PublishAgentStatusWaitEvent(PaneState pane)
    {
        AgentStatusWaitObserver[] waiters;
        lock (_agentStatusWaitGate)
        {
            if (_agentStatusWaitObservers.Count == 0)
                return;
            waiters = [.. _agentStatusWaitObservers];
        }

        var paneId = pane.Id.Value;
        var ev = new AgentStatusWaitEvent(
            paneId,
            pane.WorkspaceId.Value,
            pane.AgentKind,
            NormalizeOccupantGeneration(pane.OccupantGeneration),
            pane.AgentStatus.ToString().ToLowerInvariant());
        foreach (var waiter in waiters)
        {
            if (!string.Equals(waiter.PaneId, paneId, StringComparison.Ordinal))
                continue;
            waiter.Events.Writer.TryWrite(ev);
        }
    }

    private void ThrowIfWaitPaneUnusable(string paneId, int pinnedGen)
    {
        var pane = _state.GetPane(new PaneId(paneId))
            ?? throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");
        if (IsOccupantReplaced(pinnedGen, pane.OccupantGeneration)
            || HasUncommittedReplacement(paneId))
        {
            throw WaitFault(
                ProtocolErrorCodes.OccupantReplaced,
                EventsWaitErrors.AgentNotRunning,
                EventsWaitErrors.AgentNotRunningMessage);
        }
    }

    private static async Task WaitForStatusEventOrPulseAsync(
        AgentStatusWaitObserver observer,
        CancellationToken waitCt)
    {
        using var pulse = CancellationTokenSource.CreateLinkedTokenSource(waitCt);
        pulse.CancelAfter(EventsWaitPollInterval);
        try
        {
            if (!await observer.Events.Reader.WaitToReadAsync(pulse.Token).ConfigureAwait(false))
                await Task.Delay(EventsWaitPollInterval, waitCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!waitCt.IsCancellationRequested)
        {
        }
    }

    private static bool IsSupportedAgentStatusWait(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return false;
        var name = eventName.Trim();
        return string.Equals(name, EventsWaitMatch.PaneAgentStatusChanged, StringComparison.Ordinal);
    }

    private static string RequireWaitAgentStatus(string? agentStatus)
    {
        var raw = RequireField(agentStatus, "agent_status").Trim().ToLowerInvariant();
        return raw switch
        {
            "working" or "blocked" or "idle" or "unknown" or "done" => raw,
            _ => throw WaitFault(
                ProtocolErrorCodes.InvalidParams,
                ProtocolErrors.InvalidParams,
                "agent_status must be working, blocked, idle, unknown, or done"),
        };
    }

    private static EventsWaitResult BuildWaitMatched(
        string paneId,
        string workspaceId,
        string? agent,
        int pinnedGen,
        string status) =>
        new()
        {
            Type = EventsWaitResult.WaitMatched,
            Event = new EventsWaitMatchedEvent
            {
                Event = EventsWaitMatch.PaneAgentStatusChanged,
                Data = new EventsWaitMatchedEventData
                {
                    PaneId = paneId,
                    WorkspaceId = workspaceId,
                    AgentStatus = status,
                    Agent = agent,
                    OccupantGeneration = pinnedGen,
                },
            },
        };

    private static ControlPlaneException PaneGone(string message) =>
        WaitFault(ProtocolErrorCodes.NotFound, EventsWaitErrors.PaneNotFound, message);

    private static ControlPlaneException WaitFault(int code, string errorCode, string message) =>
        new(code, message, errorCode);

    private sealed record AgentStatusWaitEvent(
        string PaneId,
        string WorkspaceId,
        string? Agent,
        int OccupantGeneration,
        string AgentStatus);

    private sealed class AgentStatusWaitObserver
    {
        public required string PaneId { get; init; }

        public Channel<AgentStatusWaitEvent> Events { get; } =
            Channel.CreateUnbounded<AgentStatusWaitEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    }
}
