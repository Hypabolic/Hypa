# Code Intelligence

Hypa's code intelligence layer builds a local code map that agents can query without repeatedly reading large files or rediscovering project structure.

The layer should support fast syntax outlines, semantic C# analysis, optional language server enrichment, and eventually graph-backed retrieval.

## Goals

- Provide compact code maps for agent context.
- Support symbol, reference, and dependency graph queries.
- Reduce repeated file reads.
- Preserve provenance and confidence for every fact.
- Work without requiring editor-specific configuration.
- Use language servers opportunistically when available and trusted.

## Provider Strategy

Hypa uses multiple providers behind a stable interface:

```text
RoslynProvider
  compiler-grade C#/.NET analysis

TreeSitterProvider
  fast cross-language syntax structure through TreeSitter.DotNet

LanguageServerProvider
  optional semantic enrichment from installed/trusted LSP servers

RegexFallbackProvider
  degraded mode for unsupported languages or failures
```

Provider output must be normalised into Hypa-owned DTOs. Downstream graph and memory code should not depend directly on Roslyn symbols, Tree-sitter nodes, or LSP result shapes.

## Provider Interface

```csharp
public interface ICodeStructureProvider
{
    string ProviderId { get; }

    bool Supports(CodeFileIdentity file);

    Task<ProviderHealth> ProbeAsync(
        ProjectContext project,
        CancellationToken cancellationToken);

    Task<CodeStructureDocument> ParseAsync(
        CodeFileIdentity file,
        string content,
        CodeStructureOptions options,
        CancellationToken cancellationToken);
}
```

## Normalised Output

```csharp
public sealed record CodeStructureDocument(
    CodeFileIdentity File,
    IReadOnlyList<CodeSymbol> Symbols,
    IReadOnlyList<CodeReference> References,
    IReadOnlyList<CodeDependencyEdge> Edges,
    IReadOnlyList<CodeDiagnostic> Diagnostics,
    CodeIntelligenceProvenance Provenance);
```

Every record should include source span and provenance.

## C# and Roslyn

For C#, Roslyn should be the primary semantic provider.

Use Roslyn for:

- symbol identity;
- project and solution context;
- references;
- interface implementations;
- partial classes;
- overloads;
- attributes;
- nullable annotations;
- compiler diagnostics;
- dependency graph construction.

Tree-sitter may still be used for fast syntax-only outline extraction, but Roslyn wins where semantic facts conflict.

## TreeSitter.DotNet

TreeSitter.DotNet should be used as the initial in-process Tree-sitter implementation.

Use it for:

- fast syntax outlines;
- non-C# language support;
- symbol spans;
- import/declaration extraction;
- AST-aware pruning;
- map-mode file reads.

Implementation pattern:

```text
extension -> language name
extension -> query string
parse source
run query
capture @def and @name
map AST capture -> CodeSymbol
```

Initial query categories:

```text
signatures
imports
calls, later
classes/types
methods/functions
exports
```

The initial implementation should support at least:

```text
cs, ts, tsx, js, jsx, py, go, rs, java, c, cpp, sh, json, yaml/toml where available
```

## Language Server Enrichment

Language servers are optional enrichment providers.

Use them for:

- references;
- definitions;
- implementations;
- type definitions;
- diagnostics;
- hover/type documentation;
- workspace symbols.

They should not be required for baseline indexing.

Language server facts should be merged into existing symbols and edges using provenance:

```text
provider = lsp:rust-analyzer
capability = references
confidence = semantic
```

## Graph Construction

The graph should be built from normalised facts.

Recommended node types:

```text
Project
File
Namespace
Type
Method
Function
Property
Field
Interface
Test
Package
ExternalSymbol
```

Recommended edge types:

```text
contains
imports
references
calls
implements
inherits
overrides
uses-package
tests
configured-by
```

The graph should tolerate incomplete information. Syntax-derived edges should not be presented as compiler-grade facts unless enriched by Roslyn or LSP.

## Code Map Read Modes

Hypa should expose different read modes for different context budgets:

```text
full
  raw file content

outline
  top-level symbols and imports

signatures
  symbols with parameters and return types

pruned
  imports + signatures + body placeholders

graph
  relevant local neighbourhood of symbols and edges

smart
  choose mode based on file size, cache state, and active task
```

These modes are critical for reducing repeated file reads.

## Cache Strategy

Cache by content hash and provider version:

```text
file path
content hash
language
provider id
provider version
query version
indexed at
```

If the content hash and provider/query versions match, return cached code structure.

## Merge Strategy

Provider precedence:

```text
Roslyn semantic facts > LSP semantic facts > Tree-sitter syntactic facts > Regex heuristic facts
```

But do not blindly discard lower-confidence facts. Keep them if they cover different languages or unsupported constructs.

Conflicts should be visible in diagnostics if they affect graph quality.

## Diagnostics

The code intelligence layer should expose health:

```bash
hypa doctor code-intelligence
```

Example output:

```text
C#:
  Roslyn: available
  Tree-sitter: available
  LSP: csharp-ls not found

TypeScript:
  Tree-sitter: available
  LSP: typescript-language-server found, healthy

Rust:
  Tree-sitter: available
  LSP: rust-analyzer found, healthy
```

## Testing Strategy

Use fixture projects for:

- C# solution with multiple projects;
- TypeScript project;
- mixed frontend/backend repository;
- failing/incomplete code;
- large files;
- generated files ignored by policy.

Test assertions should cover:

- stable symbol IDs;
- source spans;
- provider provenance;
- cache invalidation;
- graph edges;
- graceful provider failure.

## Future Work

- Incremental indexing.
- Persistent graph store.
- Semantic search over code symbols and docs.
- Call graph extraction.
- Test-to-code mapping.
- Atomic memory promotion from stable code facts.
- LSP-backed refactoring safety checks.
