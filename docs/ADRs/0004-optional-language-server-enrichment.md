# ADR 0004: Optional Language Server Enrichment

## Status

Accepted

## Date

2026-05-06

## Context

Language servers can provide editor-like project intelligence: document symbols, definitions, references, implementations, hover information, semantic tokens, and diagnostics.

This information can improve Hypa's code graph and agent context retrieval, especially for languages where Hypa does not have a compiler-grade provider. However, language servers are optional tools installed on the user's machine. They are stateful processes, vary in quality, may be slow to initialize, and can execute local project/toolchain code depending on the server and language ecosystem.

Hypa should benefit from language servers where they are available and trusted, but must not require them for baseline indexing.

## Decision

Hypa will support language server discovery and probing as an optional enrichment layer.

Language server output will enrich, not replace, the baseline code intelligence produced by Roslyn, Tree-sitter, and fallback providers.

The runtime will:

1. discover candidate language servers from PATH, known install locations, and cheap project-local signals;
2. probe candidates by starting the server and issuing `initialize`;
3. inspect advertised LSP capabilities;
4. cache provider health and capabilities per project/environment;
5. use only trusted or explicitly enabled servers for analysis;
6. attach provenance to all facts derived from LSP.

## Consequences

### Positive

- Hypa can produce stronger reference, definition, implementation, and diagnostic edges when the user's development environment already has suitable language servers.
- Baseline indexing still works with no language servers installed.
- Language server capability information can be exposed through diagnostics and agent-readable status tools.
- Trust and health checks reduce the risk of silently relying on broken or hostile tools.

### Negative / Trade-offs

- LSP clients require JSON-RPC framing, lifecycle management, request correlation, timeouts, and process cleanup.
- Some servers are slow, fragile, or require project-specific initialization.
- Capability names are standardised, but behaviour and quality vary materially by server.
- Running local language servers has a security and trust dimension because they are executables on the user's machine.
- Indexing results become multi-source and require conflict resolution.

## Implementation Notes

Recommended candidate model:

```csharp
public sealed record LanguageServerCandidate(
    string Id,
    string DisplayName,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> FileExtensions,
    IReadOnlySet<string> ExpectedCapabilities,
    int Confidence,
    string? Version,
    string? Source);
```

Recommended health model:

```csharp
public sealed record LanguageServerHealth(
    string Id,
    bool Found,
    bool Trusted,
    bool InitializeSucceeded,
    IReadOnlySet<string> AdvertisedCapabilities,
    string? Version,
    string? FailureReason,
    DateTimeOffset CheckedAt);
```

Useful LSP requests for Hypa:

```text
textDocument/documentSymbol
textDocument/definition
textDocument/references
textDocument/implementation
textDocument/typeDefinition
textDocument/hover
textDocument/semanticTokens/full
workspace/symbol
workspace/diagnostic
textDocument/publishDiagnostics
```

Initial language server candidates:

```text
C#          csharp-ls, omnisharp
TypeScript  typescript-language-server, tsserver
Python      pyright-langserver, basedpyright-langserver, pylsp
Go          gopls
Rust        rust-analyzer
Java        jdtls
PHP         intelephense, phpactor
Ruby        ruby-lsp, solargraph
Terraform   terraform-ls
YAML        yaml-language-server
Docker      docker-langserver
```

Execution policy:

- Discovery is allowed by default.
- Execution should be limited to known allowlisted servers or explicit user configuration.
- Server startup, initialization, and requests must have timeouts.
- Servers must be stopped when idle or when the runtime shuts down.
- A failing server should be quarantined for the current session or until the next explicit rescan.

Suggested CLI commands:

```bash
hypa doctor code-intelligence
hypa scan-language-servers
hypa index --with-lsp
```

Suggested config:

```toml
[code_intelligence]
roslyn = true
tree_sitter = true
language_servers = "trusted-only"

[language_servers.trusted]
rust_analyzer = true
gopls = true
pyright = true
typescript_language_server = true
```

All LSP-derived facts should include provenance:

```text
provider = lsp
serverId = rust-analyzer | gopls | pyright | ...
capability = definition | references | diagnostics | ...
confidence = semantic
```

## Alternatives Considered

### Require language servers for semantic indexing

Rejected. It would make Hypa dependent on the user's editor/toolchain setup and would degrade the out-of-box experience.

### Ignore language servers entirely

Rejected. Available language servers can materially improve graph quality for definitions, references, diagnostics, and implementation edges.

### Automatically run any detected server

Rejected. Starting arbitrary executables found on a user's system is too permissive. Hypa should use allowlists, trust settings, and explicit config.

## Related Decisions

- ADR 0003: Code Intelligence Provider Strategy
- ADR 0001: Local Context Runtime Operating Model
