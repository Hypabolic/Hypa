using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Services;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaReadTool
{
    [McpServerTool(Name = "hypa_read"), Description("Read files in context-aware modes: full, outline, signatures, pruned, smart.")]
    public static async Task<CallToolResult> ExecuteAsync(
        FileReadService fileReadService,
        CancellationToken cancellationToken,
        [Description("File path (relative to project root, or absolute)")] string path,
        [Description("Read mode: full | outline | signatures | pruned | smart (default: smart)")] string? mode = null,
        [Description("Maximum tokens to return")] int? maxTokens = null)
    {
        var result = await fileReadService.ReadAsync(path, mode, maxTokens, cancellationToken);
        return result.IsOk
            ? McpToolResult.Ok(result.Value.Text)
            : McpToolResult.Err($"SUMMARY\nError: {result.Error.Message}");
    }
}
