using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;

namespace Hypa.Infrastructure.Storage;

public sealed class SessionResolver(ISessionRepository sessionRepository, IProjectRootDetector projectRootDetector) : ISessionResolver
{
    public async Task<Result<ContextSession, Error>> ResolveAsync(SessionResolveOptions options, CancellationToken ct)
    {
        if (options.ExplicitSessionId.HasValue)
            return await sessionRepository.LoadAsync(options.ExplicitSessionId.Value, ct);

        var projectRoot = options.ProjectRoot
            ?? projectRootDetector.Detect(Environment.CurrentDirectory)
            ?? Environment.CurrentDirectory;

        if (options.AtomicAgentSessionId is not null)
        {
            var all = await sessionRepository.ListForProjectAsync(projectRoot, ct);
            if (all.IsOk)
            {
                var match = all.Value.FirstOrDefault(s => s.Binding?.AtomicAgentSessionId == options.AtomicAgentSessionId);
                if (match is not null)
                    return Result<ContextSession, Error>.Ok(match);
            }
        }
        else
        {
            var latest = await sessionRepository.LoadLatestForProjectAsync(projectRoot, ct);
            if (latest.IsOk) return latest;
        }

        if (!options.CreateIfMissing)
            return Result<ContextSession, Error>.Fail(
                new Error("SESSION_NOT_FOUND", "No active session. Run 'hypa session init' to create one."));

        var newSession = new ContextSession
        {
            ProjectRoot = projectRoot,
            Binding = options.AtomicAgentSessionId is not null
                ? new SessionBinding { AtomicAgentSessionId = options.AtomicAgentSessionId }
                : null,
        };
        var save = await sessionRepository.SaveAsync(newSession, ct);
        return save.IsOk
            ? Result<ContextSession, Error>.Ok(newSession)
            : Result<ContextSession, Error>.Fail(save.Error);
    }
}
