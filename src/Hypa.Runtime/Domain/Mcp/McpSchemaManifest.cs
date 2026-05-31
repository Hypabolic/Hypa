namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpSchemaManifest(
    IReadOnlyList<McpServerSchema> Servers,
    IReadOnlyList<McpSchemaError>? Errors = null);

public sealed record McpServerSchema(string ServerName, IReadOnlyList<McpToolSchema> Tools);

public sealed record McpToolSchema(string Name, string Description, JsonPayload InputSchema);

public sealed record McpSchemaError(string ServerName, string Code, string Message);
