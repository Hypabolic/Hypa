# Runtime Design

Hypa is a local runtime that sits between agent harnesses, developer shells, project files, and Atomic. It is not only a CLI. It is a long-lived context runtime with multiple entry points.

## Runtime Entry Points

Hypa should support these entry points:

```text
hypa CLI
  direct human use, setup, diagnostics, indexing, shell wrapping

Hypa MCP Server
  structured agent tools over stdio or HTTP/SSE where supported

Shell Hook / Wrapper
  command execution tracking and deterministic output compression

Agent Hook Adapters
  host-specific PreToolUse / command rewrite / session start integration

Background Service
  optional long-lived local daemon for caching, indexing, language server management, and Atomic sync
```

The CLI and MCP server may initially host the runtime in-process. A separate daemon can be introduced when caching, language server lifetimes, and multi-agent coordination justify it.

## Runtime Components

```text
RuntimeHost
  owns lifecycle, configuration, logging, telemetry, cancellation

ToolRegistry
  exposes MCP tools and local command handlers

SessionManager
  loads, creates, updates, compacts, and persists local context sessions

CompressionEngine
  compresses shell and tool outputs using deterministic reducers

CodeIntelligenceEngine
  indexes source code through Roslyn, Tree-sitter, LSP, and fallback providers

EvidenceLedger
  records tool calls, file reads, command runs, diagnostics, findings, and decisions

AtomicSyncClient
  optionally synchronises selected local events and memories to Atomic

CapabilityRegistry
  records available providers, language servers, shell hooks, agent adapters, and feature health
```

## Runtime Modes

### Standalone Local Mode

Default mode. No Atomic token is required.

Capabilities:

- local sessions;
- command compression;
- file/code indexing;
- MCP tools;
- local evidence and diagnostics;
- local summaries/checkpoints.

### Atomic-Authenticated Mode

Hypa has a user token for the Atomic API.

Additional capabilities:

- attach local session to an Atomic agent session;
- attach events to objective/job/run/step IDs;
- sync evidence, decisions, findings, code maps, and artifacts;
- fetch Atomic context packs;
- use Atomic as a durable memory and coordination backend.

### Agent-Session-Bound Mode

Hypa has an Atomic agent session token or local binding.

Additional capabilities:

- all tool calls can be attributed to a specific agent session;
- job/run/step context can be attached per call;
- local events can become structured Atomic run evidence;
- context compaction can emit resume blocks scoped to the agent session.

A Hypa process does not need job/run IDs at startup. Calls can attach them later.

## Configuration Loading

Configuration should be loaded from layered sources:

```text
built-in defaults
user config
project config
workspace config
environment variables
CLI arguments
agent/session binding
```

Suggested local config paths:

```text
~/.hypa/config.toml
<project>/.hypa/config.toml
<project>/.hypa/session.json
```

The runtime should expose an effective configuration view through diagnostics.

## Session Lifecycle

A local session is selected by:

1. explicit session ID;
2. explicit Atomic agent session ID;
3. latest session for the project root;
4. new session for the project root.

Session persistence should be batched to avoid excessive disk writes. Critical lifecycle transitions should force a save:

- session creation;
- explicit attach/detach;
- compaction/checkpoint;
- shutdown;
- idle timeout;
- before uploading/syncing to Atomic.

## Agent Integration Flow

### MCP

```text
Agent -> MCP tool call -> Hypa MCP Server -> ToolRegistry -> Runtime services -> compact result
```

### Shell Compression

```text
Agent/Human shell command
  -> shell alias or explicit hypa -c
  -> command execution
  -> stdout/stderr capture
  -> compression pipeline
  -> compact output returned to caller
  -> evidence recorded
```

### PreToolUse Command Rewrite

```text
Agent proposes shell command
  -> host hook calls Hypa
  -> Hypa checks rewrite policy
  -> host receives rewritten command or reroute instruction
  -> command runs through hypa -c when useful
```

## Capability Discovery

Hypa should maintain a capability registry for diagnostics and agent planning:

```text
MCP transport available
Shell hooks installed
Agent adapters installed
Tree-sitter available
Roslyn available
Language servers found and healthy
Atomic authenticated
Session bound to Atomic
Compression reducers loaded
```

Suggested command:

```bash
hypa doctor
hypa doctor code-intelligence
hypa doctor agents
hypa doctor shell
```

## Error Handling Principles

- Tool failures should be returned in compact, agent-actionable form.
- Internal runtime errors should not crash the long-lived MCP server unless state is corrupt.
- Provider failures should degrade capability status rather than break unrelated tools.
- Shell wrapper failure should fall back to original command where safe.
- Atomic sync failure should not break standalone local operation.

## Telemetry

Telemetry should be local-first and optionally exportable.

Events to record:

```text
ToolCallStarted / ToolCallCompleted
CommandCompressed
CompressionBypassed
CodeFileIndexed
LanguageServerProbed
SessionAttached
SessionCheckpointCreated
AtomicSyncSucceeded / AtomicSyncFailed
```

OpenTelemetry should be used internally where practical so future agent/tool-call telemetry can be correlated across Hypa and Atomic.
