using System.Text.Json.Serialization;

namespace Hypa.Sdk.CodeIntelligence;

public static class IndexArtifactSchema
{
    public const string Id = "hypa.code-index/2";
    public const string Version = "2.2";
}

public sealed record IndexArtifactHeader
{
    [JsonPropertyOrder(0)]
    public string Record { get; init; } = "header";

    [JsonPropertyOrder(1)]
    public string Schema { get; init; } = IndexArtifactSchema.Id;

    [JsonPropertyOrder(2)]
    public string SchemaVersion { get; init; } = IndexArtifactSchema.Version;

    [JsonPropertyOrder(3)]
    public required IndexArtifactGenerator Generator { get; init; }

    [JsonPropertyOrder(4)]
    public required IndexArtifactWorkspace Workspace { get; init; }

    [JsonPropertyOrder(5)]
    public string Mode { get; init; } = "full";
}

public sealed record IndexArtifactGenerator
{
    [JsonPropertyOrder(0)]
    public string Binary { get; init; } = "hypa";

    [JsonPropertyOrder(1)]
    public required string BinaryVersion { get; init; }

    [JsonPropertyOrder(2)]
    public IReadOnlyList<IndexArtifactProvider> Providers { get; init; } = [];
}

public sealed record IndexArtifactProvider
{
    [JsonPropertyOrder(0)]
    public required string ProviderId { get; init; }

    [JsonPropertyOrder(1)]
    public required string ProviderVersion { get; init; }

    [JsonPropertyOrder(2)]
    public required string QueryVersion { get; init; }
}

public sealed record IndexArtifactWorkspace
{
    [JsonPropertyOrder(0)]
    public string? CommitSha { get; init; }

    [JsonPropertyOrder(1)]
    public bool GitDirty { get; init; }

    [JsonPropertyOrder(2)]
    public IReadOnlyList<string> Languages { get; init; } = [];
}

public sealed record IndexFileRecord
{
    [JsonPropertyOrder(0)]
    public string Record { get; init; } = "file";

    [JsonPropertyOrder(1)]
    public required ArtifactFileIdentity File { get; init; }

    [JsonPropertyOrder(2)]
    public string Status { get; init; } = "indexed";

    [JsonPropertyOrder(3)]
    public required IndexArtifactParse Parse { get; init; }

    [JsonPropertyOrder(4)]
    public IReadOnlyList<CodeSymbol> Symbols { get; init; } = [];

    [JsonPropertyOrder(5)]
    public IReadOnlyList<CodeReference> References { get; init; } = [];

    [JsonPropertyOrder(6)]
    public IReadOnlyList<CodeDependencyEdge> DependencyEdges { get; init; } = [];

    [JsonPropertyOrder(7)]
    public IReadOnlyList<CodeDiagnostic> Diagnostics { get; init; } = [];

    [JsonPropertyOrder(8)]
    public IReadOnlyList<ArtifactMarkdownSection> Sections { get; init; } = [];

    [JsonPropertyOrder(9)]
    public string? FrontmatterYaml { get; init; }
}

public sealed record ArtifactFileIdentity
{
    [JsonPropertyOrder(0)]
    public required string RelativePath { get; init; }

    [JsonPropertyOrder(1)]
    public required string Language { get; init; }

    [JsonPropertyOrder(2)]
    public required string ContentHash { get; init; }

    [JsonPropertyOrder(3)]
    public long SizeBytes { get; init; }

    [JsonPropertyOrder(4)]
    public string? GitBlobOid { get; init; }
}

public sealed record IndexArtifactParse
{
    [JsonPropertyOrder(0)]
    public required string Provider { get; init; }

    [JsonPropertyOrder(1)]
    public bool Valid { get; init; }
}

public sealed record ArtifactMarkdownSection
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required string FilePath { get; init; }

    [JsonPropertyOrder(2)]
    public required string HeadingText { get; init; }

    [JsonPropertyOrder(3)]
    public required int HeadingLevel { get; init; }

    [JsonPropertyOrder(4)]
    public required string HeadingPath { get; init; }

    [JsonPropertyOrder(5)]
    public required string HeadingAnchor { get; init; }

    [JsonPropertyOrder(6)]
    public required int StartLine { get; init; }

    [JsonPropertyOrder(7)]
    public required int EndLine { get; init; }

    [JsonPropertyOrder(8)]
    public required int StartByte { get; init; }

    [JsonPropertyOrder(9)]
    public required int EndByte { get; init; }

    [JsonPropertyOrder(10)]
    public required ProviderProvenance Provenance { get; init; }
}

public sealed record IndexArtifactFooter
{
    [JsonPropertyOrder(0)]
    public string Record { get; init; } = "footer";

    [JsonPropertyOrder(1)]
    public required IndexArtifactCounts Counts { get; init; }

    [JsonPropertyOrder(2)]
    public IReadOnlyList<IndexArtifactSkippedFile> SkippedFiles { get; init; } = [];

    [JsonPropertyOrder(3)]
    public IReadOnlyList<IndexArtifactProviderHealth> ProviderHealth { get; init; } = [];

    [JsonPropertyOrder(4)]
    public required string Completeness { get; init; }

    /// <summary>
    /// Number of files dropped by the code-graph noise policy (lockfiles, generated,
    /// vendored, minified, config-data). Present only under the <c>code-graph</c>
    /// export profile; <see langword="null"/> — and therefore omitted from the
    /// serialized artifact — under the default <c>full</c> profile, so the full
    /// artifact shape is unchanged. Makes otherwise-silent policy drops observable,
    /// so a false-positive exclusion is detectable rather than invisible.
    /// </summary>
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PolicyExcluded { get; init; }

    /// <summary>
    /// Project-wide indexed-file count after the update. Present only in incremental
    /// mode; omitted (<see langword="null"/>) for full exports so full-mode shape stays
    /// additive-compatible except for the schema id/version bump.
    /// </summary>
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalFiles { get; init; }
}

public sealed record IndexArtifactCounts
{
    [JsonPropertyOrder(0)]
    public int Files { get; init; }

    [JsonPropertyOrder(1)]
    public int Symbols { get; init; }

    [JsonPropertyOrder(2)]
    public int References { get; init; }

    [JsonPropertyOrder(3)]
    public int DependencyEdges { get; init; }

    [JsonPropertyOrder(4)]
    public int Diagnostics { get; init; }

    [JsonPropertyOrder(5)]
    public int Sections { get; init; }
}

public sealed record IndexArtifactSkippedFile
{
    [JsonPropertyOrder(0)]
    public required string RelativePath { get; init; }

    [JsonPropertyOrder(1)]
    public required string Reason { get; init; }
}

public sealed record IndexArtifactProviderHealth
{
    [JsonPropertyOrder(0)]
    public required string ProviderId { get; init; }

    [JsonPropertyOrder(1)]
    public required string Status { get; init; }

    [JsonPropertyOrder(2)]
    public required string Message { get; init; }
}

public sealed record IndexArtifactDocument
{
    [JsonPropertyOrder(0)]
    public required IndexArtifactHeader Header { get; init; }

    [JsonPropertyOrder(1)]
    public IReadOnlyList<IndexFileRecord> Files { get; init; } = [];

    [JsonPropertyOrder(2)]
    public required IndexArtifactFooter Footer { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IndexArtifactHeader))]
[JsonSerializable(typeof(IndexFileRecord))]
[JsonSerializable(typeof(IndexArtifactFooter))]
[JsonSerializable(typeof(IndexArtifactDocument))]
[JsonSerializable(typeof(IReadOnlyList<IndexFileRecord>))]
public sealed partial class IndexArtifactJsonContext : JsonSerializerContext;
