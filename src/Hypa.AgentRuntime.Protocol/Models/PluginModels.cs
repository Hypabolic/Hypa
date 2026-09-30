using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record PluginLinkParams
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

public sealed record PluginListParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginUnlinkParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginSetEnabledParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginActionListParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginActionInvokeParams
{
    [JsonPropertyName("action_id")]
    public string? ActionId { get; init; }

    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("context")]
    public PluginInvocationContextDto? Context { get; init; }

    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("revision")]
    public long? Revision { get; init; }
}

public sealed record PluginLogListParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

public sealed record PluginPaneOpenParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("entrypoint")]
    public string? Entrypoint { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("width")]
    public JsonElement? Width { get; init; }

    [JsonPropertyName("height")]
    public JsonElement? Height { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("target_pane_id")]
    public string? TargetPaneId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("focus")]
    public bool? Focus { get; init; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }
}

public sealed record PluginPaneFocusParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record PluginPaneCloseParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record PluginPaneSendTextParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public sealed record PluginWorktreeContextDto
{
    [JsonPropertyName("repo_key")]
    public string? RepoKey { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("repo_root")]
    public string? RepoRoot { get; init; }

    [JsonPropertyName("checkout_path")]
    public string? CheckoutPath { get; init; }

    [JsonPropertyName("is_linked_worktree")]
    public bool IsLinkedWorktree { get; init; }
}

public sealed record PluginInvocationContextDto
{
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("workspace_label")]
    public string? WorkspaceLabel { get; init; }

    [JsonPropertyName("workspace_cwd")]
    public string? WorkspaceCwd { get; init; }

    [JsonPropertyName("worktree")]
    public PluginWorktreeContextDto? Worktree { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }

    [JsonPropertyName("tab_label")]
    public string? TabLabel { get; init; }

    [JsonPropertyName("focused_pane_id")]
    public string? FocusedPaneId { get; init; }

    [JsonPropertyName("focused_pane_cwd")]
    public string? FocusedPaneCwd { get; init; }

    [JsonPropertyName("focused_pane_agent")]
    public string? FocusedPaneAgent { get; init; }

    [JsonPropertyName("focused_pane_status")]
    public string? FocusedPaneStatus { get; init; }

    [JsonPropertyName("agent_session")]
    public NativeAgentSessionDto? AgentSession { get; init; }

    [JsonPropertyName("selected_text")]
    public string? SelectedText { get; init; }

    [JsonPropertyName("invocation_source")]
    public string? InvocationSource { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("program_name")]
    public string? ProgramName { get; init; }

    [JsonPropertyName("clicked_url")]
    public string? ClickedUrl { get; init; }

    [JsonPropertyName("link_handler_id")]
    public string? LinkHandlerId { get; init; }
}

public sealed record PluginTrustPreviewDto
{
    [JsonPropertyName("commands")]
    public IReadOnlyList<IReadOnlyList<string>>? Commands { get; init; }

    [JsonPropertyName("grants")]
    public IReadOnlyList<string>? Grants { get; init; }
}

public sealed record InstalledPluginDto
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("min_hypa_version")]
    public string? MinHypaVersion { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("manifest_path")]
    public string? ManifestPath { get; init; }

    [JsonPropertyName("plugin_root")]
    public string? PluginRoot { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("actions")]
    public IReadOnlyList<PluginManifestActionDto>? Actions { get; init; }

    [JsonPropertyName("events")]
    public IReadOnlyList<PluginManifestEventDto>? Events { get; init; }

    [JsonPropertyName("panes")]
    public IReadOnlyList<PluginManifestPaneDto>? Panes { get; init; }

    [JsonPropertyName("link_handlers")]
    public IReadOnlyList<PluginManifestLinkHandlerDto>? LinkHandlers { get; init; }

    [JsonPropertyName("startup")]
    public IReadOnlyList<PluginCommandSpecDto>? Startup { get; init; }

    [JsonPropertyName("resources")]
    public IReadOnlyList<PluginManifestResourceDto>? Resources { get; init; }

    [JsonPropertyName("menu_items")]
    public IReadOnlyList<PluginManifestMenuItemDto>? MenuItems { get; init; }

    [JsonPropertyName("settings_fields")]
    public IReadOnlyList<PluginManifestSettingsFieldDto>? SettingsFields { get; init; }

    [JsonPropertyName("grants")]
    public IReadOnlyList<string>? Grants { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; init; }
}

public sealed record PluginCommandSpecDto
{
    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record PluginManifestActionDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("contexts")]
    public IReadOnlyList<string>? Contexts { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }

    [JsonPropertyName("palette")]
    public bool Palette { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

public sealed record PluginManifestMenuItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("contexts")]
    public IReadOnlyList<string>? Contexts { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }
}

public sealed record PluginManifestEventDto
{
    [JsonPropertyName("on")]
    public string? On { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record PluginManifestPaneDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record PluginManifestLinkHandlerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }
}

public sealed record PluginActionInfoDto
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("contexts")]
    public IReadOnlyList<string>? Contexts { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("palette")]
    public bool Palette { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

public sealed record PluginCommandLogDto
{
    [JsonPropertyName("log_id")]
    public string? LogId { get; init; }

    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("action_id")]
    public string? ActionId { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("started_unix_ms")]
    public long StartedUnixMs { get; init; }

    [JsonPropertyName("finished_unix_ms")]
    public long? FinishedUnixMs { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("pid")]
    public int? Pid { get; init; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; init; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("result")]
    public PluginStructuredResultDto? Result { get; init; }
}

public sealed record PluginStructuredResultDto
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}

public sealed record PluginLinkResult
{
    [JsonPropertyName("plugin")]
    public InstalledPluginDto? Plugin { get; init; }

    [JsonPropertyName("trust_preview")]
    public PluginTrustPreviewDto? TrustPreview { get; init; }
}

public sealed record PluginListResult
{
    [JsonPropertyName("plugins")]
    public IReadOnlyList<InstalledPluginDto>? Plugins { get; init; }
}

public sealed record PluginUnlinkResultDto
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("removed")]
    public bool Removed { get; init; }
}

public sealed record PluginEnabledResult
{
    [JsonPropertyName("plugin")]
    public InstalledPluginDto? Plugin { get; init; }
}

public sealed record PluginActionListResult
{
    [JsonPropertyName("actions")]
    public IReadOnlyList<PluginActionInfoDto>? Actions { get; init; }
}

public sealed record PluginActionInvokeResultDto
{
    [JsonPropertyName("action")]
    public PluginActionInfoDto? Action { get; init; }

    [JsonPropertyName("context")]
    public PluginInvocationContextDto? Context { get; init; }

    [JsonPropertyName("log")]
    public PluginCommandLogDto? Log { get; init; }
}

public sealed record PluginLogListResult
{
    [JsonPropertyName("logs")]
    public IReadOnlyList<PluginCommandLogDto>? Logs { get; init; }
}

public sealed record PluginPaneInfoDto
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("entrypoint")]
    public string? Entrypoint { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }
}

public sealed record PluginPaneOpenedResult
{
    [JsonPropertyName("plugin_pane")]
    public PluginPaneInfoDto? PluginPane { get; init; }
}

public sealed record PluginPaneFocusedResult
{
    [JsonPropertyName("plugin_pane")]
    public PluginPaneInfoDto? PluginPane { get; init; }
}

public sealed record PluginPaneClosedResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}

public sealed record PluginPaneSendTextResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}
