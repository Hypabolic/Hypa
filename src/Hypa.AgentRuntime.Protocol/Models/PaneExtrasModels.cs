using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>pane.rename</c> params. Null / empty / whitespace <c>label</c> clears.</summary>
public sealed record PaneRenameParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary><c>pane.current</c> params. Caller pane if set; else focused pane.</summary>
public sealed record PaneCurrentParams
{
    [JsonPropertyName("caller_pane_id")]
    public string? CallerPaneId { get; init; }
}

/// <summary><c>pane.focus</c> params.</summary>
public sealed record PaneFocusParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

/// <summary><c>pane.neighbor</c> params.</summary>
public sealed record PaneNeighborParams
{
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

/// <summary><c>pane.neighbor</c> result. Does not change focus.</summary>
public sealed record PaneNeighborResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("neighbor_pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? NeighborPaneId { get; init; }

    [JsonPropertyName("layout")]
    public LayoutExportResult? Layout { get; init; }
}

/// <summary><c>pane.edges</c> params.</summary>
public sealed record PaneEdgesParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

/// <summary><c>pane.edges</c> result. A side is true when BSP occupancy has no neighbor.</summary>
public sealed record PaneEdgesResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("left")]
    public bool Left { get; init; }

    [JsonPropertyName("right")]
    public bool Right { get; init; }

    [JsonPropertyName("up")]
    public bool Up { get; init; }

    [JsonPropertyName("down")]
    public bool Down { get; init; }

    [JsonPropertyName("layout")]
    public LayoutExportResult? Layout { get; init; }
}

/// <summary><c>pane.process_info</c> params.</summary>
public sealed record PaneProcessInfoParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

/// <summary>
/// <c>pane.process_info</c> result. Pids/groups are null when absent.
/// <c>foreground_processes</c> names the command that owns the group.
/// </summary>
public sealed record PaneProcessInfoResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("shell_pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ShellPid { get; init; }

    [JsonPropertyName("foreground_process_group_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? ForegroundProcessGroupId { get; init; }

    [JsonPropertyName("foreground_processes")]
    public IReadOnlyList<JsonElement>? ForegroundProcesses { get; init; }
}

/// <summary>
/// <c>pane.send_input</c> params. Key-combo input; not <c>pane.send_text</c>.
/// </summary>
public sealed record PaneSendInputParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("keys")]
    public IReadOnlyList<string>? Keys { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>pane.send_input</c> result.</summary>
public sealed record PaneSendInputResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("accepted_bytes")]
    public int AcceptedBytes { get; init; }
}

/// <summary><c>pane.input.set</c> params. <c>right_click</c> is <c>hypa</c> or <c>pane</c>.</summary>
public sealed record PaneInputSetParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("right_click")]
    public string? RightClick { get; init; }
}

/// <summary><c>pane.input.set</c> result.</summary>
public sealed record PaneInputSetResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("right_click")]
    public string? RightClick { get; init; }
}
