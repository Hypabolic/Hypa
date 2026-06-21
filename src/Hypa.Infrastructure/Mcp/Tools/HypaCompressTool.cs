using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Services;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaCompressTool
{
    [McpServerTool(Name = "hypa_compress"), Description("Compress explicit text. Kinds: shell-output, log, code, generic.")]
    public static async Task<CallToolResult> ExecuteAsync(
        CompressService compressService,
        CancellationToken cancellationToken,
        [Description("Text to compress")] string input,
        [Description("Output kind: shell-output | log | code | generic")] string? kind = null,
        [Description("Original command (helps select the right compressor)")] string? command = null,
        [Description("Maximum output tokens")] int? maxTokens = null)
    {
        var result = await compressService.CompressAsync(input, kind, command, maxTokens, cancellationToken);
        return result.IsOk
            ? McpToolResult.Ok(result.Value.Text)
            : McpToolResult.Err($"SUMMARY\nError: {result.Error.Message}");
    }
}
