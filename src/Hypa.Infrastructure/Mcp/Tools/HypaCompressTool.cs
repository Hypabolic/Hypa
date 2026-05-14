using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaCompressTool
{
    [McpServerTool(Name = "hypa_compress"), Description("Compress explicit text. Kinds: shell-output, log, code, generic.")]
    public static async Task<CallToolResult> ExecuteAsync(
        IEnumerable<IOutputCompressor> compressors,
        ITokenCounter tokenCounter,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger<HypaCompressTool> logger,
        CancellationToken cancellationToken,
        [Description("Text to compress")] string input,
        [Description("Output kind: shell-output | log | code | generic")] string? kind = null,
        [Description("Original command (helps select the right compressor)")] string? command = null,
        [Description("Maximum output tokens")] int? maxTokens = null)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(input))
            return McpToolResult.Err("SUMMARY\nError: input is required.");

        var compressorList = compressors.ToList();
        var originalTokens = tokenCounter.EstimateTokens(input);

        var commandStr = command ?? "compress";
        var invocation = CommandInvocation.Buffered("compress", [], commandStr);
        var output = CommandOutput.Captured(input, string.Empty, 0, TimeSpan.Zero);
        var options = new CompressionOptions
        {
            MaxTotalLines = maxTokens.HasValue ? maxTokens.Value / 2 : 500
        };

        var compressor = compressorList.FirstOrDefault(c => c.CanHandle(invocation))
                         ?? compressorList.FirstOrDefault(c => c.Id == "generic");

        string finalText;
        string reducerId;
        int compressedTokens;

        if (compressor is not null)
        {
            var result = compressor.Compress(invocation, output, options);
            finalText = result.Text;
            reducerId = result.ReducerId;
            compressedTokens = result.CompressedTokens;
        }
        else
        {
            finalText = input;
            reducerId = "passthrough";
            compressedTokens = originalTokens;
        }

        var saving = originalTokens > 0
            ? (int)Math.Round((1.0 - (double)compressedTokens / originalTokens) * 100)
            : 0;

        var text = $"SUMMARY\nCompressed {originalTokens} → {compressedTokens} tokens (-{saving}%).\n\nDETAILS\n{finalText.TrimEnd()}\n\nSTATS\noriginal={originalTokens} compressed={compressedTokens} saving={saving}% reducer={reducerId} duration={sw.ElapsedMilliseconds}ms";

        var args = McpToolResult.BuildArgsJson(
            ("kind", kind ?? "generic"), ("command", command),
            ("maxTokens", maxTokens?.ToString()));
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_compress",
            Args = args,
            ArgsHash = HashString(args),
            Result = text[..Math.Min(200, text.Length)],
            OutputHash = HashString(text),
            DurationMs = sw.ElapsedMilliseconds
        }, cancellationToken);

        return McpToolResult.Ok(text);
    }

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
