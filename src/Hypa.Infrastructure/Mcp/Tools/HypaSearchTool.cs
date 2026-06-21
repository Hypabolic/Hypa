using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Services;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaSearchTool
{
    [McpServerTool(Name = "hypa_search"), Description("Search files, symbols, and indexed context. Kinds: text, regex, symbol.")]
    public static async Task<CallToolResult> ExecuteAsync(
        SearchService searchService,
        CancellationToken cancellationToken,
        [Description("Search query")] string query,
        [Description("Scope: project | session | code | docs")] string? scope = null,
        [Description("Search kind: text | regex | symbol (default: text)")] string? kind = null,
        [Description("Maximum number of results (default: 20)")] int? maxResults = null)
    {
        var result = await searchService.SearchAsync(query, scope, kind, maxResults, cancellationToken);
        return result.IsOk
            ? McpToolResult.Ok(result.Value.Text)
            : McpToolResult.Err($"SUMMARY\nError: {result.Error.Message}");
    }
}
