using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

internal static class AttachEndpointActivationPump
{
    internal static async Task RunTick(AttachLiveState live, UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        // on the single client loop.
        // activation-era PaneSurface on that same loop. ActivationPumpLoopAsync
        // and ReadRenderAsync both call RunTick; serialize the complete
        // sequence with one instance-scoped async exclusive gate.
        Interlocked.Increment(ref live.ActivationPumpTicks);
        await live.ActivationPumpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // supervisor.rs:187-199 record_status on that same loop.
            AttachSession.ApplyPendingCatalogFence(live);
            AttachSession.DrainQueuedEndpointFailures(live, tty);
            // the timeout tick so a last-moment dest snapshot can Complete.
            await DrainAttachEventsAsync(live, tty).ConfigureAwait(false);
            AttachSession.TickEndpointActivation(live, DateTimeOffset.UtcNow, tty);
        }
        finally
        {
            live.ActivationPumpGate.Release();
        }
    }

    internal static bool ShouldDropPresentationEffect(AttachLiveState live, string? eventType) =>
        ShouldDropFrozenPresentationEvent(live, eventType);

    internal static bool ShouldDropFrozenPresentationEvent(AttachLiveState live, string? eventType)
    {
        if (live.PendingActivation is null && !live.PresentationFrozen)
            return false;
        if (string.IsNullOrWhiteSpace(eventType))
            return false;
        return IsPresentationEffectEvent(eventType)
            || IsFrozenChromeOrOverlayEvent(eventType);
    }

    internal static bool TryAdmitTerminalRender(
        AttachLiveState live,
        string? endpointId,
        ulong generation,
        AttachSurfaceEvidence surface,
        UnixRawTerminal? tty)
    {
        lock (live.ActivationGate)
        {
            if (live.PendingActivation?.AcceptsEndpoint(endpointId ?? string.Empty, generation) != true)
                return false;
        }

        AttachSession.OnEndpointSurface(live, endpointId ?? string.Empty, generation, surface, tty);
        return true;
    }

    internal static bool TryAdmitTerminalRenderPayload(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        JsonElement payload,
        UnixRawTerminal? tty)
    {
        if (live.PendingActivation is null)
            return false;
        if (!TryParseSurfaceEvidence(payload, out var surface))
            return false;
        return TryAdmitTerminalRender(live, endpointId, generation, surface, tty);
    }

    private static async Task DrainAttachEventsAsync(AttachLiveState live, UnixRawTerminal? tty)
    {
        await AttachSession.DrainDestConnectCompletions(live, tty).ConfigureAwait(false);
        AttachSession.ProcessActivationCompletionsForPump(live, tty);
        var controlId = live.ConnectedPlacementId;
        DrainClient(
            live,
            live.ControlSlot?.Client,
            controlId,
            AttachSession.LiveEndpointGeneration(live, controlId ?? string.Empty),
            live,
            tty);
        if (live.PendingConnectOutcome?.DestClient is { } dest)
        {
            var endpointId = live.PendingActivation?.Target.EndpointId
                ?? live.PendingConnectCube?.Id;
            var generation = live.PendingConnectOutcome.TargetConnectionGeneration;
            DrainClient(live, dest, endpointId, generation, live, tty);
        }
    }

    private static void DrainClient(
        AttachLiveState live,
        ControlPlaneClient? client,
        string? endpointId,
        ulong generation,
        AttachLiveState routingLive,
        UnixRawTerminal? tty)
    {
        if (client is null || string.IsNullOrWhiteSpace(endpointId))
            return;
        // Drain
        // render only while an activation is pending; otherwise ReadRenderAsync
        // presents the frame.
        if (live.PendingActivation is null)
            return;

        var assembler = live.ActivationSnapshotAssembler;
        var drained = client.DrainPendingAllEvents();
        if (drained.Count > 0)
            live.NoteActivationAdmit(endpointId, $"drain:{drained.Count}");
        foreach (var ev in drained)
        {
            // The socket reader already
            // accepted a shell chunk and did not queue it. A chunk that is
            // still here is returned to the render loop.
            if (IsShellLaneChunk(ev))
            {
                var bytes = Encoding.UTF8.GetByteCount(ev.GetRawText());
                if (!client.AdmitEvent(ev, bytes))
                    return;
                continue;
            }

            AdmitAttachEvent(routingLive, endpointId, generation, ev, tty, assembler);
        }
    }

    /// <summary>
    /// <c>complete_endpoint_activation</c> from that match.
    /// activation response chunk in the same match.
    /// The render read can take the queued presentation-sync event.
    /// Forward that event into this admit. Do not drop it.
    /// </summary>
    internal static bool TryForwardReadLoopActivationEvent(
        AttachLiveState live,
        ControlPlaneClient reader,
        JsonElement ev,
        UnixRawTerminal? tty)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(reader);
        if (live.PendingActivation is null || !IsPumpAdmissionEvent(ev))
            return false;
        if (!TryResolveDrainedClient(live, reader, out var endpointId, out var generation))
            return false;

        AdmitAttachEvent(live, endpointId, generation, ev, tty, live.ActivationSnapshotAssembler);
        AttachSession.ProcessActivationCompletionsForPump(live, tty);
        return true;
    }

    /// <summary>
    // The reader
    /// keeps the generation of its own connection.
    /// <see cref="AttachSession.LiveEndpointGeneration"/> is that label:
    /// pending target, pending outcome, source backup envelope, then
    /// <c>TransportEnvelope</c> for the connected placement.
    /// </summary>
    internal static bool TryResolveDrainedClient(
        AttachLiveState live,
        ControlPlaneClient reader,
        out string endpointId,
        out ulong generation)
    {
        generation = 0;
        if (!TryEndpointIdForReader(live, reader, out endpointId))
            return false;
        generation = AttachSession.LiveEndpointGeneration(live, endpointId);
        return true;
    }

    private static bool TryEndpointIdForReader(
        AttachLiveState live,
        ControlPlaneClient reader,
        out string endpointId)
    {
        var dest = live.PendingConnectOutcome?.DestClient;
        if (dest is not null
            && ReferenceEquals(reader, dest)
            && !ReferenceEquals(reader, live.ControlSlot?.Client))
        {
            endpointId = live.PendingActivation?.Target.EndpointId
                ?? live.PendingConnectCube?.Id
                ?? string.Empty;
            return endpointId.Length > 0;
        }

        if (live.SourceBackup is { SourceClient: { } source } backup
            && ReferenceEquals(reader, source)
            && !ReferenceEquals(reader, live.ControlSlot?.Client))
        {
            endpointId = backup.ConnectedPlacementId ?? string.Empty;
            return endpointId.Length > 0;
        }

        endpointId = live.ConnectedPlacementId ?? string.Empty;
        return endpointId.Length > 0;
    }

    private static bool IsPumpAdmissionEvent(JsonElement ev)
    {
        if (ev.ValueKind != JsonValueKind.Object
            || !ev.TryGetProperty("event", out var nameProp)
            || nameProp.GetString() is not { Length: > 0 } eventName)
            return false;

        if (IsActivationAdmissionName(eventName))
            return true;
        if (!string.Equals(eventName, ProtocolEventTypes.RuntimeEvent, StringComparison.Ordinal)
            || !ev.TryGetProperty("params", out var parms)
            || parms.ValueKind != JsonValueKind.Object
            || !parms.TryGetProperty("type", out var typeProp))
            return false;
        return IsActivationAdmissionName(typeProp.GetString());
    }

    private static bool IsActivationAdmissionName(string? eventName) =>
        string.Equals(eventName, AttachEndpointProtocol.PresentationSyncEvent, StringComparison.Ordinal)
        || string.Equals(eventName, AttachEndpointProtocol.PresentationReadyEvent, StringComparison.Ordinal)
        || string.Equals(eventName, AttachEndpointProtocol.ProjectionSnapshotEvent, StringComparison.Ordinal);

    private static bool IsShellLaneChunk(JsonElement ev) =>
        ev.ValueKind == JsonValueKind.Object
        && !ev.TryGetProperty("event", out _)
        && ev.TryGetProperty("id", out var id)
        && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;

    internal static void AdmitAttachEvent(
        AttachLiveState live,
        string endpointId,
        ulong generation,
        JsonElement ev,
        UnixRawTerminal? tty,
        SnapshotAssembler? assembler = null)
    {
        if (ev.ValueKind != JsonValueKind.Object
            || !ev.TryGetProperty("event", out var nameProp))
            return;

        var eventName = nameProp.GetString();
        if (string.IsNullOrWhiteSpace(eventName))
            return;
        live.NoteActivationAdmit(endpointId, eventName);

        if (!ev.TryGetProperty("params", out var paramsProp))
            return;

        // Live mux wraps pane paint as runtime.event. Flattened
        // terminal.render stays for unit fixtures.
        var evidence = paramsProp;
        if (string.Equals(eventName, ProtocolEventTypes.RuntimeEvent, StringComparison.Ordinal))
        {
            if (!paramsProp.TryGetProperty("type", out var typeProp)
                || typeProp.GetString() is not { Length: > 0 } innerType)
                return;
            eventName = innerType;
            live.NoteActivationAdmit(endpointId, innerType);
            if (!paramsProp.TryGetProperty("payload", out var payloadProp)
                || payloadProp.ValueKind != JsonValueKind.Object)
                return;
            evidence = payloadProp;
        }

        if (ShouldDropFrozenPresentationEvent(live, eventName))
            return;

        if (string.Equals(eventName, ProtocolEventTypes.TerminalRender, StringComparison.Ordinal))
        {
            // Bind capture after this event's surface identity is recorded.
            _ = TryAdmitTerminalRenderPayload(live, endpointId, generation, evidence, tty);
            AttachSession.TryCaptureActivationTerminalRender(
                live,
                endpointId,
                generation,
                evidence,
                assembler ?? live.ActivationSnapshotAssembler);
            if (live.PendingActivation is not null)
                AttachSession.RetryActivationCompleteAfterCapture(live, tty);
            return;
        }

        switch (eventName)
        {
            case AttachEndpointProtocol.PresentationSyncEvent:
                var sync = JsonSerializer.Deserialize(
                    evidence.GetRawText(),
                    ProtocolJsonContext.Default.AttachPresentationSync);
                if (sync is null)
                    return;
                AttachSession.EnqueueActivationCompletion(
                    live,
                    new EndpointActivationRpcCompletion.PresentationSync(endpointId, generation, sync.RequestId, sync));
                break;
            case AttachEndpointProtocol.PresentationReadyEvent:
                var ready = JsonSerializer.Deserialize(
                    evidence.GetRawText(),
                    ProtocolJsonContext.Default.AttachPresentationSync);
                if (ready is null)
                    return;
                AttachSession.EnqueueActivationCompletion(
                    live,
                    new EndpointActivationRpcCompletion.EffectsReady(endpointId, generation, ready.RequestId, ready));
                break;
            case AttachEndpointProtocol.WelcomeEvent:
                break;
            case AttachEndpointProtocol.ProjectionSnapshotEvent:
                if (TryParseSnapshotEvidence(evidence, out var projectionSnapshot))
                    AttachSession.OnEndpointSnapshot(live, endpointId, generation, projectionSnapshot, tty);
                if (TryParseSurfaceEvidence(evidence, out var projectionSurface))
                    AttachSession.OnEndpointSurface(live, endpointId, generation, projectionSurface, tty);
                break;
            default:
                if (TryParseSurfaceEvidence(evidence, out var surface))
                    AttachSession.OnEndpointSurface(live, endpointId, generation, surface, tty);
                else if (TryParseSnapshotEvidence(evidence, out var snapshot))
                    AttachSession.OnEndpointSnapshot(live, endpointId, generation, snapshot, tty);
                break;
        }
    }

    private static bool IsPresentationEffectEvent(string eventType) =>
        string.Equals(eventType, ProtocolEventTypes.ClientWindowTitleChanged, StringComparison.Ordinal)
        || string.Equals(eventType, ProtocolEventTypes.PaneBell, StringComparison.Ordinal)
        || string.Equals(eventType, ProtocolEventTypes.PaneScrollChanged, StringComparison.Ordinal)
        || string.Equals(eventType, ProtocolEventTypes.NotificationShown, StringComparison.Ordinal);

    private static bool IsFrozenChromeOrOverlayEvent(string eventType) =>
        string.Equals(eventType, ProtocolEventTypes.PopupLifecycle, StringComparison.Ordinal)
        || AttachSession.IsChromeRefreshEvent(eventType)
        || AttachSession.IsCommandOverlayLiveEvent(eventType)
        || string.Equals(eventType, ProtocolEventTypes.SessionLifecycle, StringComparison.Ordinal)
        || string.Equals(eventType, ProtocolEventTypes.OccupantLifecycle, StringComparison.Ordinal)
        || AttachSession.IsLiveNotifyEvent(eventType);

    private static bool TryParseSurfaceEvidence(JsonElement payload, out AttachSurfaceEvidence surface)
    {
        surface = null!;
        if (!payload.TryGetProperty("boot_id", out var bootProp)
            || bootProp.GetString() is not { Length: > 0 } bootId)
            return false;
        if (!payload.TryGetProperty("projection_revision", out var projectionProp)
            || !projectionProp.TryGetUInt64(out var projection))
            return false;
        if (!payload.TryGetProperty("surface_revision", out var surfaceProp)
            || !surfaceProp.TryGetUInt64(out var surfaceRevision))
            return false;
        if (!TryReadSurfaceGeometry(payload, "grid_cols", "columns", out var columns))
            return false;
        if (!TryReadSurfaceGeometry(payload, "grid_rows", "rows", out var rows))
            return false;

        payload.TryGetProperty("focused", out var focusedProp);
        surface = new AttachSurfaceEvidence
        {
            BootId = bootId,
            ProjectionRevision = projection,
            SurfaceRevision = surfaceRevision,
            Columns = columns,
            Rows = rows,
            Focused = focusedProp.ValueKind == JsonValueKind.True,
            FocusedPaneId = ReadOptionalString(payload, "focused_pane_id"),
        };
        return true;
    }

    private static bool TryParseSnapshotEvidence(JsonElement payload, out AttachSnapshotEvidence snapshot)
    {
        snapshot = null!;
        if (!payload.TryGetProperty("boot_id", out var bootProp)
            || bootProp.GetString() is not { Length: > 0 } bootId)
            return false;
        if (!payload.TryGetProperty("revision", out var revisionProp)
            && !payload.TryGetProperty("projection_revision", out revisionProp))
            return false;
        if (!revisionProp.TryGetUInt64(out var revision))
            return false;

        snapshot = new AttachSnapshotEvidence
        {
            BootId = bootId,
            Revision = revision,
            WorkspaceId = ReadOptionalString(payload, "workspace_id"),
            TabId = ReadOptionalString(payload, "tab_id"),
            PaneId = ReadOptionalString(payload, "pane_id"),
        };
        return true;
    }

    /// <summary>
    /// Live <c>terminal.render</c> cells use <c>rows</c> as the packed
    /// grid. Geometry is <c>grid_cols</c>/<c>grid_rows</c> or Hello
    /// <c>columns</c>.
    /// </summary>
    private static bool TryReadSurfaceGeometry(
        JsonElement payload,
        string primary,
        string fallback,
        out ushort value)
    {
        value = 0;
        if (payload.TryGetProperty(primary, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetUInt16(out value))
            return true;
        return payload.TryGetProperty(fallback, out prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetUInt16(out value);
    }

    private static string? ReadOptionalString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        return prop.GetString();
    }
}
