using Hypa.Runtime.Domain.Projects;

namespace Hypa.Runtime.Application.Ports;

public interface IProjectRegistry
{
    Task RegisterAsync(string rootPath, string agentKey, CancellationToken ct = default);
    Task UnregisterAsync(string rootPath, string agentKey, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRegistration>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRegistration>> GetByAgentAsync(string agentKey, CancellationToken ct = default);
}
