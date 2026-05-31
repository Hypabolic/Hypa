using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpServerDefinitionRepository
{
    Task<Result<IReadOnlyList<McpServerDefinition>, Error>> LoadAsync(CancellationToken ct);
}
