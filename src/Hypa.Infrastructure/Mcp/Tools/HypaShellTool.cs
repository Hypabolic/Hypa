using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
        ICommandRunnerService commandRunnerService,
        ICommandRunner commandRunner,
        ICommandRewriteRegistry rewriteRegistry,
        IShellLexer shellLexer,
        ITokenCounter tokenCounter,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger<HypaShellTool> logger,
        McpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken,
        [Description("The shell command to run")] string command,
        [Description("Working directory (optional)")] string? cwd = null,
        [Description("Output mode: omit for default compressed output, or 'raw' to stream directly without compression")] string? mode = null,
        [Description("Timeout in milliseconds (default 120000)")] int? timeoutMs = null)
    {
        var sw = Stopwatch.StartNew();
        var args = McpToolResult.BuildArgsJson(
            ("command", command), ("cwd", cwd), ("mode", mode), ("timeoutMs", (timeoutMs ?? 120_000).ToString()));

        CallToolResult result;
        try
        {
            result = await RunAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hypa_shell unexpected exception");
            result = McpToolResult.Err($"SUMMARY\nError: unexpected failure: {ex.GetType().Name}");
        }

        var resultText = McpToolResult.TextOf(result);
        try
        {
            await RecordEvidenceAsync(evidenceLedger, sessionResolver, logger, args, resultText, sw.ElapsedMilliseconds, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hypa_shell evidence recording failed");
        }

        return result;

        async Task<CallToolResult> RunAsync()
        {
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
                return McpToolResult.Err("SUMMARY\nCommand denied by rewrite policy.");

            var effectiveCommand = rewriteDecision.Outcome is RewriteOutcome.Rewritten or RewriteOutcome.GenericWrapper
                ? rewriteDecision.Command!
                : command;

            var lexed = shellLexer.Lex(effectiveCommand);
            var usesShellSyntax =
                lexed.Any(t => t.Kind is TokenKind.Operator or TokenKind.Pipe or TokenKind.Redirect or TokenKind.Shellism)
                || ShellExpansion.ContainsExpansion(lexed)
                || ShellExpansion.ContainsTildeExpansion(lexed);

            var timeout = TimeSpan.FromMilliseconds(timeoutMs ?? 120_000);
            CommandInvocation invocation;
            if (usesShellSyntax)
            {
                invocation = (OperatingSystem.IsWindows()
                    ? CommandInvocation.Buffered("cmd.exe", ["/d", "/s", "/c", effectiveCommand], effectiveCommand)
                    : CommandInvocation.Buffered("sh", ["-c", effectiveCommand], effectiveCommand))
                    with
                { WorkingDirectory = cwd, Timeout = timeout };
            }
            else
            {
                var tokens = lexed
                    .Where(t => t.Kind is TokenKind.Arg or TokenKind.QuotedArg)
                    .Select(t => t.Kind == TokenKind.QuotedArg ? StripQuotes(t.Value) : t.Value)
                    .ToArray();

                if (tokens.Length == 0)
                    return McpToolResult.Err("SUMMARY\nError: command produced no arguments after tokenisation.");

                invocation = CommandInvocation.Buffered(tokens[0], tokens[1..], effectiveCommand)
                    with
                { WorkingDirectory = cwd, Timeout = timeout };
            }

            if (mode is not null && !string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
                return McpToolResult.Err($"SUMMARY\nError: unsupported mode '{mode}'. Supported values: raw.");

            if (string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
            {
                var rawResult = await commandRunner.RunAsync(invocation, cancellationToken);
                if (!rawResult.IsOk)
                    return McpToolResult.Err($"SUMMARY\nExecution failed: {rawResult.Error.Message}");

                var rawOutput = rawResult.Value;
                var rawCombined = rawOutput.Stdout + (rawOutput.Stderr.Length > 0 ? "\n" + rawOutput.Stderr : "");
                var rawTokens = tokenCounter.EstimateTokens(rawCombined);
                return McpToolResult.Ok($"SUMMARY\nCommand completed (exit {rawOutput.ExitCode}).\n\nDETAILS\n{rawCombined.TrimEnd()}\n\nSTATS\ntokens={rawTokens} duration={sw.ElapsedMilliseconds}ms");
            }

            var runResult = await commandRunnerService.RunBufferedAsync(invocation, CompressionOptions.Default, cancellationToken);
            if (!runResult.IsOk)
            {
                logger.LogDebug("hypa_shell RunBufferedAsync failed: {Error}", runResult.Error.Message);
                return McpToolResult.Err($"SUMMARY\nExecution failed: {runResult.Error.Message}");
            }

            var buffered = runResult.Value;
            var tokenCount = tokenCounter.EstimateTokens(buffered.Text);
            return McpToolResult.Ok($"SUMMARY\nCommand completed (exit {buffered.ExitCode}).\n\nDETAILS\n{buffered.Text.TrimEnd()}\n\nSTATS\ntokens={tokenCount} duration={sw.ElapsedMilliseconds}ms");
        }
    }

    private static async Task RecordEvidenceAsync(
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        ILogger<HypaShellTool> logger,
        string args,
        string resultText,
        long durationMs,
        CancellationToken cancellationToken)
    {
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_shell",
            Args = args,
            ArgsHash = HashString(args),
            Result = resultText[..Math.Min(200, resultText.Length)],
            OutputHash = HashString(resultText),
            DurationMs = durationMs
        }, cancellationToken);
    }

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') ||
                                   (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }
}
