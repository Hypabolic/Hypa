using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record AttachEndpointIdentity
{
    [JsonPropertyName("endpoint_id")]
    public required string EndpointId { get; init; }

    [JsonPropertyName("connection_generation")]
    public required ulong ConnectionGeneration { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }
}

public sealed record AttachGeometry
{
    [JsonPropertyName("columns")]
    public required ushort Columns { get; init; }

    [JsonPropertyName("rows")]
    public required ushort Rows { get; init; }

    [JsonPropertyName("cell_width_px")]
    public required uint CellWidthPx { get; init; }

    [JsonPropertyName("cell_height_px")]
    public required uint CellHeightPx { get; init; }

    [JsonPropertyName("geometry_revision")]
    public required ulong GeometryRevision { get; init; }
}

public sealed record AttachEndpointHello
{
    [JsonPropertyName("endpoint_generation")]
    public required uint EndpointGeneration { get; init; }

    [JsonPropertyName("protocol_major")]
    public required uint ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public required uint ProtocolMinor { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("geometry")]
    public required AttachGeometry Geometry { get; init; }

    [JsonPropertyName("surface_active")]
    public required bool SurfaceActive { get; init; }

    [JsonPropertyName("snapshot_codecs")]
    public required string[] SnapshotCodecs { get; init; }

    [JsonPropertyName("surface_codecs")]
    public required string[] SurfaceCodecs { get; init; }

    [JsonPropertyName("input_codecs")]
    public required string[] InputCodecs { get; init; }

    [JsonPropertyName("required_capabilities")]
    public required string[] RequiredCapabilities { get; init; }
}

public sealed record AttachEndpointError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record AttachEndpointWelcome
{
    [JsonPropertyName("endpoint_generation")]
    public required uint EndpointGeneration { get; init; }

    [JsonPropertyName("protocol_major")]
    public required uint ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public required uint ProtocolMinor { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("mux_identity")]
    public required string MuxIdentity { get; init; }

    [JsonPropertyName("snapshot_codec")]
    public required string SnapshotCodec { get; init; }

    [JsonPropertyName("surface_codec")]
    public required string SurfaceCodec { get; init; }

    [JsonPropertyName("input_codec")]
    public required string InputCodec { get; init; }

    [JsonPropertyName("methods")]
    public required string[] Methods { get; init; }

    [JsonPropertyName("capabilities")]
    public required string[] Capabilities { get; init; }

    [JsonPropertyName("connection_generation")]
    public ulong? ConnectionGeneration { get; init; }

    [JsonPropertyName("error")]
    public AttachEndpointError? Error { get; init; }
}

public sealed record AttachEndpointLease
{
    [JsonPropertyName("identity")]
    public required AttachEndpointIdentity Identity { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("minimum_projection_revision")]
    public required ulong MinimumProjectionRevision { get; init; }

    [JsonPropertyName("geometry")]
    public required AttachGeometry Geometry { get; init; }
}

public sealed record AttachSurfaceInterestRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("active")]
    public required bool Active { get; init; }

    [JsonPropertyName("geometry_revision")]
    public required ulong GeometryRevision { get; init; }
}

public sealed record AttachSurfaceInterestResult
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("active")]
    public required bool Active { get; init; }

    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("minimum_projection_revision")]
    public required ulong MinimumProjectionRevision { get; init; }

    [JsonPropertyName("geometry_revision")]
    public required ulong GeometryRevision { get; init; }

    [JsonPropertyName("connection_generation")]
    public required ulong ConnectionGeneration { get; init; }
}

public sealed record AttachResizeRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("geometry")]
    public required AttachGeometry Geometry { get; init; }
}

public sealed record AttachFocusRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("focused")]
    public required bool Focused { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record AttachControlResult
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("projection_revision")]
    public required ulong ProjectionRevision { get; init; }

    [JsonPropertyName("geometry_revision")]
    public required ulong GeometryRevision { get; init; }

    [JsonPropertyName("error")]
    public AttachEndpointError? Error { get; init; }
}

public sealed record AttachHealthRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }
}

public sealed record AttachHealthResult
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }
}

public sealed record AttachPresentationSyncRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("boot_id")]
    public string? BootId { get; init; }

    [JsonPropertyName("projection_revision")]
    public ulong? ProjectionRevision { get; init; }

    [JsonPropertyName("surface_revision")]
    public ulong? SurfaceRevision { get; init; }
}

/// <summary>
// / Per-viewer projection floor.
/// <c>src/protocol/endpoint.rs</c> <c>snapshot_message</c>.
/// </summary>
public sealed record AttachProjectionSnapshot
{
    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("revision")]
    public required ulong Revision { get; init; }

    [JsonPropertyName("projection_revision")]
    public required ulong ProjectionRevision { get; init; }

    [JsonPropertyName("surface_revision")]
    public required ulong SurfaceRevision { get; init; }

    [JsonPropertyName("columns")]
    public required ushort Columns { get; init; }

    [JsonPropertyName("rows")]
    public required ushort Rows { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }

    [JsonPropertyName("focused_pane_id")]
    public string? FocusedPaneId { get; init; }
}

public sealed record AttachPresentationSync
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("projection_revision")]
    public required ulong ProjectionRevision { get; init; }

    [JsonPropertyName("surface_revision")]
    public required ulong SurfaceRevision { get; init; }
}

public sealed record AttachPresentationReady
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("lease_id")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("boot_id")]
    public required string BootId { get; init; }

    [JsonPropertyName("projection_revision")]
    public required ulong ProjectionRevision { get; init; }

    [JsonPropertyName("surface_revision")]
    public required ulong SurfaceRevision { get; init; }
}

public sealed record AttachDisplayEnumSample
{
    [JsonPropertyName("display")]
    public string? Display { get; init; }
}

public sealed record AttachActivationEvidence
{
    [JsonPropertyName("lease")]
    public required AttachEndpointLease Lease { get; init; }

    [JsonPropertyName("snapshot_revision")]
    public ulong? SnapshotRevision { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("surface_revision")]
    public ulong? SurfaceRevision { get; init; }

    [JsonPropertyName("geometry_revision")]
    public required ulong GeometryRevision { get; init; }
}
