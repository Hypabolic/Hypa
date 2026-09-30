using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

public sealed record AttachSurfaceInterestError(string Code, string Message)
{
    public static AttachSurfaceInterestError InvalidConnection { get; } =
        new("invalid_connection", "connection is required");

    public static AttachSurfaceInterestError ClientMismatch { get; } =
        new("client_mismatch", "client_id does not match this connection");

    public static AttachSurfaceInterestError StaleGeometry { get; } =
        new("stale_geometry", "geometry_revision is stale");

    public static AttachSurfaceInterestError StaleSurface { get; } =
        new("stale_surface", "surface does not meet projection floor");

    public static AttachSurfaceInterestError SurfaceInactive { get; } =
        new(AttachEndpointErrorCodes.SurfaceInactive, "surface is inactive");
}

public sealed record AttachSurfaceInterestSnapshot
{
    public required string ConnectionId { get; init; }
    public required string ClientId { get; init; }
    public required ulong ConnectionGeneration { get; init; }
    public required string BootId { get; init; }
    public required bool SurfaceActive { get; init; }
    public required ulong ProjectionRevision { get; init; }
    public required ulong GeometryRevision { get; init; }
    public required ushort Columns { get; init; }
    public required ushort Rows { get; init; }
    public string? LeaseId { get; init; }
    public ulong? CachedSnapshotRevision { get; init; }
}

public sealed record AttachSurfaceInterestApplyRequest
{
    public required string ConnectionId { get; init; }
    public required string ClientId { get; init; }
    public required bool Active { get; init; }
    public required ulong GeometryRevision { get; init; }
    public AttachGeometry? Geometry { get; init; }
}

public sealed record AttachSurfaceInterestApplyResult
{
    public required bool Changed { get; init; }
    public required ulong ProjectionRevision { get; init; }
    public required ulong GeometryRevision { get; init; }
    public required string LeaseId { get; init; }
    public required string BootId { get; init; }
}

public sealed record AttachEndpointHelloApplyRequest
{
    public required string ConnectionId { get; init; }
    public required AttachEndpointHello Hello { get; init; }
    public required string EndpointId { get; init; }
}

public sealed record AttachEndpointHelloApplyResult
{
    public required AttachEndpointWelcome Welcome { get; init; }
    public required ulong ConnectionGeneration { get; init; }
}

public sealed record AttachActivationAdmissionRequest
{
    public required string ConnectionId { get; init; }
    public required ulong ProjectionRevision { get; init; }
    public required ulong SurfaceRevision { get; init; }
    public required ulong GeometryRevision { get; init; }
    public required string BootId { get; init; }
    public required string LeaseId { get; init; }
    public ulong? SnapshotRevision { get; init; }
}
