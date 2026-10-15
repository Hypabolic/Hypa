using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    private readonly ProgramStatusAttentionPolicy _programAttention;
    private readonly ITimer _programAttentionTimer;
    private readonly ProgramStatusEmitCoalescer _programStatusEmits;
    private readonly ITimer _programStatusEmitTimer;

    // Reports, resets, and prompt starts arrive from the pane read loop, in output order.
    // The store of the pane changes here, and one event goes out when it changed.
    private void OnRuntimeProgramStatus(IPaneRuntime runtime, IReadOnlyList<ProgramStatusSignal> signals)
    {
        if (signals.Count == 0)
            return;

        lock (_gate)
        {
            if (!IsRegisteredOccupantUnlocked(runtime))
                return;
        }

        UpdateProgramStatus(runtime, store =>
        {
            foreach (var signal in signals)
            {
                store = signal.Kind switch
                {
                    ProgramStatusSignalKind.Report when signal.Report is { } report => store.Apply(report, signal.Source),
                    ProgramStatusSignalKind.Reset => store.On(ProgramStatusLifecycleEvent.FullReset),
                    ProgramStatusSignalKind.PromptStart => store.On(ProgramStatusLifecycleEvent.PromptStart),
                    _ => store,
                };
            }

            return store;
        });
    }

    /// <summary>
    /// Change the program status store of the pane of <paramref name="runtime"/>.
    /// The write point emits the events.
    /// </summary>
    private PaneState? UpdateProgramStatus(IPaneRuntime runtime, Func<ProgramStatusStore, ProgramStatusStore> change)
    {
        var now = _time.GetUtcNow();
        return UpdatePaneEmittingStatus(runtime.Id, p =>
        {
            var next = change(p.ProgramStatusStore);
            return ReferenceEquals(next, p.ProgramStatusStore)
                ? p
                : ProgramStatusAuthorityPolicy.Apply(p, next, now);
        });
    }

    /// <summary>
    /// The program status of a pane changed. The coalescer decides when the event goes out.
    /// This method starts the emit it names and arms the one timer for the others.
    /// </summary>
    private void RequestProgramStatusEmit(PaneId id)
    {
        if (_programStatusEmits.Request(id.Value) == ProgramStatusEmitDecision.EmitNow)
            _ = RunProgramStatusEmitAsync(id);
        else
            ArmProgramStatusEmitTimer();
    }

    /// <summary>
    /// A label of a pane, tab, or workspace changed. The views of the panes that hold records name the labels,
    /// so the views go out again. This is the same coalesced path as a change of the records.
    /// </summary>
    private void RepublishProgramStatusViews(Func<PaneState, bool> inScope)
    {
        foreach (var pane in _state.ListPanes())
        {
            if (!pane.ProgramStatusStore.IsEmpty && inScope(pane))
                RequestProgramStatusEmit(pane.Id);
        }
    }

    /// <summary>
    /// The ids of the panes in scope that hold records the person has not seen.
    /// Call it before a change that can mark panes seen, then pass the result to
    /// <see cref="RepublishNewlySeenProgramStatus"/>.
    /// </summary>
    private HashSet<string> UnseenProgramStatusPanes(Func<PaneState, bool> inScope) =>
        _state.ListPanes()
            .Where(p => !p.ProgramStatusStore.IsEmpty && !p.ProgramStatusSeen && inScope(p))
            .Select(p => p.Id.Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A pane became seen: the tab view and the session view stop counting a done or error of the pane.
    /// The views go out again through the coalescer.
    /// </summary>
    private void RepublishNewlySeenProgramStatus(HashSet<string> unseenBefore)
    {
        foreach (var id in unseenBefore)
        {
            if (_state.GetPane(new PaneId(id)) is { ProgramStatusSeen: true })
                RequestProgramStatusEmit(new PaneId(id));
        }
    }

    /// <summary>The pane is unseen, or it is a done status that a view turns into idle.</summary>
    private bool NeedsSeenMark(PaneState pane) =>
        !pane.Seen || (pane.AgentStatus == AgentStatus.Done && !HoldsSemanticAuthority(pane));

    /// <summary>
    /// The person has seen the pane. One rule for <c>pane.focus</c>, <c>agent.focus</c>, and <c>pane.mark_seen</c>.
    /// A done status without an authority is a seen done: it becomes idle. The write point emits what changed.
    /// Returns the pane as it is after the change.
    /// </summary>
    private PaneState MarkPaneSeen(PaneState pane)
    {
        if (!NeedsSeenMark(pane))
            return pane;
        return UpdatePaneEmittingStatus(pane.Id, current =>
        {
            var nextStatus = HoldsSemanticAuthority(current)
                || current.AgentStatus != AgentStatus.Done
                ? current.AgentStatus
                : AgentStatus.Idle;
            return current with
            {
                AgentStatus = nextStatus,
                Seen = true,
                UpdatedAt = _time.GetUtcNow(),
            };
        }) ?? pane;
    }

    /// <summary>
    /// <c>pane.mark_seen</c>: the frontend says that the person can see these panes. Focus does not change.
    /// A pane that is gone or seen already is skipped, so the call is idempotent.
    /// The result lists the panes that changed. The views of a newly seen pane go out again.
    /// </summary>
    private async Task<JsonObject> PaneMarkSeenAsync(
        PaneMarkSeenParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneMarkSeen);
        var ids = (p.PaneIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var changed = new JsonArray();
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.PaneMarkSeen);
            EnsureNotFrozenForMutation(ProtocolMethods.PaneMarkSeen);
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var unseen = UnseenProgramStatusPanes(x => wanted.Contains(x.Id.Value));
            foreach (var id in ids)
            {
                if (_state.GetPane(new PaneId(id)) is not { } pane || !NeedsSeenMark(pane))
                    continue;
                _ = MarkPaneSeen(pane);
                changed.Add((JsonNode)JsonValue.Create(id));
            }

            RepublishNewlySeenProgramStatus(unseen);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        if (changed.Count > 0)
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new JsonObject { ["marked"] = changed };
    }

    internal async Task<JsonElement> HandlePaneMarkSeenAsync(
        PaneMarkSeenParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        Ok(await PaneMarkSeenAsync(p, connection, ct).ConfigureAwait(false));

    /// <summary>Test hook: no program status emit is pending or in flight.</summary>
    internal bool ProgramStatusEmitsAreIdleForTest => !_programStatusEmits.IsBusy;

    private readonly object _programStatusEmitTimerGate = new();
    private readonly object _programAttentionTimerGate = new();

    private void OnProgramStatusEmitDue()
    {
        if (IsShuttingDown)
            return;

        foreach (var id in _programStatusEmits.TakeDue())
            _ = RunProgramStatusEmitAsync(new PaneId(id));
        ArmProgramStatusEmitTimer();
    }

    /// <summary>Emit the latest state of a pane. The state is read here, not when the change happened.</summary>
    private async Task RunProgramStatusEmitAsync(PaneId id)
    {
        try
        {
            if (!IsShuttingDown && _state.GetPane(id) is { } pane)
                await EmitProgramStatusChangedAsync(pane).ConfigureAwait(false);
        }
        finally
        {
            _programStatusEmits.Complete(id.Value);
            // Any path that removed the pane leaves no history behind.
            if (_state.GetPane(id) is null)
                _programStatusEmits.Forget(id.Value);
            ArmProgramStatusEmitTimer();
        }
    }

    private void ArmProgramStatusEmitTimer()
    {
        if (IsShuttingDown)
            return;

        // Read the due time and set the timer as one step. Two callers that interleave could
        // otherwise set an older due time (none) after a newer one and lose the wake-up.
        lock (_programStatusEmitTimerGate)
        {
            var next = _programStatusEmits.NextDueAt;
            try
            {
                _programStatusEmitTimer.Change(
                    next is { } at ? Max(at - _time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) when (IsShuttingDown)
            {
                // The session is closing.
            }
        }

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    /// <summary>
    /// Publish the program status of a pane and the recomputed view of a tab.
    /// <paramref name="paneGone"/> says the pane left the session: the event has no records
    /// and no pane view, and the tab view no longer counts the pane.
    /// <paramref name="tabId"/> names another tab to refresh (the tab a pane moved out of).
    /// The pane view stays valid in that case, so a frontend loses nothing.
    /// </summary>
    private async Task EmitProgramStatusChangedAsync(PaneState pane, bool paneGone = false, TabId? tabId = null)
    {
        if (_journal is null)
            return;

        var tab = tabId ?? pane.TabId;
        var payload = RuntimeEventPayloadJson.WritePaneProgramStatusChanged(
            pane.Id.Value,
            tab.Value,
            pane.WorkspaceId.Value,
            pane.OccupantGeneration,
            paneGone ? [] : ReadableProgramStatus(pane.ProgramStatusStore),
            paneGone ? null : ProgramStatusViewOf(pane),
            TabProgramStatusViewOf(tab),
            SessionProgramStatusViewOf());

        try
        {
            await EmitReliableAsync(
                    EventClass.Lifecycle,
                    ProtocolEventTypes.PaneProgramStatusChanged,
                    payload,
                    CancellationToken.None,
                    stillValid: paneGone ? null : () => OccupantGenerationIsCurrent(pane))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit pane.program_status_changed for {PaneId}", pane.Id);
        }
    }

    /// <summary>
    /// The program status of a pane changed. The policy decides if the pane needs the person.
    /// This method sends the pane view to the policy and publishes what the policy returns.
    /// </summary>
    private void ObserveProgramAttention(PaneState pane)
    {
        var items = _programAttention.Observe(
            pane.Id.Value,
            pane.TabId.Value,
            pane.WorkspaceId.Value,
            ProgramStatusViewOf(pane),
            pane.AgentAuthority is { IsProgramStatus: true });
        PublishProgramAttention(items);
    }

    private void OnProgramAttentionDue()
    {
        if (IsShuttingDown)
            return;

        PublishProgramAttention(_programAttention.Release());
    }

    private void PublishProgramAttention(IReadOnlyList<ProgramStatusAttentionItem> items)
    {
        foreach (var item in items)
            _ = EmitProgramAttentionAsync(item);

        // A held item goes out when the limit allows. One timer serves all panes.
        lock (_programAttentionTimerGate)
        {
            var next = _programAttention.NextReleaseAt;
            try
            {
                _programAttentionTimer.Change(
                    next is { } at ? Max(at - _time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) when (IsShuttingDown)
            {
                // The session is closing.
            }
        }

        static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    private async Task EmitProgramAttentionAsync(ProgramStatusAttentionItem item)
    {
        if (_journal is null || _state.GetPane(new PaneId(item.PaneId)) is not { } pane)
            return;

        var payload = RuntimeEventPayloadJson.WritePaneProgramAttention(pane.OccupantGeneration, item);
        try
        {
            await EmitReliableAsync(
                    EventClass.Lifecycle,
                    ProtocolEventTypes.PaneProgramAttention,
                    payload,
                    CancellationToken.None,
                    stillValid: () => OccupantGenerationIsCurrent(pane))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit pane.program_attention for {PaneId}", item.PaneId);
        }
    }

    /// <summary>
    /// Records as a reader sees them: sorted root first, <c>app</c> resolved, free text redacted.
    /// The pane JSON and the event payload both use this method.
    /// A program writes <c>msg</c> and <c>title</c>, so they can echo a secret.
    /// The redactor removes known secret patterns from that text only. Ids and <c>app</c> stay as reported.
    /// </summary>
    private IReadOnlyList<ProgramStatusRecord> ReadableProgramStatus(ProgramStatusStore store)
    {
        var records = store.Resolved();
        var readable = new List<ProgramStatusRecord>(records.Count);
        foreach (var r in records)
        {
            if (r.Msg is null && r.Title is null)
            {
                readable.Add(r);
                continue;
            }

            var text = new System.Text.Json.Nodes.JsonObject { ["msg"] = r.Msg, ["title"] = r.Title };
            var redacted = System.Text.Json.Nodes.JsonNode.Parse(
                _redactor.RedactJsonPayload(ProtocolEventTypes.PaneProgramStatusChanged, text.ToJsonString()));
            readable.Add(r with
            {
                Msg = (string?)redacted?["msg"],
                Title = (string?)redacted?["title"],
            });
        }

        return readable;
    }

    /// <summary>
    /// The view of one pane for any frontend. The projection holds the rules.
    /// This method reads the labels and the redacted records.
    /// </summary>
    private ProgramStatusView? ProgramStatusViewOf(PaneState pane)
    {
        if (pane.ProgramStatusStore.IsEmpty)
            return null;

        var workspace = _state.GetWorkspace(pane.WorkspaceId);
        var tab = _state.GetTab(pane.TabId);
        return ProgramStatusViewProjection.ForPane(
            ReadableProgramStatus(pane.ProgramStatusStore),
            new ProgramStatusSourceLabel(
                LabelOrId(workspace?.Label, pane.WorkspaceId.Value),
                LabelOrId(tab?.Label, pane.TabId.Value),
                LabelOrId(pane.Label, pane.Id.Value)),
            pane.ProgramStatusChangedAt,
            pane.ProgramStatusSeen);
    }

    private TabProgramStatusView? TabProgramStatusViewOf(TabId tabId) =>
        ProgramStatusViewProjection.ForTab(
            tabId.Value,
            _state.ListPanes()
                .Where(p => p.TabId == tabId && !p.ProgramStatusStore.IsEmpty)
                .OrderBy(p => p.Id.Value, StringComparer.Ordinal)
                .Select(ProgramStatusViewOf)
                .OfType<ProgramStatusView>());

    /// <summary>
    /// The most urgent pane of the session. The projection breaks a tie by the most recent change.
    /// Panes are sorted by id so a tie of time is stable.
    /// </summary>
    private SessionProgramStatusView? SessionProgramStatusViewOf() =>
        ProgramStatusViewProjection.ForSession(
            _state.ListPanes()
                .Where(p => !p.ProgramStatusStore.IsEmpty)
                .OrderBy(p => p.Id.Value, StringComparer.Ordinal)
                .Select(p => (PaneId: p.Id.Value, View: ProgramStatusViewOf(p)))
                .Where(x => x.View is not null)
                .Select(x => (x.PaneId, View: x.View!)));

    private static string LabelOrId(string? label, string id) =>
        string.IsNullOrWhiteSpace(label) ? id : label;

    private System.Text.Json.Nodes.JsonArray ProgramStatusToJson(ProgramStatusStore store) =>
        ProgramStatusRecordWire.ToArray(ReadableProgramStatus(store));
}
