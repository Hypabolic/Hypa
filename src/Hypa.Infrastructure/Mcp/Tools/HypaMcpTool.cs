using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.Infrastructure.Mcp.Auth;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Mcp.Tools;

[McpServerToolType]
public sealed class HypaMcpTool
{
    [McpServerTool(Name = "hypa_mcp"), Description("MCP proxy operations across configured upstream servers. Actions: invoke, batch, schema, search, auth_check.")]
    public static async Task<CallToolResult> ExecuteAsync(
        McpProxyService proxyService,
        IMcpServerDefinitionRepository serverDefinitionRepository,
        IMcpAuthProvider authProvider,
        IEvidenceLedger evidenceLedger,
        ISessionResolver sessionResolver,
        SecretRedactionRegistry redactionRegistry,
        ILogger<HypaMcpTool> logger,
        CancellationToken cancellationToken,
        [Description("Action: invoke | batch | schema | search | auth_check")] string action,
        [Description("Upstream server name (required for: invoke, auth_check; optional filter for: schema)")] string? server = null,
        [Description("Tool name on the upstream server (required for: invoke)")] string? tool = null,
        [Description("Tool arguments as a JSON object string (for: invoke; default: {})")] string? arguments = null,
        [Description("Compression hint: raw | summary | structured (for: invoke)")] string? hint = null,
        [Description("Batch requests as a JSON array of {server,tool,arguments?,hint?} objects (for: batch)")] string? requests = null,
        [Description("Free-text search query (required for: search)")] string? query = null)
    {
        var sw = Stopwatch.StartNew();

        CallToolResult toolResult;
        try
        {
            toolResult = action switch
            {
                "invoke" => await InvokeAsync(proxyService, server, tool, arguments, hint, cancellationToken),
                "batch" => await BatchAsync(proxyService, requests, cancellationToken),
                "schema" => await SchemaAsync(proxyService, server, cancellationToken),
                "search" => await SearchAsync(proxyService, query, cancellationToken),
                "auth_check" => await AuthCheckAsync(serverDefinitionRepository, authProvider, server, logger, cancellationToken),
                _ => McpToolResult.Err(McpErrorCodes.InvalidRequest, $"Unknown action '{action}'. Valid actions: invoke, batch, schema, search, auth_check.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hypa_mcp unexpected exception for action '{Action}'", action);
            toolResult = McpToolResult.Err(McpErrorCodes.InvalidRequest, $"Unexpected error processing action '{action}'.");
        }

        var argsJson = McpToolResult.BuildArgsJson(
            ("action", action), ("server", server), ("tool", tool), ("hint", hint), ("query", query));
        var resultText = McpToolResult.TextOf(toolResult);
        var redactedResult = redactionRegistry.Redact(resultText);

        try
        {
            var sessionResult = await sessionResolver.ResolveAsync(new SessionResolveOptions(), cancellationToken);
            if (!sessionResult.IsOk)
                logger.LogWarning("hypa_mcp session not resolved: {Error}", sessionResult.Error.Message);
            await evidenceLedger.RecordToolCallAsync(new ToolCallRecord
            {
                SessionId = sessionResult.IsOk ? sessionResult.Value.Id : Guid.Empty,
                ToolName = "hypa_mcp",
                Args = argsJson,
                ArgsHash = HashString(argsJson),
                Result = redactedResult[..Math.Min(200, redactedResult.Length)],
                OutputHash = HashString(redactedResult),
                DurationMs = sw.ElapsedMilliseconds
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hypa_mcp evidence recording failed");
        }

        return toolResult;
    }

    private static async Task<CallToolResult> InvokeAsync(
        McpProxyService proxyService,
        string? server, string? tool, string? arguments, string? hint,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "'server' is required for invoke.");
        if (string.IsNullOrWhiteSpace(tool))
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "'tool' is required for invoke.");

        var request = new McpProxyRequest(server, tool, new JsonPayload(arguments ?? "{}"), ParseHint(hint));
        var result = await proxyService.InvokeAsync(request, ct);
        return FormatInvokeResult(result);
    }

    private static async Task<CallToolResult> BatchAsync(
        McpProxyService proxyService, string? requestsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestsJson))
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "'requests' is required for batch.");

        IReadOnlyList<McpProxyRequest> batch;
        try
        {
            batch = ParseBatchRequests(requestsJson);
        }
        catch
        {
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "Failed to parse batch requests. Ensure 'requests' is a JSON array of {server,tool,arguments?,hint?} objects.");
        }

        if (batch.Count == 0)
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "Batch requests array is empty.");

        var results = await proxyService.InvokeBatchAsync(batch, ct);

        var succeeded = results.Count(r => !r.IsError);
        var failed = results.Count(r => r.IsError);

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Batch completed: {results.Count} request(s) — {succeeded} succeeded, {failed} failed.");
        sb.AppendLine();
        sb.AppendLine("RESULTS");
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"  [{i}] {r.ServerName}/{r.ToolName} {(r.IsError ? "ERROR" : "OK")} ({r.Latency.Elapsed.TotalMilliseconds:F0}ms)");
            if (r.IsError && r.Error is not null)
                sb.AppendLine($"      {r.Error.Code}: {r.Error.Message}");
            else
                sb.AppendLine($"      {Truncate(r.CompressedResponse, 200)}");
        }

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> SchemaAsync(
        McpProxyService proxyService, string? server, CancellationToken ct)
    {
        var manifest = await proxyService.GetSchemaAsync(ct);

        var servers = string.IsNullOrWhiteSpace(server)
            ? manifest.Servers
            : manifest.Servers.Where(s => string.Equals(s.ServerName, server, StringComparison.OrdinalIgnoreCase)).ToList();

        if (servers.Count == 0)
        {
            return McpToolResult.Ok(string.IsNullOrWhiteSpace(server)
                ? "SUMMARY\nNo MCP servers configured."
                : $"SUMMARY\nServer '{server}' not found.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Schema: {servers.Count} server(s), {servers.Sum(s => s.Tools.Count)} tool(s).");
        sb.AppendLine();
        sb.AppendLine("SCHEMA");
        foreach (var srv in servers)
        {
            sb.AppendLine($"  {srv.ServerName} ({srv.Tools.Count} tool(s)):");
            foreach (var t in srv.Tools)
                sb.AppendLine($"    {t.Name}: {t.Description}");
        }

        if (manifest.Errors is { Count: > 0 } errors)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS");
            foreach (var e in errors)
                sb.AppendLine($"  {e.ServerName} [{e.Code}]: {e.Message}");
        }

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> SearchAsync(
        McpProxyService proxyService, string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "'query' is required for search.");

        var results = await proxyService.SearchToolsAsync(query, ct);

        if (results.Count == 0)
            return McpToolResult.Ok($"SUMMARY\nNo tools matching '{query}'.");

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Found {results.Count} tool(s) matching '{query}'.");
        sb.AppendLine();
        sb.AppendLine("RESULTS");
        foreach (var r in results)
            sb.AppendLine($"  {r.ServerName}/{r.ToolName} (score={r.Score:F2}): {r.Description}");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static async Task<CallToolResult> AuthCheckAsync(
        IMcpServerDefinitionRepository serverDefinitionRepository,
        IMcpAuthProvider authProvider,
        string? server,
        ILogger<HypaMcpTool> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server))
            return McpToolResult.Err(McpErrorCodes.InvalidRequest, "'server' is required for auth_check.");

        var loadResult = await serverDefinitionRepository.LoadAsync(ct);
        if (!loadResult.IsOk)
        {
            logger.LogError("auth_check: failed to load server definitions: {Error}", loadResult.Error.Message);
            return McpToolResult.Err(McpErrorCodes.SchemaUnavailable, "Failed to load server configuration.");
        }

        var definition = loadResult.Value.FirstOrDefault(s =>
            string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
            return McpToolResult.Err(McpErrorCodes.UnknownServer, $"Server '{server}' not found in configuration.");

        try
        {
            var authContext = await authProvider.GetAuthContextAsync(definition, ct);
            var authMode = definition.Auth.GetType().Name.Replace("Config", string.Empty, StringComparison.Ordinal);

            var sb = new StringBuilder();
            sb.AppendLine("SUMMARY");
            sb.AppendLine($"Auth check passed for '{server}'.");
            sb.AppendLine();
            sb.AppendLine("DETAILS");
            sb.AppendLine($"  Auth mode:           {authMode}");
            sb.AppendLine($"  Headers resolved:    {authContext.Headers.Count}");
            sb.AppendLine($"  Bearer token:        {(authContext.BearerToken is not null ? "present" : "absent")}");
            sb.AppendLine($"  Client certificate:  {(authContext.ClientCertificatePath is not null ? "configured" : "none")}");

            return McpToolResult.Ok(sb.ToString().TrimEnd());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Auth check failed for server '{Server}'", server);
            return McpToolResult.Err(McpErrorCodes.AuthRequired, $"Auth check failed for '{server}'.");
        }
    }

    private static CallToolResult FormatInvokeResult(McpResult result)
    {
        if (result.IsError)
        {
            var code = result.Error?.Code ?? McpErrorCodes.ToolInvocationFailed;
            var message = result.Error?.Message ?? "Tool invocation failed.";
            return McpToolResult.Err($"SUMMARY\nError ({code}): {message}");
        }

        var sb = new StringBuilder();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Tool '{result.ToolName}' on '{result.ServerName}' completed in {result.Latency.Elapsed.TotalMilliseconds:F0}ms.");
        sb.AppendLine();
        sb.AppendLine("DETAILS");
        sb.AppendLine(result.CompressedResponse);
        sb.AppendLine("STATS");
        sb.Append($"duration={result.Latency.Elapsed.TotalMilliseconds:F0}ms");

        return McpToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static IReadOnlyList<McpProxyRequest> ParseBatchRequests(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Batch requests must be a JSON array.");

        var requests = new List<McpProxyRequest>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            var serverName = element.GetProperty("server").GetString() ?? string.Empty;
            var toolName = element.GetProperty("tool").GetString() ?? string.Empty;
            var argumentsJson = element.TryGetProperty("arguments", out var argsEl)
                ? ArgumentsJson(argsEl)
                : "{}";
            var hintStr = element.TryGetProperty("hint", out var hintEl) ? hintEl.GetString() : null;
            requests.Add(new McpProxyRequest(serverName, toolName, new JsonPayload(argumentsJson), ParseHint(hintStr)));
        }
        return requests;
    }

    private static string ArgumentsJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? "{}"
            : element.GetRawText();

    private static CompressionHint? ParseHint(string? hint) =>
        hint?.ToLowerInvariant() switch
        {
            "raw" => CompressionHint.Raw,
            "summary" => CompressionHint.Summary,
            "structured" => CompressionHint.Structured,
            _ => null
        };

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    private static string HashString(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
