using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpAuthProvider
{
    ValueTask<McpAuthContext> GetAuthContextAsync(McpServerDefinition server, CancellationToken ct);
}
