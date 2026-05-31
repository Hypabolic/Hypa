namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpAuthGuidance(
    string? SuggestedAuthMode,
    string? AuthorizationUrl,
    string? TokenUrl,
    string? ClientId,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? NextCommands);
