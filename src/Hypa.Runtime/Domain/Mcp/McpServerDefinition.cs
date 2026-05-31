namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpServerDefinition(
    string Name,
    McpTransportConfig Transport,
    McpAuthConfig Auth,
    McpTlsConfig? Tls = null,
    TimeSpan? ConnectTimeout = null,
    TimeSpan? RequestTimeout = null);
