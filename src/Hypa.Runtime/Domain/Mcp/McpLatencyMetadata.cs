namespace Hypa.Runtime.Domain.Mcp;

public sealed record McpLatencyMetadata(DateTimeOffset StartedAt, TimeSpan Elapsed);
