using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Authoritative visible-set publish from one live attach client.
/// </summary>
public sealed record VisibleSetPublishRequest
{
    public required string ConnectionId { get; init; }

    public required IReadOnlyList<string> PaneIds { get; init; }

    public string? OverlayPaneId { get; init; }
}

/// <summary>
/// Committed publication state after a mutation. This is not a result wrapper.
/// </summary>
public sealed record VisibleSetSnapshot
{
    public required IReadOnlyList<string> UnionPaneIds { get; init; }

    public required IReadOnlyList<string> RevealedPaneIds { get; init; }

    public required int LiveClientCount { get; init; }

    public required bool FailOpen { get; init; }
}

/// <summary>Expected publication failure. Use with <see cref="Result{T,E}"/>.</summary>
public sealed record VisibleSetError(string Code, string Message)
{
    public const string InvalidConnectionCode = "invalid_connection";
    public const string InvalidPaneIdCode = "invalid_pane_id";

    public static VisibleSetError InvalidConnection { get; } =
        new(InvalidConnectionCode, "connection_id is required");

    public static VisibleSetError InvalidPaneId { get; } =
        new(InvalidPaneIdCode, "pane_id must be a non-empty string");
}

/// <summary>Live-paint capture gate. Attach path always captures.</summary>
public readonly record struct CaptureAdmission(CaptureAdmissionKind Kind, long Generation)
{
    public static CaptureAdmission Skip { get; } = new(CaptureAdmissionKind.Skip, 0);

    public static CaptureAdmission For(CaptureAdmissionKind kind, long generation) =>
        new(kind, generation);

    public bool ShouldCapture => Kind != CaptureAdmissionKind.Skip;

    public bool RequiresFull => Kind == CaptureAdmissionKind.CaptureFull;
}

public enum CaptureAdmissionKind
{
    Skip = 0,
    Capture = 1,
    CaptureFull = 2,
}
