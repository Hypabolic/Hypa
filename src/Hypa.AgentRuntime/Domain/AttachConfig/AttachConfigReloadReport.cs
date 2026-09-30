namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Result of one attach-config reload. Domain has no IO.</summary>
public enum ConfigReloadStatus
{
    Applied,
    Failed,
    Partial,
}

/// <summary>One bind of config.toml. Applied swaps Current. Failed keeps Current.</summary>
public sealed record AttachConfigReloadReport
{
    public required ConfigReloadStatus Status { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public static AttachConfigReloadReport Applied(IReadOnlyList<string>? diagnostics = null) =>
        new()
        {
            Status = ConfigReloadStatus.Applied,
            Diagnostics = diagnostics ?? [],
        };

    public static AttachConfigReloadReport Failed(IReadOnlyList<string> diagnostics) =>
        new()
        {
            Status = ConfigReloadStatus.Failed,
            Diagnostics = diagnostics,
        };

    public static AttachConfigReloadReport Partial(IReadOnlyList<string> diagnostics) =>
        new()
        {
            Status = ConfigReloadStatus.Partial,
            Diagnostics = diagnostics,
        };

    /// <summary>Schema-lock token. Live RPC must use <see cref="ToLiveWireStatus"/>.</summary>
    public string ToWireStatus() => Status switch
    {
        ConfigReloadStatus.Applied => "applied",
        ConfigReloadStatus.Partial => "partial",
        _ => "failed",
    };

    /// <summary>Live <c>server.reload_config</c> tokens. Partial is failed. Hypa does not emit partial.</summary>
    public string ToLiveWireStatus() =>
        Status == ConfigReloadStatus.Applied ? "applied" : "failed";
}
