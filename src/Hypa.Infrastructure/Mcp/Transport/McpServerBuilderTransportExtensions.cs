using Microsoft.Extensions.DependencyInjection;

namespace Hypa.Infrastructure.Mcp.Transport;

public static class McpServerBuilderTransportExtensions
{
    /// <summary>
    /// Replaces the default JSON-line stdio transport with Content-Length-framed
    /// adapters so the server accepts the framing format used by Claude Code's MCP client.
    /// </summary>
    public static IMcpServerBuilder WithContentLengthStdioTransport(this IMcpServerBuilder builder)
    {
        var stdin = new ContentLengthInputStream(Console.OpenStandardInput());
        var stdout = new ContentLengthOutputStream(Console.OpenStandardOutput());
        return builder.WithStreamServerTransport(stdin, stdout);
    }
}
