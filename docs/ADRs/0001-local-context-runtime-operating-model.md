# ADR 0001: Local Context Runtime Operating Model

## Status

Accepted

## Date

2026-05-06

## Context

Hypa is intended to run as a local background service and tool server for agentic development workflows. It should optimise tool calls, reduce context waste, index code, and provide a reliable local context layer for agent harnesses and editor integrations.

The runtime needs to support multiple usage modes:

- stand-alone local operation without requiring Atomic authentication;
- optional authentication to the Atomic API as the user;
- integration with agent harnesses through MCP, shell commands, and hook mechanisms;
- future alignment with Atomic's agent session, job, run, and memory model.

Agent harnesses differ materially. Some support MCP. Some support pre-tool hooks. Some support shell environment files. Some expose session state, but relying on harness-private transcript or session JSON files would be brittle and host-specific.

## Decision

Hypa will use a local context runtime model with its own durable session state, independent of any agent harness transcript format.

The primary integration surfaces are:

1. MCP server tools for structured file, shell, indexing, memory, and session operations.
2. Shell wrapping for command output optimisation and telemetry.
3. Agent hook adapters where the host supports command rewriting or tool-use interception.
4. Optional API authentication to Atomic for synchronising user-scoped data and attaching events to Atomic sessions, jobs, runs, and steps.

Hypa will not depend on reading or mutating harness-private conversation/session JSON files as a core mechanism.

## Consequences

### Positive

- Hypa remains portable across agent harnesses and editors.
- The runtime can work offline and stand alone.
- Atomic integration can be layered in without making the local runtime dependent on the cloud service.
- Session state can be shaped around Hypa and Atomic concepts instead of inheriting unstable host-specific transcript formats.
- Agent support can be added incrementally through adapters.

### Negative / Trade-offs

- Hypa must maintain its own session identity, lifecycle, persistence, and compaction model.
- Some harness-specific capabilities will require adapter-specific implementation.
- Hypa may not see all context exchanged inside a closed agent harness unless the harness routes actions through MCP, shell hooks, or supported lifecycle hooks.
- Correlating local runtime activity to an Atomic agent session requires explicit session binding or token claims, not implicit transcript inspection.

## Implementation Notes

The runtime should expose stable internal abstractions rather than letting MCP, shell, and hook code directly own the domain model.

Recommended high-level components:

```text
Hypa.Runtime
  Hypa.Cli
  Hypa.McpServer
  Hypa.Shell
  Hypa.AgentHooks
  Hypa.CodeIntelligence
  Hypa.ContextSessions
  Hypa.AtomicClient
```

Recommended session model fields:

```text
SessionId
UserId?                    // present only when authenticated
AtomicAgentSessionId?       // when bound to an Atomic agent session
WorkspaceId?
ProjectId?
ObjectiveId?
JobId?
RunId?
StepId?
ProjectRoot
ShellCwd
TaskSummary?
Findings[]
Decisions[]
FilesTouched[]
Evidence[]
ToolCalls[]
Stats
```

The runtime should support a command similar to:

```bash
hypa session init --agent-session-id <id>
hypa session attach --job-id <id> --run-id <id>
```

The session does not need to know the job or run at process start. Calls can attach job/run/step context later when that information becomes available.

## Alternatives Considered

### Read agent harness session JSON directly

Rejected as a core strategy. It would couple Hypa to unstable, private, and host-specific storage formats. It can be explored as an optional importer later, but should not be required for runtime correctness.

### Require Atomic authentication for all runtime usage

Rejected. Hypa must be useful as a standalone tool-call optimisation and code-indexing runtime. Atomic authentication should unlock synchronisation and richer product integration, not basic operation.

### MCP-only integration

Rejected. MCP is the cleanest structured integration point, but shell output and host tool-use hooks are important for reducing context waste in real agent sessions.

## Related Decisions

- ADR 0002: Deterministic Tool Output Compression
- ADR 0003: Code Intelligence Provider Strategy
- ADR 0004: Optional Language Server Enrichment
