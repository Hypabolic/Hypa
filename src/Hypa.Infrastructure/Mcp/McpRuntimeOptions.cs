namespace Hypa.Infrastructure.Mcp;

public sealed class McpRuntimeOptions
{
    public bool ReadOnly { get; set; }
    public IReadOnlyList<string>? ToolFilter { get; set; }
}
