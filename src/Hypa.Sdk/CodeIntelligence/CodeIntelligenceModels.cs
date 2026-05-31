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
}

public sealed record CodeStructureDocument
{
    public required CodeFileIdentity File { get; init; }
    public required ProviderProvenance Provenance { get; init; }
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
