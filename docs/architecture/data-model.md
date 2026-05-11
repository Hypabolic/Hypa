# Data Model

This document defines the initial local data model for Hypa. The model is intentionally local-first but aligned with Atomic concepts so that records can later be attached to Atomic workspaces, projects, objectives, jobs, runs, and steps.

## Model Principles

- Hypa owns its local model. It does not depend on agent harness transcript formats.
- Atomic identifiers are optional and attachable after session start.
- Every generated fact should carry provenance.
- Evidence should be append-oriented and auditable.
- Raw bulky output should be stored by reference with retention policy, not embedded everywhere.
- Compression output is not the canonical source of truth when full output is needed for audit/debug.

## Core Entities

```text
ContextSession
  SessionStats
  SessionBinding
  SessionCheckpoint[]
  EvidenceRecord[]
  ToolCallRecord[]
  FileTouchRecord[]
  Finding[]
  Decision[]
  CodeIntelligenceSnapshot[]

CodeIntelligenceSnapshot
  CodeFile[]
  CodeSymbol[]
  CodeReference[]
  CodeDependencyEdge[]
  CodeDiagnostic[]

ArtifactRef
  references raw output, compressed output, code maps, logs, or external files
```

## ContextSession

A local context session represents a coherent period of agent or human work in a project.

Suggested shape:

```csharp
public sealed class ContextSession
{
    public required string Id { get; init; }
    public int Version { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? ProjectRoot { get; set; }
    public string? ShellCwd { get; set; }
    public string? TaskSummary { get; set; }

    public SessionBinding Binding { get; set; } = new();
    public SessionStats Stats { get; set; } = new();

    public List<Finding> Findings { get; } = [];
    public List<Decision> Decisions { get; } = [];
    public List<FileTouchRecord> FilesTouched { get; } = [];
    public List<ToolCallRecord> ToolCalls { get; } = [];
    public List<EvidenceRecord> Evidence { get; } = [];
    public List<SessionCheckpoint> Checkpoints { get; } = [];
}
```

## SessionBinding

Bindings connect a local Hypa session to Atomic and/or an agent harness.

```csharp
public sealed class SessionBinding
{
    public string? UserId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? ProjectId { get; set; }

    public string? AtomicAgentSessionId { get; set; }
    public string? ObjectiveId { get; set; }
    public string? JobId { get; set; }
    public string? RunId { get; set; }
    public string? StepId { get; set; }

    public string? HarnessName { get; set; }
    public string? HarnessSessionId { get; set; }
}
```

Bindings should be mutable. A session may start unbound and later attach to an Atomic agent session, job, run, or step.

## EvidenceRecord

Evidence is an auditable statement that something happened or was observed.

```csharp
public sealed record EvidenceRecord(
    string Id,
    EvidenceKind Kind,
    string Key,
    string? Value,
    string? ToolName,
    string? InputHash,
    string? OutputHash,
    string? ArtifactRef,
    CodeIntelligenceProvenance Provenance,
    DateTimeOffset Timestamp);
```

Examples:

```text
tool:ctx_shell
tool:ctx_shell:dotnet-test
file:read:src/Hypa.Runtime/SessionManager.cs
diagnostic:cs:CS8602
decision:compression:deterministic-default
finding:code-index:tree-sitter-supported
```

## ToolCallRecord

Tool calls track agent-visible interactions.

```csharp
public sealed record ToolCallRecord(
    string Id,
    string ToolName,
    string InputHash,
    string OutputHash,
    int InputTokens,
    int OutputTokens,
    int SavedTokens,
    TimeSpan Duration,
    string? Command,
    int? ExitCode,
    string? ArtifactRef,
    DateTimeOffset Timestamp);
```

## FileTouchRecord

Tracks files read, indexed, modified, or emitted in a session.

```csharp
public sealed record FileTouchRecord(
    string Path,
    string? FileHash,
    FileTouchKind Kind,
    int ReadCount,
    bool Modified,
    string? LastMode,
    int LastTokenCount,
    DateTimeOffset LastTouchedAt);
```

## Findings and Decisions

Findings are observations. Decisions are commitments.

```csharp
public sealed record Finding(
    string Id,
    string Summary,
    string? FilePath,
    SourceSpan? Span,
    string? EvidenceRef,
    DateTimeOffset Timestamp);

public sealed record Decision(
    string Id,
    string Summary,
    string? Rationale,
    string? EvidenceRef,
    DateTimeOffset Timestamp);
```

Findings and decisions are candidates for later promotion into Atomic memory.

## Code Intelligence Model

```csharp
public sealed record CodeFile(
    string Path,
    string Language,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset IndexedAt);

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

public sealed record CodeReference(
    string StableId,
    string SymbolId,
    string FilePath,
    SourceSpan Span,
    ReferenceKind Kind,
    CodeIntelligenceProvenance Provenance);

public sealed record CodeDependencyEdge(
    string FromId,
    string ToId,
    CodeDependencyKind Kind,
    CodeIntelligenceProvenance Provenance);

public sealed record CodeDiagnostic(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    SourceSpan Span,
    CodeIntelligenceProvenance Provenance);
```

## Provenance

Every non-trivial fact should carry provenance.

```csharp
public sealed record CodeIntelligenceProvenance(
    string Provider,
    string? ProviderVersion,
    string Confidence,
    string? Capability,
    string? SourceArtifactRef,
    DateTimeOffset ObservedAt);
```

Examples:

```text
provider = roslyn, confidence = semantic
provider = tree-sitter, confidence = syntactic
provider = lsp:rust-analyzer, confidence = semantic, capability = references
provider = regex, confidence = heuristic
provider = agent, confidence = claimed
```

## ArtifactRef

Large outputs and generated files should be stored separately and referenced.

```csharp
public sealed record ArtifactRef(
    string Id,
    ArtifactKind Kind,
    string StoragePath,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool Redacted);
```

Artifact kinds:

```text
RawCommandOutput
CompressedCommandOutput
CodeMap
IndexSnapshot
SessionCheckpoint
AgentHandoff
Log
```

## Storage Strategy

MVP storage can be JSON files with atomic writes:

```text
~/.hypa/sessions/<session-id>.json
~/.hypa/sessions/latest.json
~/.hypa/artifacts/<artifact-id>
<project>/.hypa/cache/index.json
```

When the model grows, move to SQLite for local storage:

```text
sessions
session_bindings
evidence
artifacts
tool_calls
file_touches
code_files
code_symbols
code_references
code_edges
code_diagnostics
```

SQLite is the likely local durable store once concurrent indexing, language server enrichment, and richer graph queries are needed.

## Atomic Mapping

Local Hypa entities should map to Atomic concepts without requiring Atomic availability:

```text
ContextSession       -> Atomic AgentSession, when bound
ToolCallRecord       -> Run/Step evidence
EvidenceRecord       -> Evidence / Memory candidate
Finding              -> Finding memory candidate
Decision             -> Decision memory candidate
CodeSymbol/Edge      -> Code context / code graph memory
ArtifactRef          -> Artifact
SessionCheckpoint    -> Resume block / context pack
```

## Compaction and Retention

The session should periodically produce compact checkpoints:

```text
Task summary
Current binding
Recent findings
Recent decisions
Modified files
Important diagnostics
Next steps
Artifact references
Stats
```

Raw artifacts should have retention policies. Redacted full command output can be retained briefly for debugging; durable evidence should point to stable summaries or selected excerpts.
