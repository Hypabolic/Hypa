# ADR 0008: Telemetry Model Boundaries Between Hypa and Atomic

## Status

Proposed

## Date

2026-05-11

## Context

Phase 11a introduces token savings recording and a `hypa impact` command. Phase 12 pushes that telemetry to Atomic as structured run events. Before implementation begins, the data model landscape across both codebases needs to be mapped and the sharing boundary decided, so neither side duplicates schema knowledge or couples to the other's internals.

### Hypa's telemetry models (internal domain)

All live in `Hypa.Runtime.Domain`:

| Type | What it captures |
|---|---|
| `CommandMetricsRecord` | Per shell-command: session ID, recorded at, original tokens, compressed tokens, reducer ID, parse tier, duration, exit code, `IsStreamed`, tee artifact ID |
| `ToolCallMetricsRecord` | Per non-shell Hypa MCP tool call: session ID, recorded at, tool name, original tokens, compressed tokens, duration (Phase 5) |
| `ParseMetricsRecord` | Per command: parse tier actually applied, filter ID |
| `FileTouchRecord` | File operation observed during a session: path, operation, content hash |
| `ArtifactRef` | Reference to a tee-stored artifact (full command output, stored on disk) |
| `ContextSession` | The local session: session ID, project root, optional Atomic run ID binding |

`Finding` and `Decision` have been removed from Hypa's domain — they are interpretive types that require reasoning about the meaning of work, which is Atomic's responsibility. Atomic synthesises findings and decisions from the raw observational telemetry Hypa produces.

### Atomic's event model (external API surface)

Atomic's `RunEventArtifactEndpoints` accepts:

```
POST /runs/{runId}/events
  Body: AddEventInput(RunId, StepId?, EventType: string, Payload: JsonObject, Source?, IdempotencyKey?)
  Response: EventDto(Id, RunId, StepId?, EventType, Payload: JsonObject, Source?, CreatedAt)

POST /runs/{runId}/artifacts
  Body: AddArtifactInput(RunId, StepId?, ArtifactType, Title, Uri, Metadata: JsonObject, CreatedBy?, IdempotencyKey?)
  Response: ArtifactDto(Id, RunId, StepId?, ArtifactType, Title, Uri, Metadata: JsonObject, ...)
```

Both `EventDto.Payload` and `ArtifactDto.Metadata` are opaque `JsonObject` fields. Atomic does not specify what goes in them — that is left to the producing system.

### The gap

Without a shared model:

- Hypa writes `payload["originalTokens"] = 100` as an anonymous JSON property.
- Atomic reads `payload["originalTokens"]` as an anonymous JSON property.
- If one side renames the field, the other silently breaks with no compile-time signal.
- Atomic's memory synthesis and analytics layers cannot reference payload fields by name safely.
- TypeScript types (Atomic's Next.js frontend, per ADR 0007) cannot be generated for telemetry payloads.

### What exists in `Hypa.Sdk` today

`Hypa.Sdk` currently contains `Hypa.Sdk.CodeIntelligence` — the shared code symbol domain model (code from `CodeFileIdentity`, `CodeSymbol`, `CodeDependencyEdge`, etc.). ADR 0007 established `Hypa.Sdk` as the canonical home for types shared between Hypa and downstream consumers including Atomic, with a Quicktype-based multi-language generation chain.

No telemetry types exist in `Hypa.Sdk` yet.

### What exists in Atomic for the Hypa integration

Nothing. Atomic has no Hypa-specific types. It only has the generic `AddEventInput` / `EventDto` surface, where `Payload` is `JsonObject`.

---

## Decision

### 1. Add `Hypa.Sdk.Telemetry` to the existing `Hypa.Sdk` project

All shared telemetry payload types and event type string constants live in `Hypa.Sdk.Telemetry`. This namespace contains only the **wire format** — the shapes that cross the boundary between Hypa and Atomic. It contains no Hypa-internal domain logic and no Atomic-internal domain types.

Atomic references `Hypa.Sdk` as a `<PackageReference>`. Atomic uses `Hypa.Sdk.Telemetry` types to deserialise `EventDto.Payload` fields when processing Hypa-originated events for memory synthesis, analytics, or the frontend.

The existing Quicktype generation chain (ADR 0007) picks up `Hypa.Sdk.Telemetry` types automatically, making TypeScript and Python versions available to Atomic's frontend and any agent tooling.

### 2. Hypa's internal domain models are not shared

`CommandMetricsRecord`, `ToolCallRecord`, `ParseMetricsRecord`, `EvidenceRecord`, and all `Hypa.Runtime.*` types remain internal to Hypa. At the sync boundary, Hypa maps internal types → `Hypa.Sdk.Telemetry` payload types → `JsonObject` → `AddEventInput`. Atomic never sees `CommandMetricsRecord`.

### 3. Atomic's internal domain models are not referenced by Hypa

Hypa's sync layer calls Atomic's REST API over HTTP. It does not take a `<PackageReference>` to any `Atomic.*` assembly. HTTP request/response bodies are constructed from `Hypa.Sdk.Telemetry` types serialised to JSON. This isolates Hypa from Atomic's internal refactoring.

When the Atomic API has a stable OpenAPI spec, Hypa uses a Kiota-generated typed HTTP client (per ADR 0007 Tier 2) rather than raw `HttpClient` calls. The generated client models use `JsonObject` for payload fields, which Hypa fills from `Hypa.Sdk.Telemetry` types.

### 4. No `Atomic.Sdk` package is introduced

There is no need for `Atomic.Sdk` at this time. Atomic's public contract is its REST API, not a .NET library surface. If external consumers need to call Atomic's API, they use the OpenAPI-generated client (Kiota). `Atomic.Sdk` would couple Atomic's release cycle to all consumers; the REST API + OpenAPI approach does not.

### 5. Idempotency key convention

Hypa uses `CommandMetricsRecord.Id.ToString("N")` (lowercase hex, no hyphens) as the `IdempotencyKey` in `AddEventInput`. This guarantees exactly-once delivery when sync restarts from cursor. Tool call events use `ToolCallMetricsRecord.Id.ToString("N")` on the same convention.

---

## What Goes in `Hypa.Sdk.Telemetry`

### Event type constants

```csharp
namespace Hypa.Sdk.Telemetry;

public static class HypaEventTypes
{
    public const string SessionStarted      = "hypa.session.started";
    public const string SessionEnded        = "hypa.session.ended";
    public const string CommandCompressed   = "hypa.command.compressed";
    public const string CommandPassthrough  = "hypa.command.passthrough";
    public const string ToolCallCompressed  = "hypa.tool_call.compressed";
    public const string FileRead            = "hypa.file.read";
    public const string FileWrite           = "hypa.file.write";
    public const string FileTouch           = "hypa.file.touch";
    public const string AgentTurn           = "hypa.agent.turn";
    public const string CodeIndexed         = "hypa.code.indexed";
}
```

All nine event types are observational — things Hypa directly witnessed. Interpretive event types (`hypa.evidence.finding`, `hypa.evidence.decision`, `hypa.session.summary`) are absent: those require reasoning about the meaning of the work, which is Atomic's responsibility.

### Payload records (v1)

Each payload record carries a `SchemaVersion` integer and standard correlation fields (`SessionId`, `RecordedAt`). `SessionId` lets Atomic group events within a run. `RecordedAt` is when the operation occurred in Hypa — `EventDto.CreatedAt` on the Atomic side reflects only when the sync push was received. When a field is added, `SchemaVersion` increments. Atomic should handle unknown versions gracefully (log, store, and skip memory synthesis if unrecognised).

```csharp
public sealed record HypaSessionEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string ProjectRoot { get; init; } = string.Empty;
    public string? AgentKind { get; init; }
    public string? AtomicRunId { get; init; }
}

public sealed record HypaCommandEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string Command { get; init; } = string.Empty;
    public string Executable { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public string ReducerId { get; init; } = string.Empty;
    public string ParseTier { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public int ExitCode { get; init; }
    public bool IsStreamed { get; init; }
    public string? TeeArtifactId { get; init; }
    public string? FilterId { get; init; }
}

// Catch-all for Hypa MCP tools that are not shell wrappers and do not have a
// dedicated record type. Shell MCP tools (hypa_shell) produce HypaCommandEventV1.
// File MCP tools (hypa_read, hypa_write) produce their own event types below.
public sealed record HypaToolCallEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int CompressedTokens { get; init; }
    public long DurationMs { get; init; }
}

public sealed record HypaFileReadEventV1   // Phase 5
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string? ReadMode { get; init; }
    public int? TokensReturned { get; init; }
    public bool? CacheHit { get; init; }
    public long? DurationMs { get; init; }
}

public sealed record HypaFileWriteEventV1  // Phase 5
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;  // create | edit | delete
    public int? LinesAdded { get; init; }
    public int? LinesRemoved { get; init; }
    public long? ByteDelta { get; init; }
}

public sealed record HypaFileTouchEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;  // read | write | edit | delete
    public string? ContentHash { get; init; }
}

public sealed record HypaAgentTurnEventV1  // Phase 9B / Phase 13
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public int TurnIndex { get; init; }
    public string Role { get; init; } = string.Empty;         // user | assistant
    public int? EstimatedTokens { get; init; }
    public string CaptureSource { get; init; } = string.Empty; // log_tail | proxy
    // Content is never included at any redaction level.
}

public sealed record HypaCodeIndexedEventV1
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public int FilesIndexed { get; init; }
    public int FilesSkipped { get; init; }
    public int SymbolCount { get; init; }
    public int EdgeCount { get; init; }
    public string Language { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public long DurationMs { get; init; }
}
```

All types in `Hypa.Sdk.Telemetry` are included in the `Hypa.Sdk` AOT-compatible source-gen JSON context (`HypaSdkJsonContext`).

### What does NOT go in `Hypa.Sdk.Telemetry`

| Type | Stays in |
|---|---|
| `CommandMetricsRecord` | `Hypa.Runtime.Domain.Runner` |
| `ToolCallRecord` / `ToolCallMetricsRecord` | `Hypa.Runtime.Domain.Runner` |
| `ParseMetricsRecord` | `Hypa.Runtime.Domain.Metrics` |
| `FileTouchRecord` | `Hypa.Runtime.Domain.Sessions` |
| `ArtifactRef` | `Hypa.Runtime.Domain.Sessions` |
| `ContextSession` | `Hypa.Runtime.Domain.Sessions` |
| `EventDto`, `AddEventInput` | `Atomic.Application.Models` |
| `RunDto`, `ArtifactDto` | `Atomic.Application.Models` |

`Finding`, `Decision`, and `SessionStats` have been removed from `Hypa.Runtime` entirely — they are interpretive types that belong in Atomic, not in an observation-layer tool.

---

## The Mapping Layer

Hypa's sync service (Phase 12) is the only place where internal models are mapped to SDK types. This service lives in `Hypa.Infrastructure` or a dedicated `Hypa.Sync` project:

```
CommandMetricsRecord  →  HypaCommandEventV1   →  JsonObject  →  AddEventInput
ToolCallMetricsRecord →  HypaToolCallEventV1  →  JsonObject  →  AddEventInput
FileTouchRecord       →  HypaFileTouchEventV1 →  JsonObject  →  AddEventInput
ContextSession        →  HypaSessionEventV1   →  JsonObject  →  AddEventInput
CodeIndexResult       →  HypaCodeIndexedEventV1 → JsonObject →  AddEventInput
-- Phase 5 --
FileReadMetricsRecord →  HypaFileReadEventV1  →  JsonObject  →  AddEventInput
FileWriteMetricsRecord → HypaFileWriteEventV1 →  JsonObject  →  AddEventInput
-- Phase 9B / 13 --
AgentTurnRecord       →  HypaAgentTurnEventV1 →  JsonObject  →  AddEventInput
```

The mapper is a thin translation layer with no business logic. Any field added to a payload schema must be added to the corresponding internal model first; the mapper then exposes it.

---

## Consequences

### Positive

- Both sides have compile-time type safety for the payload fields that matter.
- Payload schema is versioned; either side can detect when it is reading an older or newer payload.
- The Quicktype generation chain produces TypeScript and Python types for Atomic's frontend and agent tooling without any additional configuration.
- Atomic's internals are fully decoupled from Hypa's internals — both sides can evolve their domain models independently.
- `Hypa.Sdk` remains the single authoritative home for types Atomic consumes from Hypa, consistent with ADR 0007.
- Idempotency key convention makes Phase 12 sync restarts safe by construction.

### Negative / Trade-offs

- A mapping layer is required in Hypa's sync path. This is a small but real maintenance obligation — when internal models gain fields, the mapper and the SDK type must both be updated.
- Atomic takes a dependency on `Hypa.Sdk`. If Hypa makes a breaking change to `Hypa.Sdk.Telemetry`, Atomic must update. This is mitigated by `SchemaVersion` — Atomic can support multiple versions in parallel if needed.
- The `Hypa.Sdk` release cycle must be coordinated with Atomic. An `Hypa.Sdk` version bump that adds a payload field should be published before Atomic is updated to read it.

---

## Implementation Notes

### Files to create or modify

| Action | File |
|---|---|
| Create | `src/Hypa.Sdk/Telemetry/HypaEventTypes.cs` |
| Create | `src/Hypa.Sdk/Telemetry/TelemetryRedactionLevel.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaSdkSchemaVersions.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaSessionEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaCommandEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaToolCallEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaFileReadEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaFileWriteEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaFileTouchEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaAgentTurnEventV1.cs` |
| Create | `src/Hypa.Sdk/Telemetry/HypaCodeIndexedEventV1.cs` |
| Create | `src/Hypa.Sdk/HypaSdkJsonContext.cs` — AOT source-gen context covering all SDK types |
| Modify | `src/Hypa.Sdk/Hypa.Sdk.csproj` — update description and tags |
| Create | `src/Hypa.Infrastructure/Sync/TelemetryEventMapper.cs` — maps internal → SDK types |

### Atomic-side

Atomic adds `<PackageReference Include="Hypa.Sdk" Version="..." />` to its `Atomic.Application` or a dedicated `Atomic.Hypa` integration project. It uses `Hypa.Sdk.Telemetry` to deserialise event payloads received from the Hypa sync push:

```csharp
// In Atomic's memory synthesis or analytics layer
if (evt.EventType == HypaEventTypes.CommandCompressed)
{
    var payload = evt.Payload.Deserialize<HypaCommandEventV1>(options);
    var tokensSaved = payload.OriginalTokens - payload.CompressedTokens;
    // ... synthesise memory or update run stats
}
```

### Schema evolution rules

1. **Adding a nullable field** — backwards compatible; increment `SchemaVersion`.
2. **Adding a required field** — breaking; publish a new record type (`HypaCommandEventV2`); update `HypaEventTypes` with a new constant; support both in Atomic until old events are no longer produced.
3. **Renaming or removing a field** — always breaking; follow the same v2 path.
4. Atomic must handle unknown `SchemaVersion` values without throwing — log and store, skip synthesis.

---

## Alternatives Considered

### `Atomic.Sdk` (Atomic defines the contract)

Would reverse the producer/consumer relationship: Atomic (the consumer) would own the payload schema that Hypa (the producer) must conform to. This creates an odd dependency direction and requires Atomic to anticipate Hypa's telemetry fields. Rejected in favour of `Hypa.Sdk.Telemetry`, where the producer owns the schema.

### Separate `Hypa.Telemetry.Contracts` package

A neutral third package that neither Hypa nor Atomic owns directly. Adds a third repository or at minimum a third csproj to coordinate releases. Overkill while both products are in the same organisation. Can be revisited if Hypa's telemetry schema is consumed by a third party beyond Atomic.

### No shared types — `JsonObject` convention only

Both sides agree on field names by convention with no compile-time enforcement. The simplest option, but silently breaks on field renames and provides no path for Quicktype generation of TypeScript/Python types. Rejected because the schema will be read by Atomic's memory synthesis LLM prompts, where field name drift is especially damaging.

### Package both codebases' models into a shared `Atomic.Platform.Contracts` monorepo package

Combines all cross-product contracts (Hypa telemetry, Atomic API surface, future tool contracts) into one package. Premature — there is currently only one active integration surface. If multiple products produce events that Atomic consumes, this may become the right answer.

---

## Related Decisions

- ADR 0007: SDK Multi-Language Generation Strategy
- ADR 0001: Local Context Runtime Operating Model
- Phase 11a plan: Token Savings Ledger — Gaps and `hypa impact`
- Phase 12 plan (pending): Atomic Sync and Run Telemetry Push
