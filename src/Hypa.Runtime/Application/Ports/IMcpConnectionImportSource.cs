using Hypa.Runtime.Domain.Mcp;

namespace Hypa.Runtime.Application.Ports;

public interface IMcpConnectionImportSource
{
    string AgentKey { get; }
    bool SupportsScope(McpImportScope scope);
    Task<IReadOnlyList<McpImportedConnection>> DiscoverAsync(McpImportDiscoveryRequest request, CancellationToken ct);
}

public sealed record McpImportDiscoveryRequest(McpImportScope Scope, string? ProjectRoot);

public enum McpImportScope
{
    Global,
    Project,
    All,
}

public sealed record McpImportedConnection(
    string SourceAgent,
    string SourceScope,
    string SourceName,
    McpServerDefinition? Server,
    string Fingerprint,
    McpImportCandidateStatus Status,
    string? Detail);

public enum McpImportCandidateStatus
{
    Importable,
    SkippedSelf,
    SkippedUnsafeSecret,
    SkippedIncomplete,
    SkippedUnsupported,
    SkippedDuplicate,
    SkippedConflict,
    ParseError,
}
