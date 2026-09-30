namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Cached agent-detection manifests. Local override wins. Reload does not
/// restart panes.
/// </summary>
public interface IAgentManifestCatalog
{
    IReadOnlyList<AgentManifestSummary> ListSummaries();

    IReadOnlyList<AgentManifestSummary> Reload();

    long? LastCheckUnix { get; }

    string? LastResult { get; }

    void CheckRemoteUpdates(bool enabled);
}

public sealed record AgentManifestSummary
{
    public required string Agent { get; init; }
    public required string Source { get; init; }
    public required string SourceKind { get; init; }
    public string? ActiveVersion { get; init; }
    public string? CachedRemoteVersion { get; init; }
    public bool LocalOverrideShadowingRemote { get; init; }
    public string? RemoteUpdateResult { get; init; }
    public string? RemoteUpdateError { get; init; }
    public long? RemoteLastCheckedUnix { get; init; }
    public string? Warning { get; init; }
}

/// <summary>HTTP (or test) fetch of a remote catalog or manifest body.</summary>
public interface IAgentManifestTextFetcher
{
    AgentManifestFetchResult Fetch(string url, int maxBytes);
}

public sealed record AgentManifestFetchResult(bool Ok, string Body, string? Error);
