using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Same-process dest worker. Dest HOME and dest cwd live on this object.
/// Source HandoffService does not resolve those paths.
/// </summary>
public sealed class InProcessDestWorkExecutor : IDestWorkExecutor
{
    private readonly DestApplyService _apply;
    private readonly DestApplyHostContext _host;
    private readonly IWorkPackSpool _destSpool;

    public InProcessDestWorkExecutor(
        DestApplyService apply,
        DestApplyHostContext host,
        IWorkPackSpool destSpool)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _destSpool = destSpool ?? throw new ArgumentNullException(nameof(destSpool));
    }

    public ValueTask<DestApplyResult> ApplyAsync(
        DestApplyInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(invocation.Request);
        var attemptId = invocation.Request.AttemptId;
        if (string.IsNullOrWhiteSpace(invocation.PackPath) || !File.Exists(invocation.PackPath))
        {
            return ValueTask.FromResult(
                DestApplyResult.Fail(
                    attemptId,
                    ContinuityReasons.PackInvalid,
                    "source pack is missing"));
        }

        if (!WorkId.TryParse(invocation.Request.WorkId, out var workId))
        {
            return ValueTask.FromResult(
                DestApplyResult.Fail(
                    attemptId,
                    ContinuityReasons.Internal,
                    "work id is invalid"));
        }

        Directory.CreateDirectory(_destSpool.SpoolDirectory);
        var destPack = Path.Combine(_destSpool.SpoolDirectory, workId.Value + ".workpack");
        File.Copy(invocation.PackPath, destPack, overwrite: true);
        var sidecar = invocation.PackPath + ".sha256";
        if (File.Exists(sidecar))
            File.Copy(sidecar, destPack + ".sha256", overwrite: true);

        return _apply.ApplyPackAsync(destPack, invocation.Request, _host, cancellationToken);
    }
}
