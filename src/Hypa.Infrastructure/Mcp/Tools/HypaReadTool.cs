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
        if (!result.IsOk)
            return McpToolResult.Err($"SUMMARY\nError: {result.Error.Message}");

        var output = result.Value;
        if (output.IsImage && output.ImageBytes is not null && output.ImageMimeType is not null)
            return McpToolResult.OkWithImage(output.Text, output.ImageBytes, output.ImageMimeType);

        return McpToolResult.Ok(output.Text);
    }
}
