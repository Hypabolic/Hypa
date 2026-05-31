using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpServerConfigReader
{
    Task<Result<IReadOnlyList<McpServerDefinition>, Error>> ReadEditableAsync(CancellationToken ct);
}
