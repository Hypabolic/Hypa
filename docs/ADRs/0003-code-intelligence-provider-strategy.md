# ADR 0003: Code Intelligence Provider Strategy

## Status

Accepted

## Date

2026-05-06

## Context

Hypa needs to build useful code maps for agentic development workflows. The runtime should support codebase indexing, context reduction, symbol discovery, dependency graph construction, and retrieval for agent tool calls.

No single parser or analysis engine is the right choice for every language and depth of analysis:

- Roslyn is the strongest option for C# and .NET semantic analysis.
- Tree-sitter is a strong cross-language syntax parser and can provide fast outlines and structural spans.
- Language servers can provide semantic project intelligence where available, but are optional, stateful, and inconsistent across ecosystems.
- Regex-only parsing is useful as a fallback but should not be the primary strategy.

The runtime must remain implementation-flexible. Code graph and memory layers should consume Hypa-owned DTOs rather than depending directly on Roslyn, Tree-sitter, or LSP models.

## Decision

Hypa will use a provider-based code intelligence strategy.

The baseline providers are:

1. Roslyn for C# and .NET semantic analysis.
2. TreeSitter.DotNet for .NET-hosted, cross-language syntax parsing.
3. Optional language server enrichment where a trusted and healthy language server is available.
4. Regex/text fallback for unsupported languages or degraded environments.

TreeSitter.DotNet is preferred over a Rust sidecar for the initial .NET implementation because it keeps parsing in-process and ships native Tree-sitter runtime and grammar libraries via NuGet for supported platforms.

The code graph will depend on Hypa's own normalized model, not on provider-specific AST or symbol types.

## Consequences

### Positive

- C# analysis can use compiler-grade Roslyn semantics.
- Non-C# analysis can use Tree-sitter without introducing a Rust sidecar at MVP stage.
- The graph model remains stable if a provider is swapped later.
- Optional LSP enrichment can improve references, definitions, diagnostics, and implementation edges.
- The runtime can still index unsupported languages at a lower fidelity.

### Negative / Trade-offs

- Multiple providers require merge logic and provenance tracking.
- TreeSitter.DotNet still depends on native libraries, even though it is .NET-hosted.
- Roslyn and Tree-sitter may produce overlapping but non-identical views of C# files.
- Provider-specific bugs must be isolated behind capability checks and health reporting.
- Single-file and NativeAOT deployment require explicit validation of native asset loading.

## Implementation Notes

Recommended abstraction:

```csharp
public interface ICodeStructureProvider
{
    string ProviderId { get; }

    bool Supports(CodeFileIdentity file);

    Task<CodeStructureDocument> ParseAsync(
        CodeFileIdentity file,
        string content,
        CodeStructureOptions options,
        CancellationToken cancellationToken);
}
```

Recommended providers:

```text
CSharpRoslynStructureProvider
TreeSitterStructureProvider
LanguageServerStructureProvider
RegexFallbackStructureProvider
```

Recommended normalized DTOs:

```csharp
public sealed record CodeStructureDocument(
    CodeFileIdentity File,
    IReadOnlyList<CodeSymbol> Symbols,
    IReadOnlyList<CodeReference> References,
    IReadOnlyList<CodeDependencyEdge> Edges,
    IReadOnlyList<CodeDiagnostic> Diagnostics,
    CodeIntelligenceProvenance Provenance);

public sealed record CodeSymbol(
    string StableId,
    string Kind,
    string Name,
    string? ContainerName,
    string? ReturnType,
    string? Parameters,
    SourceSpan Span,
    SymbolVisibility Visibility,
    CodeIntelligenceProvenance Provenance);
```

Provider precedence should be explicit:

```text
C# semantic symbols/references: Roslyn wins.
C# fast outline: Roslyn or Tree-sitter may be used, but output must normalize to the same DTO.
Non-C# syntax symbols: Tree-sitter wins.
Definitions/references/diagnostics from healthy LSP providers enrich existing symbols and edges.
Regex fallback is lowest confidence.
```

Every fact emitted by a provider should carry provenance:

```text
provider = roslyn | tree-sitter | lsp | regex
providerVersion?
language?
confidence = semantic | syntactic | heuristic
sourceFile
span
```

Tree-sitter implementation should follow a query-registry model similar to:

```text
extension -> language name
extension -> signature query
query captures @def and @name
AST node + capture -> CodeSymbol
```

TreeSitter.DotNet should be validated for:

- Windows x64/arm64
- Linux x64/arm64
- macOS x64/arm64
- normal framework-dependent publish
- self-contained publish
- single-file publish if planned
- NativeAOT only if the CLI/runtime requires it

A Rust sidecar remains an escape hatch if TreeSitter.DotNet proves unreliable for native asset loading, grammar coverage, performance, or deployment.

## Alternatives Considered

### Rust Tree-sitter sidecar as the default

Deferred. It is technically strong and may be useful later, but it adds a second runtime and process boundary. TreeSitter.DotNet should be tried first to keep the MVP simpler from a .NET product perspective.

### Tree-sitter for C# semantics

Rejected. Tree-sitter is excellent for syntax, but Roslyn is the correct tool for C# semantic identity, references, partial classes, generics, attributes, nullable annotations, project context, and compiler diagnostics.

### Roslyn only

Rejected. Hypa needs multi-language indexing and context reduction. Roslyn only covers .NET languages.

### Regex-only indexing

Rejected as a primary strategy. It is too brittle for code graph construction, but remains useful as a last-resort fallback.

## Related Decisions

- ADR 0001: Local Context Runtime Operating Model
- ADR 0004: Optional Language Server Enrichment
