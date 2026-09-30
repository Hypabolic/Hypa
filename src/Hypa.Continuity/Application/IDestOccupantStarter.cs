using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Starts a harness occupant on the destination mux after probes pass.
/// Implemented by Placement (protocol client). Continuity does not speak NDJSON.
/// </summary>
public interface IDestOccupantStarter
{
    ValueTask<DestOccupantStartResult> StartResumeAsync(
        DestOccupantStartRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Live dest occupant process after <c>agent.start</c>.
/// Wait uses this to fail <c>resume_unproven</c> when the process exits
/// before a harness report.
/// </summary>
public interface IDestOccupantLiveness
{
    ValueTask<bool> IsAliveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Best-effort stop of the source occupant after the generation fence (spec §4.2).
/// Continuity does not speak NDJSON; Placement implements this.
/// </summary>
public interface ISourceOccupantStopper
{
    ValueTask<ContinuityOutcome> StopAsync(
        SourceOccupantStopRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SourceOccupantStopRequest
{
    public required string SocketPath { get; init; }
    public required string PaneId { get; init; }
    public string? WorkId { get; init; }
    public long? Generation { get; init; }
}

public sealed record DestOccupantStartRequest
{
    public required string MuxId { get; init; }
    public required string SocketPath { get; init; }
    public required string Workspace { get; init; }
    public required string CubeHome { get; init; }
    public required string HarnessId { get; init; }
    public required IReadOnlyList<string> ResumeArgs { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? PaneId { get; init; }
    public string? WorkId { get; init; }
    public long? Generation { get; init; }
}

public sealed record DestOccupantStartResult
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public string? PaneId { get; init; }
    public string? Occupant { get; init; }
    public bool Alive { get; init; }
    public IDestOccupantLiveness? Liveness { get; init; }

    public static DestOccupantStartResult Pass(
        string? paneId,
        string? occupant,
        bool alive = true,
        IDestOccupantLiveness? liveness = null) =>
        new()
        {
            Ok = true,
            PaneId = paneId,
            Occupant = occupant,
            Alive = alive,
            Liveness = liveness,
        };

    public static DestOccupantStartResult Fail(string error) =>
        new() { Ok = false, Error = error };
}
