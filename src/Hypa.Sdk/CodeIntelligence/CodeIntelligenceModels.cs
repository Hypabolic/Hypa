using System.Text.Json.Serialization;

namespace Hypa.Sdk.CodeIntelligence;

public sealed record CodeFileIdentity
{
    public required string ProjectRoot { get; init; }
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required string Language { get; init; }
    public required string ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GitBlobOid { get; init; }
    public long MTimeMs { get; init; }
}

public sealed record FileIndexState
{
    public required string AbsolutePath { get; init; }
    public string? GitBlobOid { get; init; }
    public required long MTimeMs { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record CodeStructureDocument
{
    public required CodeFileIdentity File { get; init; }
    public required ProviderProvenance Provenance { get; init; }

    /// <summary>Whether the parse gate accepted the file (tree-sitter parse without errors,
    /// or the markdown gate). False when extraction ran on an unvalidated file.</summary>
    public bool ParseGateValid { get; init; } = true;

    /// <summary>
    /// Artifact <c>parse.provider</c> value ("tree-sitter" | "markdown" | "none").
    /// Distinct from <see cref="Provenance"/> (which tracks extraction provenance on
    /// individual facts). Null on documents that predate schema v4 storage; loaders
    /// reconstruct a best-effort parse record from language + provenance.
    /// </summary>
    public string? ParseProvider { get; init; }

    public IReadOnlyList<CodeSymbol> Symbols { get; init; } = [];
    public IReadOnlyList<CodeReference> References { get; init; } = [];
    public IReadOnlyList<CodeDependencyEdge> DependencyEdges { get; init; } = [];
    public IReadOnlyList<CodeDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<MarkdownSection> Sections { get; init; } = [];
    public string? FrontmatterYaml { get; init; }
    public string? PlainText { get; init; }
}

public sealed record CodeSymbol
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Language { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? ParentId { get; init; }
    public required SourceSpan Span { get; init; }
    public required ProviderProvenance Provenance { get; init; }

    /// <summary>
    /// Effective language accessibility. C# declarations include language defaults when no
    /// keyword is present; TypeScript sets this for class/interface members. Omitted when the
    /// language construct has no accessibility concept.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessibility { get; init; }

    /// <summary>
    /// TypeScript module export status: <c>exported</c>, <c>default-exported</c>, or
    /// <c>not-exported</c>. Omitted for other languages.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExportStatus { get; init; }

    /// <summary>Surface-relevant declaration modifiers in source order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>
    /// Source-text signature fragment. Callable signatures contain parameters and, when
    /// declared, return type; typed fields/properties contain their declared type.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }
}

public sealed record CodeReference
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Kind { get; init; }
    public required string Target { get; init; }
    public required SourceSpan Span { get; init; }
    public required ProviderProvenance Provenance { get; init; }
}

public sealed record CodeDependencyEdge
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string Kind { get; init; }
    public SourceSpan? SourceSpan { get; init; }
    public string? TargetName { get; init; }
    public string TargetResolutionStatus { get; init; } = "unresolved";
    public required ProviderProvenance Provenance { get; init; }
}

public sealed record CodeDiagnostic
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public SourceSpan? Span { get; init; }
    public required ProviderProvenance Provenance { get; init; }
}

public sealed record SourceSpan
{
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public int StartByte { get; init; }
    public int EndByte { get; init; }
}

public sealed record ProviderProvenance
{
    public required string ProviderId { get; init; }
    public required string ProviderVersion { get; init; }
    public required string QueryVersion { get; init; }
    public required string FactKind { get; init; }
    public double Confidence { get; init; }
}

public sealed record CodeProviderHealth
{
    public required string ProviderId { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CodeIndexResult
{
    public int FilesIndexed { get; init; }
    public int FilesSkipped { get; init; }
    public int FilesDeleted { get; init; }
    public int SymbolCount { get; init; }
    public int ReferenceCount { get; init; }
    public int EdgeCount { get; init; }
    public int DiagnosticCount { get; init; }
    public IReadOnlyList<CodeProviderHealth> ProviderHealth { get; init; } = [];
}

public sealed record CodeSymbolQuery
{
    public string? Query { get; init; }
    public string? Path { get; init; }
    public string? Kind { get; init; }
}

public sealed record CodeGraphQuery
{
    public string? SymbolId { get; init; }
    public string? Path { get; init; }
    public int Depth { get; init; } = 1;
    public string? EdgeKind { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? References { get; init; }
    public string? Callers { get; init; }
    public string? Callees { get; init; }
}

public sealed record CodeGraphResult
{
    public IReadOnlyList<CodeSymbol> Symbols { get; init; } = [];
    public IReadOnlyList<CodeDependencyEdge> Edges { get; init; } = [];
    public IReadOnlyList<CodeReference> References { get; init; } = [];
}

public sealed record MarkdownSection
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string HeadingText { get; init; }
    public required int HeadingLevel { get; init; }
    public required string HeadingPath { get; init; }
    public required string HeadingAnchor { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required int StartByte { get; init; }
    public required int EndByte { get; init; }
    public string? Text { get; init; }
    public string? PlainText { get; init; }
    public required ProviderProvenance Provenance { get; init; }
}
