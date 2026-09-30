namespace Hypa.AgentRuntime.Application.Metadata;

/// <summary>Single resource-agnostic display-token contract used by workspaces, panes, and future resources.</summary>
public static class MetadataTokenLimits
{
    public const int MinKeyLength = 1;
    public const int MaxKeyLength = 32;
    public const int MaxValueLength = 80;
    public const int MaxKeysPerReport = 16;
    public const int MaxRetainedKeys = 32;
    public const int MaxSequencedSources = 32;
    public const int MinSourceLength = 1;
    public const int MaxSourceLength = 80;
    public const int MinTtlMilliseconds = 1;
    public const int MaxTtlMilliseconds = 86_400_000;
}
