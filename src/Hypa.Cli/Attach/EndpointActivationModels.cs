using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Keys;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

public sealed record EndpointActivationLease
{
    public required string EndpointId { get; init; }
    public required ulong ConnectionGeneration { get; init; }
    public required string BootId { get; init; }
    public required ulong MinimumProjectionRevision { get; init; }
    public required string ClientId { get; init; }
    public required string LeaseId { get; init; }
    internal EndpointActivationRouting? Routing { get; init; }
}

/// Hypa routing identity carried on each lease after subscribe.
internal sealed record EndpointActivationRouting
{
    public string? ControlSub { get; init; }
    public string? RenderSub { get; init; }
    public string? AttachClientId { get; init; }
    public string? PaneId { get; init; }
    public string? InputLease { get; init; }
    public string? ResizeLease { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TabId { get; init; }
    public string? SessionName { get; init; }
    public string? ConnectedPlacementId { get; init; }
    public SidebarCubeKind? PlacementKind { get; init; }
    public string? PlacementDisplayName { get; init; }
    public bool ConnectedSshPlacement { get; init; }
    public bool RemoteDestination { get; init; }
    public string? ActiveMuxSocketPath { get; init; }
    public string? PaintedMuxLabel { get; init; }
}

internal static class EndpointActivationProjection
{
    internal static ulong ResolveMinimumRevision(JsonElement? snapshot, ulong? evidenceRevision = null)
    {
        if (TryParseSnapshotIdentity(snapshot, out _, out var revision))
            return revision;

        return evidenceRevision ?? 0;
    }

    internal static bool TryParseSnapshotIdentity(
        JsonElement? snapshot,
        out string bootId,
        out ulong revision)
    {
        bootId = string.Empty;
        revision = 0;
        if (snapshot is not { ValueKind: JsonValueKind.Object } element)
            return false;
        if (!element.TryGetProperty("boot_id", out var bootProp)
            || bootProp.GetString() is not { Length: > 0 } parsedBoot)
            return false;
        if (element.TryGetProperty("projection_revision", out var projection)
            && projection.TryGetUInt64(out revision))
        {
            bootId = parsedBoot;
            return true;
        }

        if (element.TryGetProperty("revision", out var snapshotRevision)
            && snapshotRevision.TryGetUInt64(out revision))
        {
            bootId = parsedBoot;
            return true;
        }

        return false;
    }

    internal static bool LiveEndpointSnapshotMatches(
        AttachLiveState live,
        EndpointActivationLease lease,
        AttachSurfaceEvidence surface)
    {
        if (!TryResolveLiveSnapshot(live, lease, out var snapshot))
            return false;

        if (!TryParseSnapshotIdentity(snapshot, out var bootId, out var revision))
            return false;

        if (!LiveSnapshotGenerationMatches(live, lease))
            return false;

        return string.Equals(bootId, lease.BootId, StringComparison.Ordinal)
            && string.Equals(bootId, surface.BootId, StringComparison.Ordinal)
            && revision == surface.ProjectionRevision;
    }

    internal static JsonElement SnapshotIdentityElement(AttachSnapshotEvidence snapshot)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("boot_id", snapshot.BootId);
            writer.WriteNumber("projection_revision", snapshot.Revision);
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    /// snapshot. The projection event updates boot and revision only.
    internal static JsonElement MergeSnapshotIdentity(JsonElement full, AttachSnapshotEvidence snapshot)
    {
        if (full.ValueKind != JsonValueKind.Object)
            return SnapshotIdentityElement(snapshot);

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in full.EnumerateObject())
            {
                if (property.NameEquals("boot_id")
                    || property.NameEquals("projection_revision")
                    || property.NameEquals("revision"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("boot_id", snapshot.BootId);
            writer.WriteNumber("projection_revision", snapshot.Revision);
            writer.WriteNumber("revision", snapshot.Revision);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static bool TryResolveLiveSnapshot(
        AttachLiveState live,
        EndpointActivationLease lease,
        out JsonElement snapshot)
    {
        snapshot = default;
        // After set_active, ConnectedPlacementId is that endpoint. The
        // pending dest snapshot still belongs to it. Do not read the
        // source LastSnapshot for that id.
        if (live.PendingConnectCube is { } cube
            && string.Equals(cube.Id, lease.EndpointId, StringComparison.Ordinal)
            && live.PendingTargetSessionSnapshot is { ValueKind: JsonValueKind.Object } dest)
        {
            snapshot = dest;
            return true;
        }

        if (IsSourceAttachLease(live, lease))
        {
            if (live.LastSnapshot is not { ValueKind: JsonValueKind.Object } last)
                return false;
            snapshot = last;
            return true;
        }

        return false;
    }

    private static bool LiveSnapshotGenerationMatches(AttachLiveState live, EndpointActivationLease lease)
    {
        if (live.PendingConnectCube is { } cube
            && string.Equals(cube.Id, lease.EndpointId, StringComparison.Ordinal)
            && live.PendingTargetSessionSnapshot is { ValueKind: JsonValueKind.Object })
        {
            return live.PendingTargetSessionSnapshotGeneration == lease.ConnectionGeneration;
        }

        if (IsSourceAttachLease(live, lease))
            return live.LastSnapshotConnectionGeneration == lease.ConnectionGeneration;

        return false;
    }

    // / ready for this generation.
    /// <c>endpoints.rs:458-464</c> binds the live snapshot to the
    /// current connection once. A later hello must not invent a new
    /// generation for the same live source.
    internal static bool TryResolveSourceSnapshotIdentity(
        AttachLiveState live,
        ulong connectionGeneration,
        string? sourceBootId,
        out ulong minimumRevision)
    {
        minimumRevision = 0;
        if (live.LastSnapshot is not { ValueKind: JsonValueKind.Object })
            return false;

        var parsed = TryParseSnapshotIdentity(live.LastSnapshot, out var bootId, out minimumRevision);
        if (live.LastSnapshotConnectionGeneration == connectionGeneration)
            return true;

        // First bind: local attach never stamped a generation. Adopt
        // the live session snapshot onto this source hello.
        if (live.LastSnapshotConnectionGeneration is null or 0)
        {
            if (parsed
                && !string.IsNullOrWhiteSpace(sourceBootId)
                && !string.Equals(bootId, sourceBootId, StringComparison.Ordinal))
            {
                return false;
            }

            live.LastSnapshotConnectionGeneration = connectionGeneration;
            return true;
        }

        return false;
    }

    private static bool IsSourceAttachLease(AttachLiveState live, EndpointActivationLease lease)
    {
        if (string.Equals(lease.EndpointId, "local", StringComparison.Ordinal))
            return true;
        return !string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            && string.Equals(live.ConnectedPlacementId, lease.EndpointId, StringComparison.Ordinal);
    }
}

internal sealed class EndpointActivationEvidence
{
    public ulong? SnapshotRevision { get; private set; }
    public string? SnapshotEndpointId { get; private set; }
    public ulong? SnapshotConnectionGeneration { get; private set; }
    public string? WorkspaceId { get; private set; }
    public string? TabId { get; private set; }
    public string? PaneId { get; private set; }
    public AttachSurfaceEvidence? Surface { get; private set; }

    public void RecordSnapshot(string endpointId, ulong generation, AttachSnapshotEvidence snapshot)
    {
        if (SnapshotRevision is null || snapshot.Revision >= SnapshotRevision)
        {
            SnapshotRevision = snapshot.Revision;
            SnapshotEndpointId = endpointId;
            SnapshotConnectionGeneration = generation;
            WorkspaceId = snapshot.WorkspaceId;
            TabId = snapshot.TabId;
            PaneId = snapshot.PaneId;
        }
    }

    public void RecordSurface(AttachSurfaceEvidence surface)
    {
        var replace = Surface is null
            || surface.ProjectionRevision > Surface.ProjectionRevision
            || (surface.ProjectionRevision == Surface.ProjectionRevision
                && surface.SurfaceRevision >= Surface.SurfaceRevision);
        if (replace)
            Surface = surface;
    }

    public void InvalidateSurface() => Surface = null;

    public bool HasCoherentSnapshotSurfacePair() =>
        SnapshotRevision is not null
        && Surface is not null
        && SnapshotRevision == Surface.ProjectionRevision;

    public bool EndpointSnapshotMatches(
        EndpointActivationLease lease,
        AttachSurfaceEvidence surface,
        AttachGeometry geometry) =>
        HasCoherentSnapshotSurfacePair()
        && SnapshotEndpointId is not null
        && SnapshotConnectionGeneration is not null
        && string.Equals(SnapshotEndpointId, lease.EndpointId, StringComparison.Ordinal)
        && SnapshotConnectionGeneration == lease.ConnectionGeneration
        && string.Equals(lease.BootId, surface.BootId, StringComparison.Ordinal)
        && SnapshotRevision == surface.ProjectionRevision
        && surface.Columns == geometry.Columns
        && surface.Rows == geometry.Rows;

    public AttachSurfaceEvidence? CoherentSurface(ulong minimumRevision, AttachGeometry geometry)
    {
        if (!HasCoherentSnapshotSurfacePair())
            return null;
        if (Surface!.ProjectionRevision < minimumRevision)
            return null;
        if (Surface.Columns != geometry.Columns || Surface.Rows != geometry.Rows)
            return null;
        return Surface;
    }
}

internal sealed record ActivationCaptureBinding(
    string EndpointId,
    ulong Generation,
    string BootId,
    ulong ProjectionRevision,
    ulong SurfaceRevision);

internal sealed record ActivationCapturedFrame(
    ActivationCaptureBinding Binding,
    AssembledSnapshot Frame);

internal sealed record AttachSnapshotEvidence
{
    public required string BootId { get; init; }
    public required ulong Revision { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TabId { get; init; }
    public string? PaneId { get; init; }
}

internal sealed record AttachSurfaceEvidence
{
    public required string BootId { get; init; }
    public required ulong ProjectionRevision { get; init; }
    public required ulong SurfaceRevision { get; init; }
    public required ushort Columns { get; init; }
    public required ushort Rows { get; init; }
    public bool Focused { get; init; }
    public string? FocusedPaneId { get; init; }
}

internal abstract record ActivationPhase
{
    internal sealed record ReleasingSource(string RequestId) : ActivationPhase;

    internal sealed record ActivatingTarget(
        string RequestId,
        ulong? AcknowledgedRevision,
        string? FocusRequestId,
        EndpointFocusTarget? FocusRequestTarget,
        bool FocusAcknowledged,
        EndpointActivationEvidence Evidence) : ActivationPhase;

    internal sealed record ReleasingTargetForRollback(string RequestId) : ActivationPhase;

    internal sealed record RestoringSource(
        string RequestId,
        ulong? AcknowledgedRevision,
        EndpointActivationEvidence Evidence) : ActivationPhase;

    internal sealed record SynchronizingPresentation(
        EndpointActivationLease Lease,
        string RequestId,
        ulong? AcknowledgedRevision,
        EndpointActivationEvidence Evidence,
        ActivationCompletion Completion) : ActivationPhase;

    internal sealed record AwaitingPresentationEffects(
        EndpointActivationLease Lease,
        string Token,
        bool Ready,
        ActivationCompletion Completion) : ActivationPhase;
}

internal enum EndpointFocusTargetKind
{
    Workspace,
    Tab,
    Pane,
}

internal sealed record EndpointFocusTarget(EndpointFocusTargetKind Kind, string Id);

internal abstract record SurfaceActivationProgress
{
    internal sealed record Pending : SurfaceActivationProgress;
    internal sealed record Ready : SurfaceActivationProgress;
    internal sealed record Rejected(string Message, bool SourceReleaseRejected) : SurfaceActivationProgress;
    internal sealed record Stale : SurfaceActivationProgress;
}

internal abstract record ActivationRollback
{
    internal sealed record Pending : ActivationRollback;
    internal sealed record Unavailable(string Message) : ActivationRollback;
}

internal abstract record ActivationCompletion
{
    internal sealed record AwaitingPresentationSync(string Previous, string Endpoint) : ActivationCompletion;
    internal sealed record AwaitingPresentationEffects : ActivationCompletion;
    internal sealed record Activated : ActivationCompletion;
    internal sealed record RestoredSource(string Error, EndpointActivationIntent? Successor) : ActivationCompletion;
}

internal abstract record ActivationBeginError
{
    internal sealed record Preflight(string Message) : ActivationBeginError;
    internal sealed record Partial(PendingEndpointActivation Activation, string Message) : ActivationBeginError;
}

public sealed record EndpointActivationIntent
{
    public required string EndpointId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TabId { get; init; }
    public string? PaneId { get; init; }
    internal CubesConnectStageClock? StageClock { get; init; }

    internal EndpointFocusTarget? FocusTarget()
    {
        if (!string.IsNullOrWhiteSpace(PaneId))
            return new EndpointFocusTarget(EndpointFocusTargetKind.Pane, PaneId);
        if (!string.IsNullOrWhiteSpace(TabId))
            return new EndpointFocusTarget(EndpointFocusTargetKind.Tab, TabId);
        if (!string.IsNullOrWhiteSpace(WorkspaceId))
            return new EndpointFocusTarget(EndpointFocusTargetKind.Workspace, WorkspaceId);
        return null;
    }
}

internal sealed record EndpointActivationBeginRequest
{
    public required string ClientId { get; init; }
    public required AttachGeometry Geometry { get; init; }
    public required EndpointActivationIntent Target { get; init; }
    public EndpointActivationLease? Source { get; init; }
    public bool SourceAvailable { get; init; }
    public bool HostFocused { get; init; }
    public ulong Epoch { get; init; }
    public required EndpointActivationLease TargetLease { get; init; }
}

internal sealed record EndpointActivationPreflightError(string Message)
{
    public string? Code { get; init; }
}

internal sealed record EndpointSurfaceControlResult
{
    public required bool Ok { get; init; }
    public ulong ProjectionRevision { get; init; }
    public string? LeaseId { get; init; }
    public string? BootId { get; init; }
    public string? RequestId { get; init; }
    public ulong ConnectionGeneration { get; init; }
    public string? ErrorMessage { get; init; }
    public bool HasErrorCode { get; init; }
}

internal abstract record EndpointActivationMessage
{
    internal sealed record FocusRevoke(string RequestId) : EndpointActivationMessage;
    internal sealed record SurfaceInterest(string RequestId, bool Active) : EndpointActivationMessage;
    internal sealed record HostFocusBaseline(bool Focused, string RequestId) : EndpointActivationMessage;
    internal sealed record Resize(AttachGeometry Geometry, string RequestId) : EndpointActivationMessage;
    internal sealed record NavigationFocus(string RequestId, EndpointFocusTarget Target) : EndpointActivationMessage;
    internal sealed record PresentationSync(string RequestId) : EndpointActivationMessage;
    internal sealed record PresentationEffectsFence(string Token) : EndpointActivationMessage;

    internal sealed record HostTheme(HostThemeSetParams Theme, string RequestId) : EndpointActivationMessage;
}

internal enum EndpointSendOutcome
{
    Sent,
    NotConnected,
    NotSent,
}

internal enum ClientEndpointStatus
{
    Online,
    Disabled,
    Reconnecting,
}

internal sealed record AttachRetargetPresentationBackup
{
    public ControlPlaneClient? SourceClient { get; init; }
    public required IAttachCommandPort SourcePort { get; init; }
    public string? ControlSub { get; init; }
    public string? RenderSub { get; init; }
    public string? AttachClientId { get; init; }
    public string? PaneId { get; init; }
    public string? InputLease { get; init; }
    public string? ResizeLease { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TabId { get; init; }
    public string? SessionName { get; init; }
    public string? ConnectedPlacementId { get; init; }
    public SidebarCubeKind? PlacementKind { get; init; }
    public string? PlacementDisplayName { get; init; }
    public bool ConnectedSshPlacement { get; init; }
    public bool RemoteDestination { get; init; }
    public string? ActiveMuxSocketPath { get; init; }
    public string? PaintedMuxLabel { get; init; }
}

internal sealed record EndpointConnectCandidate
{
    public required ControlPlaneClient DestClient { get; init; }
    public required IAttachEndpoint DestEndpoint { get; init; }
    public required AttachEndpointConnectPreflightResult Preflight { get; init; }
    public required EndpointActivationLease TargetLease { get; init; }
    public EndpointActivationLease? SourceLease { get; init; }
    public required string ClientId { get; init; }
    public required AttachGeometry Geometry { get; init; }
    public ulong SshConnectGeneration { get; init; }
}

/// in_flight plus generation. Dest prep completion is a pump event.
internal sealed class AttachDestConnectAttempt
{
    public required ulong Generation { get; init; }
    public required string EndpointId { get; init; }
    public required Task PrepTask { get; init; }
    public required CancellationTokenSource Cancel { get; init; }
}

internal sealed record AttachDestConnectCompletion
{
    public required ulong Generation { get; init; }
    public required SidebarCubeItem Cube { get; init; }
    public required EndpointActivationIntent Intent { get; init; }
    public required CubesConnectRequest Request { get; init; }
    public required AttachGeometry HostGeometry { get; init; }
    public required IAttachCommandPort Control { get; init; }
    public CancellationToken SessionCt { get; init; }
    public CubesConnectRetargetOutcome? Outcome { get; init; }
    public string? TimeoutError { get; init; }
    public bool Canceled { get; init; }
    public UnixRawTerminal? Tty { get; init; }
}
