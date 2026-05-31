using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpServerProbe
{
    Task<McpServerProbeResult> ProbeAsync(McpServerDefinition server, CancellationToken ct);
}
