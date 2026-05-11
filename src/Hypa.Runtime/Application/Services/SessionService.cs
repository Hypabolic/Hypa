using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Services;

public sealed class SessionService(ISessionRepository sessionRepository, ISessionResolver sessionResolver)
{
    public Task<Result<ContextSession, Error>> InitAsync(SessionResolveOptions options, CancellationToken ct) =>
        sessionResolver.ResolveAsync(options with { CreateIfMissing = true }, ct);

    public Task<Result<ContextSession, Error>> StatusAsync(SessionResolveOptions options, CancellationToken ct) =>
        sessionResolver.ResolveAsync(options with { CreateIfMissing = false }, ct);

    public async Task<Result<ContextSession, Error>> CheckpointAsync(Guid sessionId, CancellationToken ct)
    {
        var result = await sessionRepository.LoadAsync(sessionId, ct);
        if (!result.IsOk) return result;
        var updated = result.Value with { CheckpointedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var save = await sessionRepository.SaveAsync(updated, ct);
        return save.IsOk ? Result<ContextSession, Error>.Ok(updated) : Result<ContextSession, Error>.Fail(save.Error);
    }

    public Task<Result<ContextSession, Error>> AttachAsync(Guid sessionId, CancellationToken ct) =>
        sessionRepository.LoadAsync(sessionId, ct);
}
