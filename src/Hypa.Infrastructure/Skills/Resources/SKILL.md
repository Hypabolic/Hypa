---
name: hypa
description: Context-optimized command runtime. Rewrites verbose commands into token-efficient equivalents. Use instead of running git/dotnet/docker/kubectl directly.
---

<!-- section:1 -->
## Setup + Core Usage

Hypa is a context-aware command runtime that rewrites verbose tool commands into token-efficient equivalents.

**Setup check:** `which hypa || hypa init --global`

**Core usage:**
- `hypa` — start or reconnect the local mux and attach
- `hypa --skill` — print the agent-runtime skill (not this compression skill)
- `hypa git status` — compressed git output
- `hypa dotnet build` — compressed build output
- `hypa -c "any-cmd"` — generic wrapper for any command
- `hypa serve` — MCP stdio server (not the mux host; mux is `hypa mux serve`)

**F1 workspace floor:** Install and run `hypa` for a local agent workspace.
Unix panes use `hypa-pty-host` and Ghostty VT. Missing native fails closed.
Compression (`-c`, rewrites) stays first-class.
The attach status bar shows session, workspace, pane, and agent chips.
Window title tokens and `client.window_title.set` / `clear` are in tree.
M6 is the attach UX axis. Gate `h45_goldens: recorded`. F2 is the VT package floor plus recorded goldens.

`pane.bell` is a reliable control resource event carrying `pane_id` and a
per-output-chunk `count`. Ground BEL in pane output raises the count; OSC BEL
remains a terminator. Attach ringing
is gated until a NotificationDirector exists.
The default install is F1 (`hypa-<rid>.tar.gz`).
The F2 archive is `hypa-f2-<rid>.tar.gz`.
F2 is the Ghostty package floor plus recorded goldens.
The F2 ship matrix is linux-arm64, osx-arm64, and osx-x64.
linux-x64 Ghostty AOT is a residual.
F1 includes Ghostty. F2 writes `hypa.channel` with token `f2`.
`HYPA_VT_PROVIDER=basic` is rejected.
Gate `h15_goldens: recorded`.
`hypa-runtime` is an internal debug alias.

Workspace resources support `workspace.move`, `workspace.move_block`, and
`workspace.report_metadata`. Pane reports (`pane.report_agent`,
`pane.report_agent_session`, `pane.report_metadata`, `pane.release_agent`,
`pane.clear_agent_authority`) set semantic authority and pane tokens.
`pane.report_agent` accepts optional `agent_session_id` or `agent_session_path`.
Display tokens follow the single resource-agnostic contract
in `docs/architecture/display-tokens.md`; Space rows can render `$name` from workspace
tokens; Agent rows can render `$name` from pane tokens. Last-workspace
close remains the last-workspace close rule. A non-owner clear returns `-32005`.

Mux topology (F1 workspace layout): `tab.create`, `tab.list`, `tab.focus`, `pane.split`, `pane.zoom`, `pane.focus_direction`, `pane.swap`, `layout.export`, `layout.apply`, `layout.set_split_ratio`, `agent.start`, `pane.rename`, `pane.current`, `pane.neighbor`, `pane.edges`, `pane.process_info`, `pane.focus`, `pane.send_input`, `pane.input.set`. `pane.swap` is same-tab only (`direction` or `target_pane_id`). `pane.send_input` is key-combo input; it is not `pane.send_text`. Combined `text` plus `keys` writes text first, then keys. `agent.start` starts an occupant on an existing pane and does not create tabs or splits. Health capabilities include `layout`. CLI `hypa pane swap`.

Exit codes:
- Runtime commands (`hypa git`, `hypa dotnet`, `hypa docker`, `hypa kubectl`, `hypa -c`) return the wrapped command's exit code.
- Rewrite decision codes (0/1/2/3) are exposed by `hypa rewrite`.

<!-- section:2 -->
## Command Reference

**Git:** `hypa git status`, `hypa git diff`, `hypa git log --oneline`
**Dotnet:** `hypa dotnet build`, `hypa dotnet test`, `hypa dotnet restore`
**Docker:** `hypa docker ps`, `hypa docker logs <container>`, `hypa docker build .`
**Kubectl:** `hypa kubectl get pods`, `hypa kubectl describe pod <name>`, `hypa kubectl logs <pod>`
**Generic:** `hypa -c "any command"` — wraps and compresses output of any command

**Rewrite decisions:**
- Rewritten: hypa substitutes the command with a compressed equivalent
- Passthrough: command runs unchanged (unsafe or unsupported)
- Deny: command blocked by policy
- Ask: user confirmation required

<!-- mcp-start -->
<!-- section:3 -->
## MCP Tools Reference

Hypa exposes MCP tools to agents when running as an MCP server (`hypa serve`).

### hypa_shell
Run shell commands with compression, rewrite rules, and evidence recording.
```
hypa_shell(command, cwd?, mode?, timeoutMs?)
mode: omit for compressed output | "raw" for uncompressed
```

### hypa_read
Read files in context-aware modes. Prefer over the native Read tool for code files — returns symbol-aware outlines that fit more structure in fewer tokens.
```
hypa_read(path, mode?, maxTokens?)
mode: smart (default) | full | outline | signatures | pruned
```
| Mode | Returns |
|------|---------|
| `smart` | Auto-selects best mode for the file type and size |
| `full` | Complete file content (cached) |
| `outline` | Top-level symbols with children |
| `signatures` | Function/method signatures only |
| `pruned` | File with low-signal sections removed |

### hypa_search
Search files, symbols, and indexed context.
```
hypa_search(query, scope?, kind?, maxResults?)
scope: project | session | code | docs
kind: text (default) | regex | symbol
```

### hypa_code
Code intelligence: index, symbol queries, reference graph, diagnostics.
```
hypa_code(action, path?, symbol?)
action: index | symbols | references | graph | diagnostics
```
`index` is mutating — blocked in read-only mode.

### hypa_mcp
MCP proxy — invoke tools on configured upstream servers, search across server tool schemas, and check auth status.
```
hypa_mcp(action, server?, tool?, arguments?, hint?, requests?, query?)
action: invoke | batch | schema | search | auth_check
```
| Action | Description |
|--------|-------------|
| `invoke` | Call a tool on an upstream server. `server` and `tool` required. `arguments` is a JSON object string. `hint`: raw \| summary \| structured |
| `batch` | Call multiple tools in parallel. `requests` is a JSON array of `{server, tool, arguments?, hint?}` objects |
| `schema` | Show tool schemas. Filter by `server` or leave blank for all |
| `search` | Search for tools by name or description. `query` required |
| `auth_check` | Check authentication status for a server |

### hypa_compress
Compress explicit text. Useful for large tool outputs or logs before storing.
```
hypa_compress(input, kind?)
kind: shell-output | log | code | generic
```

### hypa_session
Inspect and mutate local session state.
```
hypa_session(action, sessionId?, text?, category?)
action: status | init | attach | checkpoint
```
<!-- mcp-end -->
<!-- section:4 -->
## Code Intelligence

Index and query source code structure. Indexing is incremental by default — only files
that have changed since the last run are re-parsed.

**Via CLI:**
```bash
hypa code index                  # incremental index (default)
hypa code index --full           # force complete re-index
hypa code index --path src/      # index a specific path
hypa code symbols --query Foo    # symbols whose name contains "Foo"
hypa code symbols --kind class   # all classes
hypa code graph --callers <id>   # what calls this symbol
hypa code diagnostics            # list diagnostics
```

Supported languages: C#, TypeScript, TSX, JavaScript, JSX, Python, Go, Rust, Java, C, C++, Bash, JSON, YAML, TOML, Markdown.

<!-- section:5 -->
## Markdown Queries

Large Markdown files read via the `Read` tool are automatically compressed to a heading
outline — the agent sees the structure, not the full content. To read a specific section,
use `hypa md` rather than reading the raw file.

```bash
hypa md README.md                          # table of contents (default)
hypa md README.md --toc --depth 2          # limit heading depth
hypa md docs/guide.md --section "Install"  # section body by heading path or text
hypa md docs/guide.md --section "Getting Started/Prerequisites"
hypa md docs/guide.md --frontmatter        # frontmatter YAML
hypa md README.md --json                   # all output as JSON
```

`hypa md` auto-indexes the file on first use and re-indexes if the file has changed —
no manual `hypa code index` needed before querying markdown.

| Flag | Default | Description |
|------|---------|-------------|
| `--toc` | implied | Table of contents |
| `--depth <n>` | 3 | Max heading level for `--toc` |
| `--section <path>` | — | Section body by heading path or text |
| `--frontmatter` | — | Frontmatter YAML |
| `--json` | — | JSON output |

<!-- mcp-start -->
<!-- section:6 -->
## MCP Server Management

```bash
hypa serve                              # start MCP stdio server
hypa serve --read-only                  # disable mutating tools (index, shell writes)
hypa serve --tool hypa_shell            # expose only specific tools

hypa mcp list                           # list configured upstream servers
hypa mcp add <name> <url>              # add an upstream server
hypa mcp import                        # import servers from Claude/Codex config
hypa mcp tools                         # list all tools across all servers
hypa mcp schema --server <name>        # show tool schemas for a server
hypa mcp search <query>                # find tools by name or description
hypa mcp invoke <server> <tool> [args] # call a tool directly
hypa mcp auth check --server <name>    # check authentication status
hypa mcp auth login --server <name>    # initiate OAuth2 login
```
<!-- mcp-end -->
<!-- section:7 -->
## Session + Trust + Filters

**Sessions:** `hypa session list`, `hypa session show <id>`
**Trust:** `hypa trust status`, `hypa trust add <pattern>`
**Filters:** `hypa filters list`, `hypa filters add <name>`

**Custom filter DSL example:**
```json
{
  "name": "my-filter",
  "stages": [{"kind": "grep", "pattern": "error|warning"}]
}
```

<!-- section:8 -->
## Advanced / Analytics

**Savings:** `hypa filters savings` — estimates token savings for current session
**Diagnostics:** `hypa doctor` — health check for all subsystems

**`hypa rewrite` exit code contract:**
| Code | Meaning |
|------|---------|
| 0    | Rewrite (use the rewritten command) |
| 1    | Passthrough (run original command) |
| 2    | Deny (do not run) |
| 3    | Ask (confirm with user) |
