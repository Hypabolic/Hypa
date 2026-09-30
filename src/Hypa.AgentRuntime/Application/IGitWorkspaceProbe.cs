namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional git HEAD/dirty probe for checkpoint export. A miss, pin mismatch, or
/// probe failure yields <c>transfer_incomplete</c> — never a hard crash.
/// </summary>
public interface IGitWorkspaceProbe
{
    Task<RuntimeResult<GitWorkspaceProbeResult>> ProbeAsync(
        string projectRoot,
        CancellationToken ct = default,
        ulong? expectedDevice = null,
        ulong? expectedInode = null);
}

/// <summary>Opaque git metadata suitable for a small artifact file.</summary>
public sealed record GitWorkspaceProbeResult
{
    public string? Head { get; init; }
    public bool? Dirty { get; init; }
    public bool Incomplete { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// Test/opt-out stub: reports incomplete without running git.
/// Production wires <c>ProcessGitWorkspaceProbe</c> in AgentServer.
/// </summary>
public sealed class StubGitWorkspaceProbe : IGitWorkspaceProbe
{
    public Task<RuntimeResult<GitWorkspaceProbeResult>> ProbeAsync(
        string projectRoot,
        CancellationToken ct = default,
        ulong? expectedDevice = null,
        ulong? expectedInode = null)
    {
        return Task.FromResult(RuntimeResult<GitWorkspaceProbeResult>.Ok(new GitWorkspaceProbeResult
        {
            Incomplete = true,
            Warning = "git probe not configured",
        }));
    }
}

/// <summary>
/// Complete probe for tests: returns a fixed HEAD and never marks incomplete.
/// </summary>
public sealed class CompleteGitWorkspaceProbe : IGitWorkspaceProbe
{
    private readonly string? _head;
    private readonly bool _dirty;

    public CompleteGitWorkspaceProbe(string? head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bool dirty = false)
    {
        _head = head;
        _dirty = dirty;
    }

    public Task<RuntimeResult<GitWorkspaceProbeResult>> ProbeAsync(
        string projectRoot,
        CancellationToken ct = default,
        ulong? expectedDevice = null,
        ulong? expectedInode = null)
    {
        return Task.FromResult(RuntimeResult<GitWorkspaceProbeResult>.Ok(new GitWorkspaceProbeResult
        {
            Head = _head,
            Dirty = _dirty,
            Incomplete = false,
        }));
    }
}
