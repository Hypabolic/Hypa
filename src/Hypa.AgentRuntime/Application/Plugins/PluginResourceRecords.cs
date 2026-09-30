using Hypa.AgentRuntime.Application.Metadata;

namespace Hypa.AgentRuntime.Application.Plugins;

public sealed record PluginManifestResource
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Projection { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<string>? Platforms { get; init; }

    public IReadOnlyList<string>? Command { get; init; }
}

public sealed record PluginResourceTarget
{
    public string? PaneId { get; init; }

    public string? WorkspaceId { get; init; }

    public string? TabId { get; init; }
}

public sealed record PluginCollectionItem
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public required string Status { get; init; }

    public long Attention { get; init; }

    public IReadOnlyDictionary<string, string> Tokens { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Action { get; init; }

    public PluginResourceTarget? Target { get; init; }

    public long? Sequence { get; init; }

    public int? TtlMs { get; init; }
}

public sealed record PluginCollectionValue
{
    public string? Summary { get; init; }

    public IReadOnlyList<PluginCollectionItem> Items { get; init; } = [];
}

public sealed record PluginResourceRecord
{
    public required PluginResourceId Id { get; init; }

    public required string Schema { get; init; }

    public required long Revision { get; init; }

    public required string Freshness { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required PluginCollectionValue Value { get; init; }
}

public sealed record PluginResourcePublishRequest
{
    public required PluginResourceId Id { get; init; }

    public required string Schema { get; init; }

    public required long Revision { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required PluginCollectionValue Value { get; init; }

    public int PublishBytes { get; init; }
}

public sealed record PluginResourcePublishOutcome
{
    public required bool Ignored { get; init; }

    public required PluginResourceRecord Resource { get; init; }
}

public static class PluginItemId
{
    public static bool TryNormalize(string? value, out string id)
    {
        id = "";
        if (value is null)
            return false;
        var trimmed = value.Trim();
        if (trimmed.Length is < MetadataTokenLimits.MinKeyLength
            or > PluginCommandLimits.IdentifierMaxChars)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':')
                continue;
            return false;
        }

        id = trimmed;
        return true;
    }
}
