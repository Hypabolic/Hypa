using System.Text.Json.Serialization;
using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Cli.Json;

[JsonSerializable(typeof(McpResult))]
[JsonSerializable(typeof(IReadOnlyList<McpResult>))]
[JsonSerializable(typeof(McpSchemaManifest))]
[JsonSerializable(typeof(McpServerSchema))]
[JsonSerializable(typeof(McpToolSchema))]
[JsonSerializable(typeof(McpSchemaError))]
[JsonSerializable(typeof(IReadOnlyList<McpSchemaError>))]
[JsonSerializable(typeof(IReadOnlyList<McpToolSearchResult>))]
[JsonSerializable(typeof(McpToolSearchResult))]
[JsonSerializable(typeof(McpProxyError))]
[JsonSerializable(typeof(McpLatencyMetadata))]
[JsonSerializable(typeof(JsonPayload))]
[JsonSerializable(typeof(AuthCheckResult))]
[JsonSerializable(typeof(IReadOnlyList<McpServerListItemJson>))]
[JsonSerializable(typeof(IReadOnlyList<McpToolListEntryJson>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
internal sealed partial class McpJsonContext : JsonSerializerContext { }

internal sealed record AuthCheckResult(
    string Server,
    string AuthMode,
    bool Passed,
    string? Error = null);

internal sealed record McpServerListItemJson(
    string Name,
    string Transport,
    string? Endpoint,
    string Auth,
    bool HasTls);

internal sealed record McpToolListEntryJson(
    string ServerName,
    string ToolName,
    string Description);

internal sealed record McpAddDryRunJson(
    string Name,
    string Transport,
    string? Endpoint,
    McpAddAuthDryRunJson? Auth,
    McpAddTlsDryRunJson? Tls,
    int? ConnectTimeoutSeconds,
    int? RequestTimeoutSeconds);

internal sealed record McpAddAuthDryRunJson(
    string Type,
    string? TokenRef = null,
    string? HeaderName = null,
    string? ValueRef = null,
    bool? InQueryString = null,
    string? UsernameRef = null,
    string? PasswordRef = null,
    string? TokenUrl = null,
    string? ClientIdRef = null,
    string? ClientSecretRef = null,
    string[]? Scopes = null,
    string? AuthUrl = null,
    string? ClientId = null,
    string? ClientCertRef = null,
    string? ClientKeyRef = null);

internal sealed record McpAddTlsDryRunJson(
    string? CaCertPath,
    string? ClientCertPath,
    string? ClientKeyPath);

internal sealed record McpAddResultJson(
    bool Success,
    string? Name = null,
    string? Transport = null,
    string? Endpoint = null,
    string? Auth = null,
    int? ToolCount = null,
    string? Error = null,
    McpAddGuidanceJson? Guidance = null);

internal sealed record McpAddGuidanceJson(
    string? SuggestedAuthMode = null,
    string? AuthorizationUrl = null,
    IReadOnlyList<string>? NextCommands = null);

[JsonSerializable(typeof(McpAddDryRunJson))]
[JsonSerializable(typeof(McpAddAuthDryRunJson))]
[JsonSerializable(typeof(McpAddTlsDryRunJson))]
[JsonSerializable(typeof(McpAddResultJson))]
[JsonSerializable(typeof(McpAddGuidanceJson))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpDryRunJsonContext : JsonSerializerContext { }
