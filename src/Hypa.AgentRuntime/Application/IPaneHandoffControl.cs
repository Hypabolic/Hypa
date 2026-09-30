namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional same-host handoff on a pane runtime. Process-io never implements this.
/// </summary>
public interface IPaneHandoffControl
{
    bool SupportsHandoff { get; }

    Task<RuntimeResult<PaneHandoffExport>> ExportHandoffAsync(
        string runtimeSessionId,
        int generation,
        CancellationToken ct);

    Task<RuntimeResult<PaneHandoffAdopt>> AdoptHandoffAsync(
        string handoffPath,
        string nonceHex,
        int generation,
        CancellationToken ct);
}

/// <summary>Private AF_UNIX path plus nonce/generation for a peer adopt.</summary>
public sealed record PaneHandoffExport
{
    public required string HandoffPath { get; init; }

    public required string NonceHex { get; init; }

    public required int Generation { get; init; }
}

/// <summary>Adopt result. Child pid is present when import succeeded.</summary>
public sealed record PaneHandoffAdopt
{
    public required bool Adopted { get; init; }

    public int? ChildPid { get; init; }
}
