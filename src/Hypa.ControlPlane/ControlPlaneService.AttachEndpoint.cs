using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal Task<JsonElement> HandleAttachHelloAsync(
        AttachEndpointHello p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        _ = ct;
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach hello requires a live connection");

        var endpointId = ResolveAttachEndpointId();
        var applied = _attachSurfaceInterest.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = connection.ConnectionId,
            Hello = p,
            EndpointId = endpointId,
        });

        if (!applied.IsOk)
            throw AttachEndpointFailure(applied.Error);

        _ = _attachClientViews.Bind(
            new AttachClientViewBindRequest
            {
                ConnectionId = connection.ConnectionId,
                ClientId = p.ClientId,
                EndpointId = endpointId,
                BootId = _attachSurfaceInterest.BootId,
                ConnectionGeneration = applied.Value.ConnectionGeneration,
            },
            CurrentAttachTopology());

        return Task.FromResult(OkTyped(applied.Value.Welcome, ProtocolJsonContext.Default.AttachEndpointWelcome));
    }

    internal async Task<JsonElement> HandleAttachResizeAsync(
        AttachResizeRequest p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach resize requires a live connection");

        EnsureAttachIngress(connection, p.ClientId);
        var applied = _attachSurfaceInterest.ApplyResize(connection.ConnectionId, p.ClientId, p.Geometry);
        if (!applied.IsOk)
            throw AttachEndpointFailure(applied.Error);

        var (projection, geometry) = applied.Value;
        await ResizeShellTabIfControllerAsync(connection, ct).ConfigureAwait(false);
        var result = new AttachControlResult
        {
            RequestId = p.RequestId,
            BootId = _attachSurfaceInterest.BootId,
            ProjectionRevision = projection,
            GeometryRevision = geometry,
        };
        return OkTyped(result, ProtocolJsonContext.Default.AttachControlResult);
    }

    internal async Task<JsonElement> HandleAttachSurfaceInterestAsync(
        AttachSurfaceInterestRequest p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "attach surface_interest requires a live connection");

        EnsureAttachIngress(connection, p.ClientId);
        AttachSurfaceInterestApplyResult applied;
        if (!p.Active)
        {
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                applied = _attachSurfaceInterest.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
                {
                    ConnectionId = connection.ConnectionId,
                    ClientId = p.ClientId,
                    Active = false,
                    GeometryRevision = p.GeometryRevision,
                }) switch
                {
                    { IsOk: true, Value: var value } => value,
                    { IsOk: false, Error: var error } => throw AttachEndpointFailure(error),
                };
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            if (applied.Changed)
            {
                ReleaseAttachSurfacePresentation(connection);
                _ = _visibleSets.Publish(new VisibleSetPublishRequest
                {
                    ConnectionId = connection.ConnectionId,
                    PaneIds = [],
                });
            }

            // ownership, then reapplies remaining controllers.
            ReapplyShellTabGeometryAfterDisconnect(connection.ConnectionId);
        }
        else
        {
            var result = _attachSurfaceInterest.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
            {
                ConnectionId = connection.ConnectionId,
                ClientId = p.ClientId,
                Active = true,
                GeometryRevision = p.GeometryRevision,
            });
            if (!result.IsOk)
                throw AttachEndpointFailure(result.Error);
            applied = result.Value;
            // render.rs:423-464 emit one per-viewer snapshot after the
            // new projection floor. Emit that snapshot before the first
            // render so the client can bind cells to the revision.
            RestoreAttachSurfaceVisibleSet(connection);
            await EmitAttachProjectionSnapshotAsync(connection, applied, ct)
                .ConfigureAwait(false);
            RequestAttachSurfaceRepaint(connection);
            await ClaimUnownedShellTabGeometryAsync(connection, ct).ConfigureAwait(false);
        }

        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        var response = new AttachSurfaceInterestResult
        {
            RequestId = p.RequestId,
            Active = p.Active,
            LeaseId = applied.LeaseId,
            BootId = applied.BootId,
            MinimumProjectionRevision = applied.ProjectionRevision,
            GeometryRevision = applied.GeometryRevision,
            ConnectionGeneration = snapshot?.ConnectionGeneration ?? 0,
        };
        return OkTyped(response, ProtocolJsonContext.Default.AttachSurfaceInterestResult);
    }

    internal async Task<JsonElement> HandleAttachFocusAsync(
        AttachFocusRequest p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach focus requires a live connection");

        EnsureAttachIngress(connection, p.ClientId);
        if (p.Focused)
        {
            var admit = _attachSurfaceInterest.AdmitPaneMutation(connection.ConnectionId);
            if (!admit.IsOk)
                throw AttachEndpointFailure(admit.Error);
        }

        var applied = _attachSurfaceInterest.ApplyFocus(connection.ConnectionId, p.ClientId);
        if (!applied.IsOk)
            throw AttachEndpointFailure(applied.Error);

        var (projection, geometry) = applied.Value;
        if (p.Focused)
        {
            var interest = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId)
                ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "attach connection is unknown");
            var view = _attachClientViews.ApplyFocus(
                new AttachClientViewFocusRequest
                {
                    ConnectionId = connection.ConnectionId,
                    ClientId = p.ClientId,
                    ConnectionGeneration = interest.ConnectionGeneration,
                    WorkspaceId = p.WorkspaceId,
                    TabId = p.TabId,
                    PaneId = p.PaneId,
                },
                CurrentAttachTopology());
            if (!view.IsOk)
                throw AttachClientViewFailure(view.Error);
            if (view.Value.Changed)
            {
                var consumed = _attachSurfaceInterest.ConsumeProjectionFloor(
                    connection.ConnectionId,
                    p.ClientId);
                if (!consumed.IsOk)
                    throw AttachEndpointFailure(consumed.Error);
                (projection, geometry) = consumed.Value;
            }

            await ClaimShellTabGeometryOnInteractAsync(connection, ct).ConfigureAwait(false);
        }

        var result = new AttachControlResult
        {
            RequestId = p.RequestId,
            BootId = _attachSurfaceInterest.BootId,
            ProjectionRevision = projection,
            GeometryRevision = geometry,
        };
        return OkTyped(result, ProtocolJsonContext.Default.AttachControlResult);
    }

    internal Task<JsonElement> HandleAttachHealthAsync(
        AttachHealthRequest p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        _ = ct;
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach health requires a live connection");

        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "attach connection is unknown");
        EnsureAttachIngress(connection, snapshot.ClientId);
        if (!string.Equals(snapshot.BootId, _attachSurfaceInterest.BootId, StringComparison.Ordinal))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "health boot_id mismatch");
        }

        var result = new AttachHealthResult
        {
            RequestId = p.RequestId,
            BootId = _attachSurfaceInterest.BootId,
        };
        return Task.FromResult(OkTyped(result, ProtocolJsonContext.Default.AttachHealthResult));
    }

    internal Task<JsonElement> HandleAttachPresentationSyncAsync(
        AttachPresentationSyncRequest p,
        IClientConnection? connection,
        CancellationToken ct) =>
        EmitAttachPresentationEventAsync(
            connection,
            p.ClientId,
            p.RequestId,
            AttachEndpointProtocol.PresentationSyncEvent,
            ct);

    internal Task<JsonElement> HandleAttachPresentationReadyAsync(
        AttachPresentationSyncRequest p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        _ = ct;
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach presentation_ready requires a live connection");

        EnsureAttachIngress(connection, p.ClientId);
        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "attach connection is unknown");
        if (!snapshot.SurfaceActive || string.IsNullOrWhiteSpace(snapshot.LeaseId))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "surface is inactive for presentation_ready");
        }

        if (string.IsNullOrWhiteSpace(p.LeaseId)
            || !string.Equals(p.LeaseId, snapshot.LeaseId, StringComparison.Ordinal))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "presentation_ready lease_id mismatch");
        }

        if (string.IsNullOrWhiteSpace(p.BootId)
            || !string.Equals(p.BootId, snapshot.BootId, StringComparison.Ordinal))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "presentation_ready boot_id mismatch");
        }

        if (p.ProjectionRevision is not { } projection
            || projection < snapshot.ProjectionRevision)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "presentation_ready projection_revision is stale");
        }

        if (p.SurfaceRevision is not { } surface || surface < snapshot.ProjectionRevision)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "presentation_ready surface_revision is stale");
        }

        return EmitAttachPresentationEventAsync(
            connection,
            p.ClientId,
            p.RequestId,
            AttachEndpointProtocol.PresentationReadyEvent,
            ct);
    }

    internal bool TryAdmitAttachPaneMutation(IClientConnection? connection)
    {
        if (connection is null)
            return true;

        // Admit pane mutations when this connection has no snapshot yet.
        // After attach hello, local attach and Cubes Connect need SurfaceActive.
        if (_attachSurfaceInterest.GetSnapshot(connection.ConnectionId) is null)
            return true;

        var admit = _attachSurfaceInterest.AdmitPaneMutation(connection.ConnectionId);
        return admit.IsOk;
    }

    internal void EnsureAttachPaneMutation(IClientConnection? connection, string operation)
    {
        if (connection is null)
            return;

        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        if (snapshot is null)
            return;

        if (!snapshot.SurfaceActive)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                $"surface is inactive for {operation}");
        }

        var ingress = _attachSurfaceInterest.ValidateIngress(
            connection.ConnectionId,
            snapshot.ClientId,
            snapshot.ConnectionGeneration);
        if (!ingress.IsOk)
            throw AttachEndpointFailure(ingress.Error);

        if (!TryAdmitAttachPaneMutation(connection))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                $"surface is inactive for {operation}");
        }
    }

    internal bool TryAdmitAttachSurfaceRender(
        string connectionId,
        ulong surfaceRevision,
        ulong snapshotRevision,
        out AttachSurfaceEmitBinding binding)
    {
        binding = null!;
        var interest = _attachSurfaceInterest.GetSnapshot(connectionId);
        // Local attach has no endpoint snapshot. Emit unbound, as before the
        // surface gate. Cubes Connect hellos and must keep the lease.
        if (interest is null)
            return true;

        if (!interest.SurfaceActive || string.IsNullOrWhiteSpace(interest.LeaseId))
            return false;

        // shell_projection_revision. AdmitSurface compares SnapshotRevision to
        // that floor. The caller snapshotRevision is PTY feedGeneration.
        _ = snapshotRevision;
        var projectionSnapshot = interest.ProjectionRevision;
        var admit = _attachSurfaceInterest.AdmitSurface(new AttachActivationAdmissionRequest
        {
            ConnectionId = connectionId,
            ProjectionRevision = interest.ProjectionRevision,
            SurfaceRevision = surfaceRevision,
            GeometryRevision = interest.GeometryRevision,
            BootId = interest.BootId,
            LeaseId = interest.LeaseId,
            SnapshotRevision = projectionSnapshot,
        });
        if (!admit.IsOk)
            return false;

        binding = new AttachSurfaceEmitBinding
        {
            ConnectionId = connectionId,
            BootId = interest.BootId,
            ConnectionGeneration = interest.ConnectionGeneration,
            LeaseId = interest.LeaseId,
            ProjectionRevision = interest.ProjectionRevision,
            SurfaceRevision = surfaceRevision,
            SnapshotRevision = projectionSnapshot,
            GeometryRevision = interest.GeometryRevision,
            Columns = interest.Columns,
            Rows = interest.Rows,
            Focused = TryResolveAttachProjectionFocus(out _, out _, out var focusedPane)
                && focusedPane is { Length: > 0 },
            FocusedPaneId = focusedPane,
        };
        return true;
    }

    internal bool RecheckAttachSurfaceEmit(in AttachSurfaceEmitBinding binding)
    {
        var snapshot = _attachSurfaceInterest.GetSnapshot(binding.ConnectionId);
        if (snapshot is null)
            return false;

        var ingress = _attachSurfaceInterest.ValidateIngress(
            binding.ConnectionId,
            snapshot.ClientId,
            binding.ConnectionGeneration);
        if (!ingress.IsOk)
            return false;

        var admit = _attachSurfaceInterest.AdmitSurface(new AttachActivationAdmissionRequest
        {
            ConnectionId = binding.ConnectionId,
            ProjectionRevision = binding.ProjectionRevision,
            SurfaceRevision = binding.SurfaceRevision,
            GeometryRevision = binding.GeometryRevision,
            BootId = binding.BootId,
            LeaseId = binding.LeaseId,
            SnapshotRevision = binding.SnapshotRevision,
        });
        return admit.IsOk;
    }

    private void EnsureAttachIngress(IClientConnection connection, string clientId)
    {
        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId)
            ?? throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "attach connection is unknown");

        var ingress = _attachSurfaceInterest.ValidateIngress(
            connection.ConnectionId,
            clientId,
            snapshot.ConnectionGeneration);
        if (!ingress.IsOk)
            throw AttachEndpointFailure(ingress.Error);
    }

    private async Task<JsonElement> EmitAttachPresentationEventAsync(
        IClientConnection? connection,
        string clientId,
        string requestId,
        string eventName,
        CancellationToken ct)
    {
        if (connection is null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "attach presentation requires a live connection");

        EnsureAttachIngress(connection, clientId);
        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        if (snapshot is null || !snapshot.SurfaceActive || string.IsNullOrWhiteSpace(snapshot.LeaseId))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "surface is inactive for presentation synchronization");
        }

        var payload = new AttachPresentationSync
        {
            RequestId = requestId,
            LeaseId = snapshot.LeaseId,
            BootId = snapshot.BootId,
            ProjectionRevision = snapshot.ProjectionRevision,
            SurfaceRevision = snapshot.ProjectionRevision,
        };
        var line = new JsonObject
        {
            ["event"] = eventName,
            ["params"] = JsonSerializer.SerializeToNode(payload, ProtocolJsonContext.Default.AttachPresentationSync),
        };
        await connection.WriteLineAsync(line.ToJsonString(), ct).ConfigureAwait(false);
        return Ok(new JsonObject { ["ok"] = true });
    }

    private void ReleaseAttachSurfacePresentation(IClientConnection connection)
    {
        connection.DiscardPendingRender();
        if (_subscriptions is null)
            return;

        foreach (var sub in _subscriptions.ListForConnection(connection.ConnectionId))
        {
            _ = sub.TakeDeferredFrames();
            _ = sub.TakeDeferredJsonFulls();
            foreach (var attachment in sub.ListAttachments())
                ForgetPostedFull(attachment.PaneId);
        }
    }

    private void RestoreAttachSurfaceVisibleSet(IClientConnection connection)
    {
        var paneIds = _visibleSets.RetainedPaneIds(connection.ConnectionId);
        if (paneIds.Count == 0
            && TryResolveAttachProjectionFocus(out _, out _, out var focusedPane)
            && focusedPane is { Length: > 0 })
        {
            paneIds = [focusedPane];
        }

        if (paneIds.Count == 0)
            return;
        _ = _visibleSets.Publish(new VisibleSetPublishRequest
        {
            ConnectionId = connection.ConnectionId,
            PaneIds = paneIds,
        });
    }

    private async Task EmitAttachProjectionSnapshotAsync(
        IClientConnection connection,
        AttachSurfaceInterestApplyResult applied,
        CancellationToken ct)
    {
        TryResolveAttachProjectionFocus(out var workspaceId, out var tabId, out var paneId);
        var snapshot = _attachSurfaceInterest.GetSnapshot(connection.ConnectionId);
        var columns = snapshot?.Columns ?? 0;
        var rows = snapshot?.Rows ?? 0;
        if (columns < 1 || rows < 1)
            return;
        var payload = new AttachProjectionSnapshot
        {
            BootId = applied.BootId,
            Revision = applied.ProjectionRevision,
            ProjectionRevision = applied.ProjectionRevision,
            SurfaceRevision = applied.ProjectionRevision,
            Columns = columns,
            Rows = rows,
            WorkspaceId = workspaceId,
            TabId = tabId,
            PaneId = paneId,
            Focused = paneId is { Length: > 0 },
            FocusedPaneId = paneId,
        };
        var line = new JsonObject
        {
            ["event"] = AttachEndpointProtocol.ProjectionSnapshotEvent,
            ["params"] = JsonSerializer.SerializeToNode(
                payload,
                ProtocolJsonContext.Default.AttachProjectionSnapshot),
        };
        await connection.WriteLineAsync(line.ToJsonString(), ct).ConfigureAwait(false);
    }

    private bool TryResolveAttachProjectionFocus(
        out string? workspaceId,
        out string? tabId,
        out string? paneId)
    {
        workspaceId = null;
        tabId = null;
        paneId = null;
        var topology = CurrentAttachTopology();
        workspaceId = topology.FocusedWorkspaceId ?? topology.FallbackWorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;
        if (topology.ActiveTabIds.TryGetValue(workspaceId, out var activeTab)
            && !string.IsNullOrWhiteSpace(activeTab))
        {
            tabId = activeTab;
        }

        if (tabId is { Length: > 0 }
            && topology.FocusedPaneIds.TryGetValue(tabId, out var focusedPane)
            && !string.IsNullOrWhiteSpace(focusedPane))
        {
            paneId = focusedPane;
            return true;
        }

        if (tabId is { Length: > 0 }
            && topology.FallbackPaneIds.TryGetValue(tabId, out var fallbackPane)
            && !string.IsNullOrWhiteSpace(fallbackPane))
        {
            paneId = fallbackPane;
            return true;
        }

        paneId = FocusedPaneIdOrNull();
        return paneId is { Length: > 0 };
    }

    /// pane will not emit unless the coalescer is armed.
    private void RequestAttachSurfaceRepaint(IClientConnection connection)
    {
        var paneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paneId in _visibleSets.RetainedPaneIds(connection.ConnectionId))
            paneIds.Add(paneId);

        if (_subscriptions is not null)
        {
            foreach (var sub in _subscriptions.ListForConnection(connection.ConnectionId))
            {
                foreach (var attachment in sub.ListAttachments())
                    paneIds.Add(attachment.PaneId);
            }
        }

        if (paneIds.Count == 0
            && TryResolveAttachProjectionFocus(out _, out _, out var focusedPane)
            && focusedPane is { Length: > 0 })
            paneIds.Add(focusedPane);

        foreach (var paneId in paneIds)
        {
            if (_subscriptions is not null)
            {
                foreach (var sub in _subscriptions.ListForConnection(connection.ConnectionId))
                    sub.MarkReanchorPending(paneId);
            }

            _renderCoalescer.RequestOriginPaint(paneId);
        }
    }

    internal bool TryPeekCoalescedPaint(string paneId, out bool forceFull) =>
        _renderCoalescer.TryPeekPending(paneId, out forceFull);

    internal IAttachSurfaceInterestPublication AttachSurfaceInterest => _attachSurfaceInterest;

    private string ResolveAttachEndpointId() => _state.Snapshot().Id.Value;

    private bool ShouldEmitAttachQueuedRecord(IClientConnection connection, RuntimeEventRecord rec)
    {
        if (rec.AttachEmitSurfaceRevision is not { } surfaceRevision
            || rec.AttachEmitSnapshotRevision is not { } snapshotRevision)
            return true;

        if (!TryAdmitAttachSurfaceRender(
                connection.ConnectionId,
                surfaceRevision,
                snapshotRevision,
                out var binding))
            return false;

        return RecheckAttachSurfaceEmit(binding);
    }

    private bool TryPostAttachLiveBatch(
        IReadOnlyList<EventSubscription> observers,
        IReadOnlyList<RuntimeEventRecord> records,
        string paneId,
        string identity,
        bool attachPath,
        long surfaceGeneration,
        long feedGeneration)
    {
        if (!attachPath)
            return _subscriptions!.PostLiveBatch(records, paneId, identity);

        var stamped = new RuntimeEventRecord[records.Count];
        AttachSurfaceEmitBinding? emitBinding = null;
        foreach (var sub in observers)
        {
            if (!TryAdmitAttachSurfaceRender(
                    sub.ConnectionId,
                    (ulong)surfaceGeneration,
                    (ulong)feedGeneration,
                    out var binding))
                return false;
            if (binding is not null && !RecheckAttachSurfaceEmit(binding))
                return false;
            emitBinding ??= binding;
        }

        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            stamped[i] = emitBinding is null
                ? rec with
                {
                    AttachEmitSurfaceRevision = (ulong)surfaceGeneration,
                    AttachEmitSnapshotRevision = (ulong)feedGeneration,
                }
                : emitBinding.Stamp(rec);
        }

        return _subscriptions!.PostLiveBatch(stamped, paneId, identity);
    }

    private static ControlPlaneException AttachClientViewFailure(AttachClientViewError error) =>
        new(
            string.Equals(error.Code, "stale_generation", StringComparison.Ordinal)
                ? ProtocolErrorCodes.InvalidState
                : ProtocolErrorCodes.InvalidParams,
            error.Message);

    private static ControlPlaneException AttachEndpointFailure(AttachSurfaceInterestError error) =>
        new(
            string.Equals(error.Code, AttachEndpointErrorCodes.Incompatible, StringComparison.Ordinal)
                ? ProtocolErrorCodes.InvalidParams
                : ProtocolErrorCodes.InvalidState,
            error.Message);
}
