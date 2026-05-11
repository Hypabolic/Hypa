using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Runtime.Application.Ports;

public interface ISessionResolver
{
    Task<Result<ContextSession, Error>> ResolveAsync(SessionResolveOptions options, CancellationToken ct);
}
