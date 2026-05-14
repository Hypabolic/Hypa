using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaSessionTool
{
    private static readonly IReadOnlySet<string> MutatingActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "init", "attach", "checkpoint"
    };

    [McpServerTool(Name = "hypa_session"), Description("Inspect and mutate local session state. Actions: status, init, attach, checkpoint.")]
    public static async Task<CallToolResult> ExecuteAsync(
        ISessionRepository sessionRepository,
        ISessionResolver sessionResolver,
        IEvidenceLedger evidenceLedger,
        IClock clock,
        ILogger<HypaSessionTool> logger,
        McpRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken,
        [Description("Action: status | init | attach | checkpoint")] string action = "status",
        [Description("Session ID (required for attach)")] string? sessionId = null,
        [Description("Text for record actions")] string? text = null,
        [Description("Category for record actions")] string? category = null)
    {
        var sw = Stopwatch.StartNew();

        if (runtimeOptions.ReadOnly && MutatingActions.Contains(action))
            return McpToolResult.Err($"SUMMARY\nRead-only mode: action '{action}' is not permitted.");

        var toolResult = await DispatchAsync(action, sessionId, text, category, sessionRepository, sessionResolver, clock, cancellationToken);
        var resultText = McpToolResult.TextOf(toolResult);

        var args = McpToolResult.BuildArgsJson(
            ("action", action), ("sessionId", sessionId), ("category", category));
        var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
        if (!sessionResult.IsOk)
            logger.LogWarning("session not resolved, recording with empty ID: {Error}", sessionResult.Error.Message);
        await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
        {
            SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
            ToolName = "hypa_session",
            Args = args,
            ArgsHash = HashString(args),
            Result = resultText[..Math.Min(200, resultText.Length)],
            OutputHash = HashString(resultText),
            DurationMs = sw.ElapsedMilliseconds
        }, cancellationToken);

        return toolResult;
    }

    private static async Task<CallToolResult> DispatchAsync(
        string action,
        string? sessionId,
        string? text,
        string? category,
        ISessionRepository sessionRepository,
        ISessionResolver sessionResolver,
        IClock clock,
        CancellationToken ct) =>
        action switch
        {
            "status" => await StatusAsync(sessionResolver, ct),
            "init" => await InitAsync(sessionResolver, ct),
            "attach" => await AttachAsync(sessionId, sessionRepository, ct),
            "checkpoint" => await CheckpointAsync(sessionRepository, sessionResolver, clock, ct),
            _ => McpToolResult.Err($"SUMMARY\nUnknown action '{action}'. Valid actions: status, init, attach, checkpoint.")
        };

    private static async Task<CallToolResult> StatusAsync(ISessionResolver sessionResolver, CancellationToken ct)
    {
        var result = await sessionResolver.ResolveAsync(new SessionResolveOptions(), ct);
        if (!result.IsOk)
            return McpToolResult.Ok("SUMMARY\nNo active session.");

        var session = result.Value;
        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Active session: {session.Id}");
        sb.AppendLine();
        sb.AppendLine("DETAILS");
        sb.AppendLine($"Project root: {session.ProjectRoot}");
        sb.AppendLine($"Created: {session.CreatedAt:u}");
        sb.AppendLine($"Updated: {session.UpdatedAt:u}");
        if (session.CheckpointedAt.HasValue)
            sb.AppendLine($"Last checkpoint: {session.CheckpointedAt:u}");
        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> InitAsync(ISessionResolver sessionResolver, CancellationToken ct)
    {
        var opts = new SessionResolveOptions { CreateIfMissing = true };
        var result = await sessionResolver.ResolveAsync(opts, ct);
        if (!result.IsOk)
            return McpToolResult.Err($"SUMMARY\nFailed to init session: {result.Error.Message}");

        var session = result.Value;
        return McpToolResult.Ok($"SUMMARY\nSession ready: {session.Id}\n\nDETAILS\nProject root: {session.ProjectRoot}");
    }

    private static async Task<CallToolResult> AttachAsync(string? sessionId, ISessionRepository sessionRepository, CancellationToken ct)
    {
        if (sessionId is null)
            return McpToolResult.Err("SUMMARY\nError: sessionId is required for attach.");

        if (!Guid.TryParse(sessionId, out var id))
            return McpToolResult.Err("SUMMARY\nError: sessionId is not a valid GUID.");

        var result = await sessionRepository.LoadAsync(id, ct);
        if (!result.IsOk)
            return McpToolResult.Err($"SUMMARY\nFailed to attach: {result.Error.Message}");

        return McpToolResult.Ok($"SUMMARY\nAttached to session {id}");
    }

    private static async Task<CallToolResult> CheckpointAsync(
        ISessionRepository sessionRepository,
        ISessionResolver sessionResolver,
        IClock clock,
        CancellationToken ct)
    {
        var resolveResult = await sessionResolver.ResolveAsync(new SessionResolveOptions { CreateIfMissing = false }, ct);
        if (!resolveResult.IsOk)
            return McpToolResult.Err("SUMMARY\nNo active session to checkpoint.");

        var session = resolveResult.Value;
        var updated = session with { CheckpointedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var saveResult = await sessionRepository.SaveAsync(updated, ct);
        if (!saveResult.IsOk)
            return McpToolResult.Err($"SUMMARY\nCheckpoint failed: {saveResult.Error.Message}");

        return McpToolResult.Ok($"SUMMARY\nCheckpoint recorded at {updated.CheckpointedAt:u}");
    }

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
