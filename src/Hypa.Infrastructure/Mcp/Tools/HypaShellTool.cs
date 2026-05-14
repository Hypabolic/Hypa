using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Rewrite;
using Hypa.Runtime.Domain.Runner;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaShellTool
{
    [McpServerTool(Name = "hypa_shell"), Description("Run shell commands with optional compression and evidence recording. Applies rewrite and deny rules.")]
    public static async Task<CallToolResult> ExecuteAsync(
        ICommandRunner commandRunner,
        ICommandRewriteRegistry rewriteRegistry,
        IShellLexer shellLexer,
        ISessionResolver sessionResolver,
        IEvidenceLedger evidenceLedger,
        ITokenCounter tokenCounter,
        ILogger<HypaShellTool> logger,
        McpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken,
        [Description("The shell command to run")] string command,
        [Description("Working directory (optional)")] string? cwd = null,
        [Description("Output mode: auto, raw, compress, track")] string? mode = null,
        [Description("Timeout in milliseconds (default 120000)")] int? timeoutMs = null)
    {
        var sw = Stopwatch.StartNew();

        if (runtimeOptions.ReadOnly)
            return McpToolResult.Err("SUMMARY\nBlocked: hypa_shell is disabled in read-only mode.");

        if (string.IsNullOrWhiteSpace(command))
            return McpToolResult.Err("SUMMARY\nError: command is required.");

        var rewriteContext = new RewriteContext(
            IsHypaDisabled: false,
            ExcludeCommands: [],
            GenericWrapperEnabled: true);

        var rewriteDecision = rewriteRegistry.Rewrite(command, rewriteContext);
        if (rewriteDecision.Outcome == RewriteOutcome.Deny)
        {
            const string deniedText = "SUMMARY\nCommand denied by rewrite policy.";
            await RecordEvidenceAsync(evidenceLedger, sessionResolver, tokenCounter, logger, command, deniedText, 0, sw.ElapsedMilliseconds, cancellationToken);
            return McpToolResult.Err(deniedText);
        }

        var effectiveCommand = rewriteDecision.Outcome is RewriteOutcome.Rewritten or RewriteOutcome.GenericWrapper
            ? rewriteDecision.Command!
            : command;

        var tokens = shellLexer.Lex(effectiveCommand)
            .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
            .Select(t => t.Kind == TokenKind.QuotedArg ? StripQuotes(t.Value) : t.Value)
            .ToArray();

        if (tokens.Length == 0)
            return McpToolResult.Err("SUMMARY\nError: command produced no arguments after tokenisation.");

        var timeout = TimeSpan.FromMilliseconds(timeoutMs ?? 120_000);
        var invocation = CommandInvocation.Buffered(tokens[0], tokens[1..], effectiveCommand)
            with
        { WorkingDirectory = cwd, Timeout = timeout };

        var runResult = await commandRunner.RunAsync(invocation, cancellationToken);
        if (!runResult.IsOk)
            return McpToolResult.Err($"SUMMARY\nExecution failed: {runResult.Error.Message}");

        var output = runResult.Value;
        var combined = output.Stdout + (output.Stderr.Length > 0 ? "\n" + output.Stderr : "");
        var tokenCount = tokenCounter.EstimateTokens(combined);

        var text = $"SUMMARY\nCommand completed (exit {output.ExitCode}).\n\nDETAILS\n{combined.TrimEnd()}\n\nSTATS\ntokens={tokenCount} duration={sw.ElapsedMilliseconds}ms";

        await RecordEvidenceAsync(evidenceLedger, sessionResolver, tokenCounter, logger, command, text, output.ExitCode, sw.ElapsedMilliseconds, cancellationToken);

        return McpToolResult.Ok(text);
    }

    private static async Task RecordEvidenceAsync(
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ITokenCounter tokenCounter,
        ILogger<HypaShellTool> logger,
        string command,
        string outputText,
        int exitCode,
        long durationMs,
        CancellationToken ct)
    {
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), ct);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        var sessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty;
        var tokenCount = tokenCounter.EstimateTokens(outputText);

        await evidenceLedger.RecordCommandMetricsAsync(new CommandMetricsRecord
        {
            SessionId = sessionId,
            Command = command,
            ExitCode = exitCode,
            DurationMs = durationMs,
            OriginalTokens = tokenCount,
            CompressedTokens = tokenCount,
            ReducerId = "mcp"
        }, ct);
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                   (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }
}
