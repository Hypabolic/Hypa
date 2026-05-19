using Hypa.Runtime.Domain.Projects;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Runtime.Application.Ports;

public interface IProjectRegistry
{
    Task<Result<Unit, Error>> RegisterAsync(string rootPath, string agentKey, CancellationToken ct = default);
    Task<Result<Unit, Error>> UnregisterAsync(string rootPath, string agentKey, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRegistration>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRegistration>> GetByAgentAsync(string agentKey, CancellationToken ct = default);
}
