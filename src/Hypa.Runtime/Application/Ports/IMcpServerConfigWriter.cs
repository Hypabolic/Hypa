using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpServerConfigWriter
{
    Task<Result<Unit, Error>> WriteAsync(IReadOnlyList<McpServerDefinition> servers, CancellationToken ct);
}
