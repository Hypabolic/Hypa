namespace Hypa.Runtime.Application.Ports;

public interface IBinaryRemover
{
    Task<BinaryRemoveResult> RemoveAsync(bool dryRun, CancellationToken ct = default);
}

public sealed record BinaryRemoveResult(bool Removed, string? Detail = null);
