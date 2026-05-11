using Hypa.Runtime.Domain.Filters;

namespace Hypa.Runtime.Application.Ports;

public interface ITrustStore
{
    bool IsTrusted(string projectRoot, string filePath, string fileHash);
    Task GrantAsync(TrustRecord record, CancellationToken ct);
    Task<IReadOnlyList<TrustRecord>> GetAllAsync(CancellationToken ct);
}
