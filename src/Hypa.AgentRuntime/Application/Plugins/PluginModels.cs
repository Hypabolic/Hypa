using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginCommandLimits
{
    public const int MaxServerInFlight = 32;

    public const int MaxPerPluginInFlight = 8;

    public const int OutputMaxBytes = 64 * 1024;

    public const int LogLimit = 200;

    public const int ContextJsonMaxBytes = 16 * 1024;

    public const int FilterListMax = 16;

    public const int IdentifierMaxChars = 120;

    public static readonly TimeSpan GrantTokenTtl = TimeSpan.FromHours(12);
}

public sealed record PluginManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string MinHypaVersion { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public IReadOnlyList<PluginCommandSpec> Build { get; init; } = [];

    public IReadOnlyList<PluginCommandSpec> Startup { get; init; } = [];

    public IReadOnlyList<PluginManifestAction> Actions { get; init; } = [];

    public IReadOnlyList<PluginManifestEventHook> Events { get; init; } = [];

    public IReadOnlyList<PluginManifestPane> Panes { get; init; } = [];

    public IReadOnlyList<PluginManifestLinkHandler> LinkHandlers { get; init; } = [];

    public IReadOnlyList<PluginManifestResource> Resources { get; init; } = [];

    public IReadOnlyList<PluginManifestMenuItem> MenuItems { get; init; } = [];

    public IReadOnlyList<PluginManifestSettingsField> SettingsFields { get; init; } = [];

    public IReadOnlyList<PluginManifestDoctor> Doctor { get; init; } = [];

    public IReadOnlyList<string> RequestedGrants { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record PluginCommandSpec
{
    public IReadOnlyList<string>? Platforms { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
}

public sealed record PluginManifestDoctor
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
}

public sealed record PluginManifestAction
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public IReadOnlyList<string>? Platforms { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    public bool Palette { get; init; }

    /// <summary>Advisory only. Never compiled into attach key tables.</summary>
    public string? SuggestedKey { get; init; }
}

public sealed record PluginManifestMenuItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public required string Action { get; init; }
}

public sealed record PluginChromeMenuEntry
{
    public required string PluginId { get; init; }

    public required string ItemId { get; init; }

    public required string Title { get; init; }

    public required string ActionId { get; init; }

    public required string QualifiedActionId { get; init; }

    public required string Context { get; init; }
}

public sealed record PluginChromePaletteEntry
{
    public required string PluginId { get; init; }

    public required string ActionId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required string QualifiedActionId { get; init; }
}

public sealed record PluginManifestEventHook
{
    public required string On { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    public PluginEventFilter? Filter { get; init; }
}

public sealed record PluginEventFilter
{
    public IReadOnlyList<string>? Workspace { get; init; }

    public IReadOnlyList<string>? Pane { get; init; }

    public IReadOnlyList<string>? Status { get; init; }

    public IReadOnlyList<string>? Agent { get; init; }
}

public sealed record PluginManifestPane
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public string Placement { get; init; } = "overlay";

    public string? Width { get; init; }

    public string? Height { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
}

/// <summary>
// Keep manifest order.
/// Do not sort by id. Match order uses this list after plugin-id sort.
/// </summary>
public sealed record PluginManifestLinkHandler
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Pattern { get; init; }

    public required string Action { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }
}

public sealed record InstalledPlugin
{
    public required string PluginId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string MinHypaVersion { get; init; }

    public string? Description { get; init; }

    public required string ManifestPath { get; init; }

    public required string PluginRoot { get; init; }

    public required bool Enabled { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public IReadOnlyList<PluginCommandSpec> Build { get; init; } = [];

    public IReadOnlyList<PluginCommandSpec> Startup { get; init; } = [];

    public IReadOnlyList<PluginManifestAction> Actions { get; init; } = [];

    public IReadOnlyList<PluginManifestEventHook> Events { get; init; } = [];

    public IReadOnlyList<PluginManifestPane> Panes { get; init; } = [];

    public IReadOnlyList<PluginManifestLinkHandler> LinkHandlers { get; init; } = [];

    public IReadOnlyList<PluginManifestResource> Resources { get; init; } = [];

    public IReadOnlyList<PluginManifestMenuItem> MenuItems { get; init; } = [];

    public IReadOnlyList<PluginManifestSettingsField> SettingsFields { get; init; } = [];

    public IReadOnlyList<PluginManifestDoctor> Doctor { get; init; } = [];

    public IReadOnlyList<string> RequestedGrants { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public long EnableGeneration { get; init; }

    public string SourceKind { get; init; } = "local";

    /// <summary>
    /// Registry JSON omits empty lists. Source-generated deserialize then
    /// leaves those properties null. Normalize before host iteration.
    /// </summary>
    public InstalledPlugin WithNonNullCollections() => this with
    {
        Build = Build ?? [],
        Startup = Startup ?? [],
        Actions = Actions ?? [],
        Events = Events ?? [],
        Panes = Panes ?? [],
        LinkHandlers = LinkHandlers ?? [],
        Resources = Resources ?? [],
        MenuItems = MenuItems ?? [],
        SettingsFields = SettingsFields ?? [],
        Doctor = Doctor ?? [],
        RequestedGrants = RequestedGrants ?? [],
        Warnings = Warnings ?? [],
    };
}

public sealed record PluginTrustPreview
{
    public required IReadOnlyList<IReadOnlyList<string>> Commands { get; init; }

    public required IReadOnlyList<string> Grants { get; init; }
}

public sealed record PluginLinkResult
{
    public required InstalledPlugin Plugin { get; init; }

    public required PluginTrustPreview TrustPreview { get; init; }
}

public sealed record PluginUnlinkResult
{
    public required string PluginId { get; init; }

    public required bool Removed { get; init; }

    public IReadOnlyList<string> ClosedPaneIds { get; init; } = [];

    public bool ClosedPopup { get; init; }
}

public sealed record PluginEnableResult
{
    public required InstalledPlugin Plugin { get; init; }

    public IReadOnlyList<string> ClosedPaneIds { get; init; } = [];

    public bool ClosedPopup { get; init; }
}

public sealed record PluginActionInfo
{
    public required string PluginId { get; init; }

    public required string ActionId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public required IReadOnlyList<string> Command { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public bool Palette { get; init; }

    public string? SuggestedKey { get; init; }

    public string QualifiedId => PluginId + "." + ActionId;
}

/// <summary>
// / Nested worktree identity for plugin context.
/// <c>src/api/schema/workspaces.rs:79-85</c> <c>WorkspaceWorktreeInfo</c>.
/// </summary>
public sealed record PluginWorktreeContext
{
    public required string RepoKey { get; init; }

    public required string RepoName { get; init; }

    public required string RepoRoot { get; init; }

    public required string CheckoutPath { get; init; }

    public required bool IsLinkedWorktree { get; init; }
}

public sealed record PluginInvocationContext
{
    public string? WorkspaceId { get; init; }

    public string? WorkspaceLabel { get; init; }

    public string? WorkspaceCwd { get; init; }

    /// <summary>Target workspace membership. Null when the workspace has none.</summary>
    public PluginWorktreeContext? Worktree { get; init; }

    public string? TabId { get; init; }

    public string? TabLabel { get; init; }

    public string? FocusedPaneId { get; init; }

    public string? FocusedPaneCwd { get; init; }

    public string? FocusedPaneAgent { get; init; }

    public string? FocusedPaneStatus { get; init; }

    /// <summary>Native session reference for the focused pane. Null when absent.</summary>
    public NativeAgentSessionRef? AgentSession { get; init; }

    public string? SelectedText { get; init; }

    public string? InvocationSource { get; init; }

    public string? CorrelationId { get; init; }

    public string? ProgramName { get; init; }

    public string? ClickedUrl { get; init; }

    public string? LinkHandlerId { get; init; }
}

public sealed record PluginCommandLog
{
    public required string LogId { get; init; }

    public required string PluginId { get; init; }

    public string? ActionId { get; init; }

    public string? Event { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    public required string Status { get; init; }

    public required long StartedUnixMs { get; init; }

    public long? FinishedUnixMs { get; init; }

    public int? ExitCode { get; init; }

    public int? Pid { get; init; }

    public string? Stdout { get; init; }

    public string? Stderr { get; init; }

    public string? Error { get; init; }

    public PluginStructuredActionResult? Result { get; init; }
}

public sealed record PluginStructuredActionResult
{
    public required string Status { get; init; }

    public string? Message { get; init; }

    public string? DataJson { get; init; }
}

public sealed record PluginActionInvokeResult
{
    public required PluginActionInfo Action { get; init; }

    public required PluginInvocationContext Context { get; init; }

    public required PluginCommandLog Log { get; init; }
}

public sealed record PluginPaneOpenPlan
{
    public required string PluginId { get; init; }

    public required string Entrypoint { get; init; }

    public required string Title { get; init; }

    public required string Placement { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    public required string Cwd { get; init; }

    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public string? Width { get; init; }

    public string? Height { get; init; }
}

public sealed record PluginGrantDecision
{
    public required bool Allowed { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public string? PluginId { get; init; }

    public static PluginGrantDecision Allow(string? pluginId = null) =>
        new() { Allowed = true, PluginId = pluginId };

    public static PluginGrantDecision Deny(string errorCode, string? message = null) =>
        new()
        {
            Allowed = false,
            ErrorCode = errorCode,
            Message = message ?? errorCode,
        };
}
