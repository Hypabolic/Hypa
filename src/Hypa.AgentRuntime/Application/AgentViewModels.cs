using System.Text.Json;

namespace Hypa.AgentRuntime.Application;

/// <summary>Validated transient Agents-view override. Not persisted.</summary>
public sealed record AgentViewSpec
{
    public required string Source { get; init; }
    public string? Label { get; init; }
    public JsonElement Filter { get; init; }
    public JsonElement Sort { get; init; }

    /// <summary>
    /// an empty sort keeps the configured Agents order.
    /// </summary>
    public bool HasSort =>
        Sort.ValueKind is JsonValueKind.Array && Sort.GetArrayLength() > 0;
}

/// <summary>One Agents-view row. Built from live pane + tab + workspace order.</summary>
public sealed record AgentViewRow
{
    public required string PaneId { get; init; }
    public required string TabId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? Agent { get; init; }
    public required string Status { get; init; }
    public required bool Seen { get; init; }
    public IReadOnlyDictionary<string, string> Tokens { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public int WorkspaceOrder { get; init; }
    public int TabOrder { get; init; }
    public int PaneOrder { get; init; }
    public int Attention { get; init; }
    public ulong? StateChangeSeq { get; init; }
}

/// <summary>Focused workspace and tab used by view context operands.</summary>
public sealed record AgentViewEvalContext(string? CurrentWorkspaceId, string? CurrentTabId);
