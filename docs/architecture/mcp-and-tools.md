# MCP and Tool Surface

Hypa exposes structured capabilities to agents through MCP. The MCP surface should be compact, stable, and intentionally designed for agent use. It should not expose every internal operation as a separate top-level tool.

## Tool Design Goals

- Minimise schema/token overhead.
- Keep tool outputs compact and agent-actionable.
- Prefer stable, high-value tools over many narrow tools.
- Support discovery for optional capabilities.
- Keep local-only operation and Atomic-bound operation behind the same tool contracts where possible.
- Record evidence and provenance for all meaningful calls.

## Initial Tool Set

Hypa should start with a small set of tools:

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

A future meta-tool can multiplex subcommands if schema overhead becomes a problem:

```text
hypa(tool = "read", ...)
hypa(tool = "shell", ...)
hypa(tool = "code", action = "symbol", ...)
```

## Tool: hypa_session

Purpose: inspect and mutate local session state.

Actions:

```text
status
ensure
attach
detach
checkpoint
resume-block
record-finding
record-decision
list-evidence
```

Example arguments:

```json
{
  "action": "attach",
  "atomicAgentSessionId": "...",
  "jobId": "...",
  "runId": "...",
  "stepId": "..."
}
```

Output should include the active session ID, binding, project root, recent findings/decisions, and stats.

## Tool: hypa_shell

Purpose: run shell commands with optional compression and evidence recording.

Arguments:

```json
{
  "command": "dotnet test",
  "cwd": ".",
  "mode": "auto | raw | compress | track",
  "timeoutMs": 120000
}
```

Behaviour:

- apply passthrough rules for unsafe/interactive commands;
- compress when beneficial;
- preserve exit code;
- record command evidence;
- optionally tee raw output to artifact storage.

## Tool: hypa_read

Purpose: read files in context-aware modes.

Arguments:

```json
{
  "path": "src/Hypa.Runtime/SessionManager.cs",
  "mode": "smart | full | outline | signatures | pruned",
  "maxTokens": 4000
}
```

Read modes:

```text
full        raw content
outline     imports and top-level symbols
signatures  declarations with parameters/returns
pruned      signatures plus body placeholders
smart       runtime chooses based on size/cache/task
```

## Tool: hypa_search

Purpose: search files, symbols, and indexed context.

Actions:

```text
text
regex
symbol
semantic, later
recent
```

Arguments:

```json
{
  "query": "SessionBinding",
  "scope": "project | session | code | docs",
  "kind": "text | symbol | regex",
  "maxResults": 20
}
```

## Tool: hypa_code

Purpose: code intelligence queries.

Actions:

```text
index
symbols
references
definition
callers
callees
graph
impact
diagnostics
```

Examples:

```json
{
  "action": "symbols",
  "path": "src/Hypa.Runtime/SessionManager.cs"
}
```

```json
{
  "action": "references",
  "symbol": "Hypa.Runtime.SessionManager.AttachAsync"
}
```

## Tool: hypa_context

Purpose: produce compact context packs for agents.

Actions:

```text
overview
pack
expand
handoff
preload
```

Examples:

```json
{
  "action": "pack",
  "task": "Implement deterministic dotnet test compression",
  "maxTokens": 6000
}
```

The context pack should combine relevant session state, code map entries, findings, decisions, files touched, and artifact references.

## Tool: hypa_compress

Purpose: compress explicit text or a referenced artifact.

Arguments:

```json
{
  "input": "...",
  "kind": "shell-output | log | code | generic",
  "command": "dotnet test",
  "maxTokens": 2000
}
```

This supports agents that already have output and want to reduce it before proceeding.

## Tool: hypa_diagnostics

Purpose: expose runtime health and capability state.

Actions:

```text
status
providers
language-servers
shell-hooks
agent-hooks
storage
atomic-auth
config
```

Example output:

```text
Hypa runtime: healthy
Session: active, project-bound, not Atomic-bound
Compression reducers: 8 loaded
Roslyn: available
Tree-sitter: available
Language servers: rust-analyzer healthy, pyright not found
Atomic: not authenticated
```

## Output Contract

Tool outputs should be compact and structured enough for agents.

Recommended format:

```text
SUMMARY
<short result>

DETAILS
<important facts>

REFERENCES
<artifact refs, file refs, symbol refs>

STATS
<tokens, compression, duration where useful>
```

Avoid dumping raw large JSON unless the agent explicitly requests machine-readable output.

## Lazy Tool Exposure

If MCP schema overhead becomes material, expose a small default set and add discovery:

```text
hypa_diagnostics action=tools
hypa_context action=discover
```

Optional advanced tools can be hidden until requested.

## Permissions and Safety

Shell execution is powerful. The runtime should support policy hooks:

```text
allow shell execution
block destructive commands
require approval for network commands
allow read-only tools only
restrict cwd to project root
```

For local operation, policy can start permissive but observable. For Atomic-bound or multi-agent operation, policy should become explicit.

## Evidence Recording

Every MCP tool call should record:

```text
tool name
canonical arguments hash
output hash
token counts
duration
provider/reducer used
session binding
artifact refs
```

The evidence record should not duplicate huge inputs/outputs. Use hashes and artifact references.

## Agent Instructions

Hypa should generate concise agent instructions for supported hosts:

```text
Use hypa_read instead of repeatedly reading large files.
Use hypa_shell for build/test commands.
Use hypa_code for symbols, references, and graph questions.
Record durable findings and decisions through hypa_session.
Attach job/run/step IDs when available.
```

Host-specific hook adapters can inject or install these instructions where appropriate.
