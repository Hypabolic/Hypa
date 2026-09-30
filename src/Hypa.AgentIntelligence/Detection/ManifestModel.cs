using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentIntelligence.Detection;

internal sealed record AgentManifestDocument
{
    public required string Id { get; init; }
    public string? Version { get; init; }
    public uint? MinEngineVersion { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required IReadOnlyList<ManifestRuleDocument> Rules { get; init; }
}

internal sealed record ManifestRuleDocument
{
    public required string Id { get; init; }
    public AgentStatus? State { get; init; }
    public int Priority { get; init; }
    public string Region { get; init; } = "whole_recent";
    public bool VisibleIdle { get; init; }
    public bool VisibleBlocker { get; init; }
    public bool VisibleWorking { get; init; }
    public bool SkipStateUpdate { get; init; }
    public required ManifestGateDocument Gate { get; init; }
}

internal sealed record ManifestGateDocument
{
    public IReadOnlyList<ManifestGateDocument> All { get; init; } = [];
    public IReadOnlyList<ManifestGateDocument> Any { get; init; } = [];
    public IReadOnlyList<ManifestGateDocument> Not { get; init; } = [];
    public IReadOnlyList<string> Contains { get; init; } = [];
    public IReadOnlyList<string> Regex { get; init; } = [];
    public IReadOnlyList<string> LineRegex { get; init; } = [];
}

internal sealed record CompiledManifest(
    AgentManifestDocument Manifest,
    IReadOnlyList<CompiledRule> Rules,
    ManifestOrigin Origin,
    string? Warning,
    string? CachedRemoteVersion,
    bool LocalOverrideShadowingRemote);

internal sealed record CompiledRule(ManifestRuleDocument Rule, CompiledGate Gate);

internal sealed record CompiledGate(
    IReadOnlyList<CompiledGate> All,
    IReadOnlyList<CompiledGate> Any,
    IReadOnlyList<CompiledGate> Not,
    IReadOnlyList<string> ContainsLower,
    IReadOnlyList<System.Text.RegularExpressions.Regex> Regex,
    IReadOnlyList<System.Text.RegularExpressions.Regex> LineRegex);

internal enum ManifestOriginKind
{
    Bundled,
    Remote,
    Override,
}

internal sealed record ManifestOrigin(ManifestOriginKind Kind, string Label, string? Path)
{
    public static ManifestOrigin Bundled { get; } = new(ManifestOriginKind.Bundled, "bundled", null);

    public static ManifestOrigin Remote(string path, string version) =>
        new(ManifestOriginKind.Remote, "remote:" + path, path);

    public static ManifestOrigin Override(string path) =>
        new(ManifestOriginKind.Override, path, path);

    public string SourceKind => Kind switch
    {
        ManifestOriginKind.Bundled => "bundled",
        ManifestOriginKind.Remote => "remote",
        ManifestOriginKind.Override => "local override",
        _ => "bundled",
    };
}

internal sealed record ManifestMatch(
    AgentStatus State,
    bool SkipStateUpdate,
    string? MatchedRuleId,
    string? FallbackReason,
    ManifestOrigin? Origin,
    string? ManifestVersion,
    string? CachedRemoteVersion,
    bool LocalOverrideShadowingRemote,
    string? Warning);
