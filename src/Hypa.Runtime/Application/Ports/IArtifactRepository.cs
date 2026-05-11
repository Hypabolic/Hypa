using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Ports;

public interface IArtifactRepository
{
    Task<Result<ArtifactRef, Error>> StoreAsync(string content, string mimeType, Guid sessionId, CancellationToken ct);
    Task<Result<string, Error>> LoadAsync(Guid artifactId, CancellationToken ct);
    Task<Result<IReadOnlyList<ArtifactRef>, Error>> ListForSessionAsync(Guid sessionId, CancellationToken ct);
}
