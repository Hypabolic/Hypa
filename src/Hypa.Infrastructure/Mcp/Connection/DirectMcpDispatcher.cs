using System.Text.Json.Nodes;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hypa.Infrastructure.Mcp.Connection;

internal sealed class DirectMcpDispatcher : IMcpDispatcher
{
    private readonly IMcpServerDefinitionRepository _serverRepo;
    private readonly IMcpClientConnectionFactory _factory;
    private readonly McpConfigValidationService _validator;
    private readonly IClock _clock;
    private readonly ILogger<DirectMcpDispatcher> _logger;

    public DirectMcpDispatcher(
        IMcpServerDefinitionRepository serverRepo,
        IMcpClientConnectionFactory factory,
        McpConfigValidationService validator,
        IClock clock,
        ILogger<DirectMcpDispatcher> logger)
    {
        _serverRepo = serverRepo;
        _factory = factory;
        _validator = validator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<McpSchemaManifest> GetSchemaAsync(CancellationToken ct)
    {
        var serversResult = await _serverRepo.LoadAsync(ct);
        if (!serversResult.IsOk)
        {
            _logger.LogError("Failed to load server definitions: {Error}", serversResult.Error.Message);
            return new McpSchemaManifest(
                [],
                [new McpSchemaError("(config)", McpErrorCodes.SchemaUnavailable, "Failed to load server configuration.")]);
        }

        var serverSchemas = new List<McpServerSchema>();
        var schemaErrors = new List<McpSchemaError>();

        foreach (var server in serversResult.Value)
        {
            var validation = _validator.Validate([server]);
            if (!validation.IsOk)
            {
                var msg = string.Join("; ", validation.Error.Select(e => $"{e.Field}: {e.Message}"));
                _logger.LogWarning("Skipping invalid server config '{Server}': {Errors}", server.Name, msg);
                schemaErrors.Add(new McpSchemaError(server.Name, McpErrorCodes.InvalidRequest, msg));
                continue;
            }

            var clientResult = await _factory.GetOrCreateAsync(server, ct);
            if (!clientResult.IsOk)
            {
                _logger.LogWarning("Skipping schema for '{Server}': {Error}",
                    server.Name, clientResult.Error.Message);
                schemaErrors.Add(new McpSchemaError(server.Name, clientResult.Error.Code, clientResult.Error.Message));
                continue;
            }

            try
            {
                var tools = await clientResult.Value.ListToolsAsync(ct);
                var toolSchemas = tools
                    .Select(t => new McpToolSchema(
                        t.Name,
                        t.Description ?? string.Empty,
                        new JsonPayload(t.ProtocolTool.InputSchema.GetRawText())))
                    .ToList();

                serverSchemas.Add(new McpServerSchema(server.Name, toolSchemas));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list tools for server '{Server}'", server.Name);
                await _factory.InvalidateAsync(server.Name);
                schemaErrors.Add(new McpSchemaError(server.Name, McpErrorCodes.SchemaUnavailable, $"Failed to retrieve tools from server '{server.Name}'."));
            }
        }

        return new McpSchemaManifest(serverSchemas, schemaErrors.Count > 0 ? schemaErrors : null);
    }

    public async Task<IReadOnlyList<McpToolSearchResult>> SearchToolsAsync(string query, CancellationToken ct)
    {
        var schema = await GetSchemaAsync(ct);
        var lower = query.ToLowerInvariant();

        return schema.Servers
            .SelectMany(s => s.Tools.Select(t => (Server: s, Tool: t)))
            .Where(x =>
                x.Tool.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                x.Tool.Description.Contains(lower, StringComparison.OrdinalIgnoreCase))
            .Select(x => new McpToolSearchResult(
                x.Server.ServerName,
                x.Tool.Name,
                x.Tool.Description,
                Score: 1.0))
            .ToList();
    }

    public async Task<McpResult> InvokeAsync(McpProxyRequest request, CancellationToken ct)
    {
        var requestStart = _clock.UtcNow;

        var serversResult = await _serverRepo.LoadAsync(ct);
        if (!serversResult.IsOk)
            return ErrorResult(request, McpErrorCodes.UnknownServer, "Failed to load server definitions.", requestStart);

        var server = serversResult.Value.FirstOrDefault(s => s.Name == request.ServerName);
        if (server is null)
            return ErrorResult(request, McpErrorCodes.UnknownServer,
                $"No server named '{request.ServerName}' is configured.", requestStart);

        var validation = _validator.Validate([server]);
        if (!validation.IsOk)
        {
            var message = string.Join("; ", validation.Error.Select(e => $"{e.Field}: {e.Message}"));
            return ErrorResult(request, McpErrorCodes.InvalidRequest, message, requestStart);
        }

        var clientResult = await _factory.GetOrCreateAsync(server, ct);
        if (!clientResult.IsOk)
            return ErrorResult(request, clientResult.Error.Code, clientResult.Error.Message, requestStart);

        using var timeoutCts = server.RequestTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        if (timeoutCts is not null && server.RequestTimeout.HasValue)
            timeoutCts.CancelAfter(server.RequestTimeout.Value);

        var effectiveCt = timeoutCts?.Token ?? ct;
        var start = requestStart;

        Dictionary<string, object?> args;
        try
        {
            args = ParseArguments(request.Arguments.RawJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid arguments for '{Server}/{Tool}'",
                request.ServerName, request.ToolName);
            return ErrorResult(request, McpErrorCodes.InvalidRequest, $"Invalid arguments: {ex.Message}", requestStart);
        }

        try
        {
            var sdkResult = await clientResult.Value.CallToolAsync(
                request.ToolName,
                args,
                effectiveCt);

            var elapsed = _clock.UtcNow - start;
            var text = sdkResult.Content is not null
                ? string.Concat(sdkResult.Content.OfType<TextContentBlock>().Select(c => c.Text))
                : string.Empty;
            var raw = BuildRawJson(sdkResult);

            if (sdkResult.IsError == true)
            {
                return new McpResult(
                    request.ServerName,
                    request.ToolName,
                    new JsonPayload(raw),
                    text,
                    new McpLatencyMetadata(start, elapsed),
                    IsError: true,
                    new McpProxyError(McpErrorCodes.RemoteToolError, text, request.ServerName, request.ToolName));
            }

            return new McpResult(
                request.ServerName,
                request.ToolName,
                new JsonPayload(raw),
                text,
                new McpLatencyMetadata(start, elapsed),
                IsError: false,
                Error: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await _factory.InvalidateAsync(request.ServerName);
            return ErrorResult(request, McpErrorCodes.Timeout,
                $"Request to '{request.ServerName}' timed out.", start, _clock.UtcNow - start);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool invocation failed for '{Server}/{Tool}'",
                request.ServerName, request.ToolName);
            await _factory.InvalidateAsync(request.ServerName);
            return ErrorResult(request, McpErrorCodes.ToolInvocationFailed,
                $"Tool invocation failed for '{request.ServerName}/{request.ToolName}'.",
                start, _clock.UtcNow - start);
        }
    }

    public async Task<IReadOnlyList<McpResult>> InvokeBatchAsync(
        IReadOnlyList<McpProxyRequest> requests,
        CancellationToken ct)
    {
        var tasks = requests.Select(r => InvokeAsync(r, ct));
        return await Task.WhenAll(tasks);
    }

    private static McpResult ErrorResult(
        McpProxyRequest request,
        string code,
        string message,
        DateTimeOffset startedAt,
        TimeSpan? elapsed = null)
    {
        var latency = new McpLatencyMetadata(startedAt, elapsed ?? TimeSpan.Zero);
        return new McpResult(
            request.ServerName,
            request.ToolName,
            new JsonPayload("{}"),
            string.Empty,
            latency,
            IsError: true,
            new McpProxyError(code, message, request.ServerName, request.ToolName));
    }

    private static Dictionary<string, object?> ParseArguments(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return [];

        var node = JsonNode.Parse(rawJson);
        if (node is not JsonObject obj)
            return [];

        return obj.ToDictionary(kvp => kvp.Key, kvp => ToObject(kvp.Value));
    }

    private static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonObject o => o.ToDictionary(kvp => kvp.Key, kvp => ToObject(kvp.Value)),
        JsonArray a => a.Select(ToObject).ToList(),
        _ => node.ToString(),
    };

    private static string BuildRawJson(CallToolResult result)
    {
        var arr = new JsonArray();
        if (result.Content is null)
            return arr.ToJsonString();

        foreach (var block in result.Content)
        {
            JsonNode item = block switch
            {
                TextContentBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                ImageContentBlock image => new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(image.Data.Span),
                    ["mimeType"] = image.MimeType,
                },
                AudioContentBlock audio => new JsonObject
                {
                    ["type"] = "audio",
                    ["data"] = Convert.ToBase64String(audio.Data.Span),
                    ["mimeType"] = audio.MimeType,
                },
                _ => new JsonObject { ["type"] = block.Type },
            };
            arr.Add(item);
        }

        return arr.ToJsonString();
    }
}
