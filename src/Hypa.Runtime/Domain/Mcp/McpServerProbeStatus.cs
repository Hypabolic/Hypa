namespace Hypa.Runtime.Domain.Mcp;

public enum McpServerProbeStatus
{
    Reachable = 0,
    AuthRequired = 1,
    InvalidConfig = 2,
    Timeout = 3,
    ConnectionFailed = 4,
    Unknown = 5,
}
