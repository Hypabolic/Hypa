using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Ports;

public interface ISessionRepository
{
    Task<Result<ContextSession, Error>> LoadAsync(Guid id, CancellationToken ct);
    Task<Result<ContextSession, Error>> LoadLatestForProjectAsync(string projectRoot, CancellationToken ct);
    Task<Result<Unit, Error>> SaveAsync(ContextSession session, CancellationToken ct);
    Task<Result<IReadOnlyList<ContextSession>, Error>> ListForProjectAsync(string projectRoot, CancellationToken ct);
}
