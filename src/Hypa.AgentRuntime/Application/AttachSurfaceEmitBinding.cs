using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Frame identity bound at admission time. Rechecked at writer enqueue.
/// surface revision, and geometry on the same paint.
/// </summary>
public sealed record AttachSurfaceEmitBinding
{
    public required string ConnectionId { get; init; }
    public required string BootId { get; init; }
    public required ulong ConnectionGeneration { get; init; }
    public required string LeaseId { get; init; }
    public required ulong ProjectionRevision { get; init; }
    public required ulong SurfaceRevision { get; init; }
    public required ulong SnapshotRevision { get; init; }
    public required ulong GeometryRevision { get; init; }
    public required ushort Columns { get; init; }
    public required ushort Rows { get; init; }

    public bool Focused { get; init; }

    public string? FocusedPaneId { get; init; }

    public RuntimeEventRecord Stamp(RuntimeEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record with
        {
            AttachEmitSurfaceRevision = SurfaceRevision,
            AttachEmitSnapshotRevision = SnapshotRevision,
            AttachEmitBootId = BootId,
            AttachEmitProjectionRevision = ProjectionRevision,
            AttachEmitColumns = Columns,
            AttachEmitRows = Rows,
            AttachEmitFocused = Focused,
            AttachEmitFocusedPaneId = FocusedPaneId,
        };
    }
}
