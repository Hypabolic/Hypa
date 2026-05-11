# Feature Roadmap

This roadmap describes how Hypa can move toward capability completion, while remaining .NET-first, AOT-safe, and clearly positioned within the Atomic product family.

The goal is to build runtime capability — local context optimisation for agents — and to draw a clean line between what belongs in Hypa and what belongs in Atomic.

---

## Product Vision and Boundary

Hypa and Atomic are complementary, not overlapping. Each owns a distinct job.

### Hypa — Local Agent Cost Optimisation and Data Gathering

Hypa runs on the developer's machine, close to the agent harness. Its job is:

- transparently compress shell and tool output going into LLMs;
- rewrite agent shell commands to cheaper, safer equivalents;
- serve compact file reads and code intelligence to agents via MCP;
- collect raw agent session telemetry (tool calls, token spend, artifacts, timings);
- emit that telemetry to Atomic as structured run events;
- maintain a lightweight local session store, evidence ledger, and artifact cache;
- manage auth credentials shared with the Atomic CLI.

Hypa is **standalone**: it delivers value without Atomic. It has no planning, no memory synthesis, no team coordination.

### Atomic — Planning, Memory, and Agent Coordination

Atomic runs as a deployed service (with a thin local CLI). Its job is:

- model Jobs, Runs, Plans, and Objectives;
- generate durable memories from Hypa telemetry and run evidence;
- provide context packs drawn from workspace memory back to agents;
- coordinate multi-agent teams via A2A task delegation;
- own the embedding, semantic search, and property graph layers;
- serve as the intelligence and orchestration layer for teams doing real work.

The Atomic CLI is a **thin client** — it authenticates, instructs Atomic, starts/stops runs, and retrieves context. It does not compress or instrument agent harnesses directly.

### Capabilities That Belong in Atomic, Not Hypa

```text
A2A task protocol (JSON-RPC tasks/send, tasks/get, tasks/cancel)
Multi-agent orchestration and role assignment
Memory generation from telemetry (LLM synthesis)
Embedding and semantic search (already in Atomic.Embedding)
Full property graph and deep code queries across workspaces
Job / Run / Plan / Objective lifecycle management
Team workspace context retrieval and distribution
Observatory and global statistics
Neural/attention-learned context ranking
```

---

## Reference Capabilities

### Implemented into Hypa

```text
Generic compression wrapper (-c command parser)
MCP stdio server with tool registry
Deterministic reducers and filter DSL
Command rewrite registry with first-class modules
Shell hook delegates and agent harness writers
File read modes and cache
Session continuity and evidence ledger
Parser tiers (Full / Degraded / Passthrough)
Tee recovery for truncated/failed commands
SQLite analytics and token savings ledger
4-layer terse compression pipeline
MCP tool description compression (lazy-load stubs)
Redaction system (refs_only, summary, full)
Trajectory event model and dual-store (JSONL WAL + SQLite index)
Daemon mode for background harness monitoring
Local agent session registry (active session tracking, not coordination)
Local lightweight context event bus (for daemon → MCP bridging)
Proxy mode (transparent LLM-level tool result compression)
Agent harness writers for Claude, Cursor, Codex, Copilot, Windsurf, Kiro, etc.
```

### implemented into Atomic

```text
workflow engine (multi-agent task assignment)
semantic_search (embedding-based, already in Atomic.Embedding)
agent full coordination protocol (multi-agent registry owned by Atomic)
A2A compat layer (JSON-RPC tasks/send, tasks/get, tasks/cancel)
Context / session full event bus (workspace-scoped, cloud-backed)
Property graph deep queries
Observatory, global stats, gain attribution by run/job/agent
Dashboard team views
```

---

## Completed Phases

### Phase 0: Documentation and ADR Baseline ✓

ADR template, runtime operating model, compression, code intelligence, LSP enrichment, command rewrite registry, filter DSL, and SDK multi-language generation ADRs. Architecture overview, data model, compression pipeline, code intelligence, and MCP/tool design documents.

### Phase 1: Local CLI, Config, and Session Store ✓

`hypa setup`, `hypa doctor`, `hypa session status/init/attach/checkpoint`. Local config loading, project root detection, SQLite session store, evidence ledger MVP, artifact storage, latest-session pointer.

### Phase 2: Command Rewrite Registry ✓

`hypa rewrite "<command>"`, shell-aware lexer, first-class rewrite rules, generic wrapper fallback, passthrough/deny/ask decisions, `HYPA_DISABLED=1`, compound command segment rewriting, permission-aware exit-code contract.

### Phase 3: Shell Compression ✓

`hypa -c`, `hypa -t`, reducers for git, dotnet, pnpm/npm/yarn, tsc, docker, kubectl, helm, psql, terraform, gh. Shared command runner, deterministic reducers, generic cleanup, safe truncation, token counting, tee for failures.

### Phase 4: Filter DSL and Parser Tiers ✓

Built-in filter DSL engine, user-global and project-local trust-gated filters, parser-tier model (Full / Degraded / Passthrough), canonical DTOs, parser-tier metrics and warnings, fixture-based golden tests.

### Phase 7: Code Intelligence MVP ✓

TreeSitter.DotNet provider, code symbol DTOs, code file index, symbol search, dependency edges, graph export basics, provider health reporting.

---

## Pending Phases

### Phase 5: MCP Server MVP

Deliverables:

```text
hypa_session
hypa_shell
hypa_read
hypa_search
hypa_code
hypa_context
hypa_compress
hypa_diagnostics
```

Implementation:

- stdio MCP server with tool registry;
- compact output contracts per tool;
- session and evidence recording for every tool call;
- configurable tool exposure;
- project path jail;
- basic role/policy hooks.

Feature parity target: agents can use Hypa as a structured context runtime; agents can run compressed shell commands and read files through Hypa.

---

### Phase 5B: Terse / Token-Dense Compression Pipeline

_Not in original roadmap. Adds a multi-layer post-processing pipeline beyond pattern reducers._

Deliverables:

```text
4-layer terse pipeline (deterministic → residual → agent shaping → MCP description)
Quality gate with compression acceptance criteria
MCP tool description lazy-load stubs with on-demand expansion
TerseResult attribution (pattern savings vs terse savings per layer)
```

Implementation:

- **Layer 1** — Deterministic: surprisal scoring, content/function word filtering, domain dictionaries, quality gate. Applied after pattern reducers.
- **Layer 2** — Residual: pattern-aware post-terse; avoids double-compression; tracks attribution.
- **Layer 3** — Agent output shaping: Telegraph-English-inspired brevity, scale-aware adaptive levels, injected via agent instruction templates at harness init time.
- **Layer 4** — MCP description compression: compresses tool descriptions sent in the tools list; lazy-load stubs for infrequently used tools; full description returned on first real call.

Feature target: terse pipeline replicated in .NET; MCP context overhead reduced by compressing tool descriptions, not only tool results. Layer 4 is highest ROI — do it first.

Notes:

- Layer 3 prompt shaping lives in agent instruction templates, not in Hypa's compression logic.
- Quality gate is critical: a compression that breaks meaning is worse than none.

---

### Phase 6: File Cache and Read Modes

Deliverables:

```text
hypa_read mode=full
hypa_read mode=map
hypa_read mode=signatures
hypa_read mode=aggressive
hypa_read mode=lines:N-M
hypa_read mode=task
hypa_read mode=reference
```

Implementation:

- file content hash cache (mtime-validated; cache re-read costs ~13 tokens);
- per-mode result cache;
- file touch tracking;
- token-aware read mode selection;
- compact cache-hit responses;
- ignore rules for generated/vendor files.

Feature target: repeated reads become cheap; agents request compact views instead of full files;

---

### Phase 8: Optional Language Server Enrichment

Deliverables:

```text
hypa scan-language-servers
hypa doctor code-intelligence
hypa index --with-lsp
```

Implementation:

- language server discovery;
- trust/allowlist config;
- JSON-RPC stdio client;
- initialize/probe lifecycle;
- references/definitions/diagnostics enrichment;
- provider quarantine on repeated failure.

Feature parity target: improved semantic graph in environments that have LSPs; diagnostics expose what Hypa can trust.

---

### Phase 9: Agent Hook Integration

Deliverables:

```text
hypa init --agent claude
hypa init --agent cursor
hypa init --agent codex
hypa init --agent copilot
hypa init --agent windsurf
hypa init --agent kiro
hypa hooks status
hypa hooks uninstall
```

Implementation:

- MCP config writers per harness;
- shell hook installers;
- `PreToolUse` command rewrite adapters (thin delegates calling `hypa rewrite`);
- session-start instruction injection where the harness supports it;
- safe passthrough for hosts that cannot mutate tool input.

Harnesses to support: Claude Code, Cursor, VS Code Copilot, Codex, Windsurf, Kiro, Cline, JetBrains AI.

Feature target: common agent harnesses automatically route compressible commands through Hypa; agents receive instructions to use Hypa tools; hook logic stays thin and host-specific.

---

### Phase 9B: Daemon Mode and Greedy Session Collection

_Not in original roadmap. Enables background telemetry without requiring the agent to call Hypa explicitly._

**Motivation**: Hypa's value compounds when it collects session data even when not wired directly into the agent. A daemon can watch known harness directories, pick up live session state, and emit structured telemetry to Atomic without requiring hooks to be installed in every harness.

Deliverables:

```text
hypa serve -d              # start background daemon (Unix socket + PID file)
hypa serve stop
hypa daemon status
hypa daemon logs
hypa session watch         # list sessions the daemon is currently watching
```

Implementation:

- Unix socket + PID file daemon pattern for background operation;
- harness directory watcher: inotify/polling of known agent session paths (`~/.claude/projects/`, `~/.cursor/sessions/`, etc.);
- `SessionLogTailSource`: tails known log/JSONL formats and normalises events into Hypa's `TrajectoryEvent` schema;
- `RuntimeHookSource`: captures tool spans from Hypa-managed wrappers when hooks are installed;
- dual-store: append-only JSONL WAL for durability + SQLite index for replay and query;
- cursor-based replay so Atomic sync can resume after restarts without re-sending events;
- secret scrubbing on all command args/output before persistence.

**Active Run Manifest Protocol** (the Hypa↔Atomic signal):

When the user starts a Job Run via `atomic run start`, the Atomic CLI writes:

```json
// ~/.atomic/active-run.json
{
  "schema_version": 1,
  "run_id": "<uuid>",
  "job_id": "<uuid>",
  "workspace_id": "<uuid>",
  "started_at": "<rfc3339>",
  "agent_kind": "claude",
  "project_root": "/path/to/project"
}
```

The Hypa daemon watches this file (inotify on Linux, FSEvents on macOS). On arrival it begins collecting session telemetry scoped to that run and project root. When the Atomic CLI writes `finished_at` or removes the file, Hypa flushes and seals the run's telemetry batch.

This is a **pull model from Hypa's perspective** — Hypa reads a file, Atomic doesn't need to know Hypa's socket address. The Atomic CLI only writes and removes a JSON file.

Redaction defaults:

- `refs_only` — file paths, command names, no content (default);
- `summary` — token counts and tool-call categories;
- `full` — full output; requires explicit per-project opt-in;
- secret scrubbing always on.

Feature parity target: Hypa collects trajectories without full hook installation; Atomic gets structured run telemetry even from harnesses that don't natively call Hypa tools.

---

### Phase 9C: Unified Auth and Credential Sharing

_Not in original roadmap. Both Hypa and the Atomic CLI talk to the Atomic deployment; they must share credentials without duplicating auth flows._

**Design**: Both tools share a credential store at `~/.atomic/credentials.json`. The auth flow runs once in whichever tool the user invokes first.

```text
atomic auth login       # device flow / browser OAuth → writes ~/.atomic/credentials.json
hypa auth status        # reads same credential store, reports expiry and workspace
hypa auth login         # runs the same device flow if credentials are absent or expired
```

Implementation:

- `~/.atomic/credentials.json` schema: `{ schema_version, access_token, refresh_token, expires_at, workspace_id, api_base_url }`;
- platform keychain integration optional (libsecret on Linux, Keychain on macOS, DPAPI on Windows) behind a config flag;
- Hypa daemon reads credentials at start and refreshes automatically near expiry;
- Atomic CLI reads the same file;
- both tools use the same `api_base_url` — self-hosted Atomic deployments work without extra config;
- `ATOMIC_API_TOKEN` env var override for CI/non-interactive contexts.

The Hypa `auth` command group is minimal: `login`, `logout`, `status`. It delegates to the shared credential file and does not implement its own OAuth server.

Feature parity target: user authenticates once; both Hypa and Atomic CLI are ready; daemon syncs without additional prompts.

---

### Phase 10: Context Packs, Checkpoints, and Handoffs

Deliverables:

```text
hypa context overview
hypa context pack
hypa context expand
hypa context handoff
hypa session resume-block
```

Implementation:

- session compaction;
- token-budgeted context pack builder;
- code map selection based on recent file reads;
- recent decisions/findings selection from evidence ledger;
- artifact reference inclusion;
- handoff document for agent-to-agent or agent-to-human transitions.

Atomic integration: once Phase 12 is live, context packs can be enriched with Atomic memory retrieval (pull-context via Atomic API).

Feature parity target: agents can recover useful state after compaction or a fresh session; local sessions become durable memory candidates for Atomic.

---

### Phase 11: Metrics, Impact, Discover, and Cost Reporting

Deliverables:

```text
hypa impact
hypa impact --daily
hypa impact --json
hypa discover
hypa parse-health
hypa cost-report
```

Implementation:

- SQLite token savings ledger (building on existing evidence ledger);
- command/reducer stats by tool and project;
- parser-tier and compression failure stats;
- model pricing config for cost estimation;
- gain score: `tokens_saved × price_per_token` expressed as a dollar figure;
- slow command log;
- tool-call metrics per MCP tool;
- local report export (JSON and plain text).

Feature parity target: developers see measurable value from Hypa; weak reducers and missed rewrite opportunities become visible; Atomic can attribute savings to specific runs/jobs via Phase 12 telemetry.

---

### Phase 12: Atomic Sync and Run Telemetry Push

_Replaces "Atomic Sync and Memory Promotion". Hypa's job is telemetry and evidence push; memory synthesis belongs to Atomic._

Deliverables:

```text
hypa sync push
hypa sync status
hypa sync pull-context
hypa session attach --run-id <id>
```

Implementation:

- cursor-based trajectory sync: JSONL WAL → Atomic RunEvent API;
- idempotent event push with server-side dedup on `event_id`;
- run attachment: associate local Hypa session with an Atomic Job Run ID;
- pull-context: retrieve Atomic-generated context pack for the current run;
- offline queue for sync failures (exponential backoff retry);
- sync status: last cursor, pending event count, last error.

Atomic's responsibilities (not Hypa):

- receive run events via `RunEventArtifactEndpoints`;
- trigger memory generation from telemetry (`DerivedMemoryOrchestrationUseCases`);
- return context packs via `ContextAndSummaryEndpoints`;
- associate run events with Job records and update plan state.

Feature parity target: Hypa becomes Atomic's local telemetry edge; raw agent trajectories become Atomic run evidence and memory candidates without manual curation.

---

### Phase 13: LLM Proxy Mode

_Not in original roadmap. Compresses tool results at the LLM protocol boundary before they reach the model._

**Motivation**: Hook-based compression requires the agent to call `hypa -c` explicitly. Proxy mode intercepts at the HTTP level — any agent that points its `ANTHROPIC_BASE_URL` (or equivalent) at Hypa's proxy gets transparent compression with zero hook installation.

Deliverables:

```text
hypa proxy start        # local proxy on configurable port (default: 4723)
hypa proxy stop
hypa proxy status
```

Implementation:

- HTTP reverse proxy for Anthropic, OpenAI, and Google Gemini endpoints;
- intercept `tool_result` content blocks in response streams;
- apply compression pipeline (pattern reducers → terse pipeline) before forwarding;
- inject `[hypa: N→M tok, -X%]` footer into compressed blocks;
- forward all other traffic unmodified;
- record compressed vs original token counts in the evidence ledger;
- `HYPA_PROXY_PASSTHROUGH=1` env var disables compression for debugging.

Notes:

- Start HTTP-only for local use; add TLS termination config in a follow-up.
- Proxy mode is orthogonal to MCP mode — both can run simultaneously via the daemon.
- Power-user feature; off by default.

Feature parity target: any agent running locally gets compression without any hook installation; proxy mode is the zero-configuration fallback.

---

### Phase 14: Local Context Bus

_Not in original roadmap. A lightweight SQLite-backed event bus for process (daemon, mcp and cli) bridging._

**Motivation**: The daemon collects trajectory events; the MCP server needs to surface status back to agents in real time. A shared in-process event bus with cursor-based replay solves this cleanly. It is also the foundation for any future local multi-agent scenarios (two Claude sessions on the same machine that want to share context via Hypa).

It does not synchronise with Atomic — that goes through Phase 12.

Deliverables:

```text
Workspace/channel isolated SQLite event streams
Consistency levels: Local, Eventual, Strong
Event kinds: ToolCallRecorded, SessionMutated, ArtifactStored
FTS5 search over event payloads
Causal lineage via parent_id
hypa_context_events MCP tool (poll_since, subscribe, lineage)
```

Implementation:

- `IContextBus` port in `Hypa.Runtime`; SQLite backend in `Hypa.Infrastructure`;
- WAL mode, connection read pool, broadcast channel for real-time delivery;
- `hypa_context_events` MCP tool: poll since cursor, filtered by kind or actor;
- daemon appends events as it collects trajectory data;
- MCP server subscribes and surfaces to agents.

Feature parity target: daemon and MCP server share a coherent local event stream; agents can poll for session events from concurrent sessions on the same machine.

---

### Phase 15: Local Agent Session Registry

_Not in original roadmap. A minimal registry of active agent sessions visible to the daemon and MCP server._

This is a lightweight local registry that:

- records which agent harnesses are active and what project they are working on;
- allows the daemon to scope telemetry collection to the right project root;
- allows MCP tools to report concurrent session status to an agent.

Deliverables:

```text
hypa session list              # active agent sessions on this machine
hypa_agent_sessions MCP tool  (list, status)
```

Implementation:

- `AgentSessionRegistry` backed by a JSON file (not SQLite — too simple to warrant it);
- daemon registers sessions as harness activity is detected;
- stale cleanup after 24 h of inactivity;
- no message bus, no broadcast, no task delegation — those are Atomic's job.

Feature parity target: Hypa can report which agent sessions are active and what projects they relate to; daemon scopes telemetry correctly even when multiple harnesses are running.

---

### Phase 16: Local Observability Dashboard

_Not in original roadmap. A local web dashboard for token savings, reducer health, and session telemetry._

Deliverables:

```text
hypa dashboard             # launches browser-based dashboard (localhost)
hypa dashboard --json      # machine-readable current stats snapshot
```

Implementation:

- minimal TUI, hypa command output or web dashboard served from the local daemon;
- views: overview (daily savings, session count), compression stats, reducer health, parser-tier breakdown, active sessions;
- localhost only; no auth;
- optional stats push to Atomic for workspace-level rollup.

Feature target: dashboard for Hypa-owned metrics; team rollup deferred to Atomic.

---

## Capability Matrix

| Capability | Hypa | Atomic | Status |
|---|:---:|:---:|---|
| CLI setup / doctor | Yes | — | ✓ Done |
| Local session store | Yes | — | ✓ Done |
| Command rewrite registry | Yes | — | ✓ Done |
| Generic compression wrapper | Yes | — | ✓ Done |
| First-class reducers | Yes | — | ✓ Done |
| Filter DSL | Yes | — | ✓ Done |
| Parser tiers | Yes | — | ✓ Done |
| Tee recovery | Yes | — | ✓ Done |
| Shell compression | Yes | — | ✓ Done |
| Code intelligence MVP (TreeSitter) | Yes | — | ✓ Done |
| MCP stdio server | Yes | — | Phase 5 |
| 4-layer terse pipeline | Yes | — | Phase 5B |
| MCP description compression | Yes | — | Phase 5B |
| File read modes + cache | Yes | — | Phase 6 |
| Language server enrichment | Yes (optional) | — | Phase 8 |
| Agent hook writers | Yes | — | Phase 9 |
| Daemon + harness watching | Yes | — | Phase 9B |
| Greedy session / log tailing | Yes | — | Phase 9B |
| Active-run manifest (reads) | Yes | — | Phase 9B |
| Active-run manifest (writes) | — | Yes | Atomic CLI |
| Unified auth / shared credentials | Both | Both | Phase 9C |
| Context packs (local) | Yes | — | Phase 10 |
| Context packs (workspace memory) | — | Yes | Atomic |
| Token savings ledger | Yes | — | Phase 11 |
| Gain / cost reporting (local) | Yes | — | Phase 11 |
| Gain attribution by run/job | — | Yes | Atomic |
| Run telemetry push | Yes | — | Phase 12 |
| Run event ingestion | — | Yes | Atomic |
| Memory generation from telemetry | — | Yes | Atomic |
| LLM proxy mode | Yes | — | Phase 13 |
| Local context event bus | Yes | — | Phase 14 |
| Local agent session registry | Yes | — | Phase 15 |
| Local observability dashboard | Yes | — | Phase 16 |
| A2A task protocol (JSON-RPC) | — | Yes | Atomic |
| Multi-agent orchestration | — | Yes | Atomic |
| Embedding + semantic search | — | Yes | Atomic |
| Property graph / deep code queries | — | Yes | Atomic |
| Job / Run / Plan / Objective lifecycle | — | Yes | Atomic |
| Team workspace memory | — | Yes | Atomic |
| Team dashboard | — | Yes | Atomic |
| Observatory / global stats | — | Yes | Atomic |

---

## Implementation Discipline

Each phase should produce:

- command/API docs;
- unit tests;
- integration fixtures;
- golden-file tests where output matters;
- diagnostics support;
- telemetry events fed into the trajectory store;
- ADR updates if a major architectural decision changes.

---

## Out of Scope for Hypa

- LLM-based compression (local model inference).
- Full property graph across multiple workspaces.
- Multi-user team coordination.
- Memory synthesis from trajectories.
- Distributed code indexing.
- Direct mutation of private agent harness transcript files.

---

## Definition of Feature Parity

Hypa reaches practical parity when an agent can:

1. install Hypa into its harness in one command;
2. have shell commands transparently rewritten and compressed through Hypa;
3. discover and use Hypa MCP tools for file reads, code search, and context;
4. receive compact file views (map, signatures, lines:N-M);
5. run common shell commands with token-measured compressed output;
6. get session summaries, checkpoints, and handoff blocks;
7. inspect tee artifacts for truncated/failed commands;
8. receive diagnostics about what Hypa can and cannot do;
9. see measurable token savings in `hypa impact`.

Atomic parity goes further: Hypa telemetry becomes structured Atomic run evidence; Atomic generates memories; those memories flow back as context packs for the next run. The active-run manifest protocol and unified auth credential store are the integration seam between the two tools.
