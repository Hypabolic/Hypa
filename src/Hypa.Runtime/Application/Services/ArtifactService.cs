using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Services;

public sealed class ArtifactService(IArtifactRepository artifacts)
{
    public Task<Result<ArtifactRef, Error>> StoreAsync(string content, string mimeType, Guid sessionId, CancellationToken ct) =>
        artifacts.StoreAsync(content, mimeType, sessionId, ct);

    public Task<Result<IReadOnlyList<ArtifactRef>, Error>> ListAsync(Guid sessionId, CancellationToken ct) =>
        artifacts.ListForSessionAsync(sessionId, ct);
}
