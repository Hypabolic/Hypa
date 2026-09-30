using System.Text.Json.Serialization;

namespace Hypa.Annotate.Domain;

/// <summary>
/// Capture provenance supplied by the plugin host at annotation time.
/// </summary>
public sealed record CaptureContext
{
    [JsonPropertyName("workspace_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("workspace_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkspaceLabel { get; init; }

    [JsonPropertyName("tab_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TabId { get; init; }

    [JsonPropertyName("tab_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TabLabel { get; init; }

    [JsonPropertyName("focused_pane_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedPaneId { get; init; }

    [JsonPropertyName("focused_pane_cwd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedPaneCwd { get; init; }

    [JsonPropertyName("focused_pane_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FocusedPaneAgent { get; init; }
}
