---
name: hypa
description: Context-optimized command runtime. Rewrites verbose commands into token-efficient equivalents. Use instead of running git/dotnet/docker/kubectl directly.
---

<!-- section:1 -->
## Setup + Core Usage

Hypa is a context-aware command runtime that rewrites verbose tool commands into token-efficient equivalents.

**Setup check:** `which hypa || hypa init --global`

**Core usage:**
- `hypa git status` — compressed git output
- `hypa dotnet build` — compressed build output
- `hypa -c "any-cmd"` — generic wrapper for any command

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

<!-- section:3 -->
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

<!-- section:4 -->
## Code Intelligence

Index and query source code structure. Indexing is incremental by default — only files
that have changed since the last run are re-parsed.

**Via MCP (`hypa_code` tool):**
| Action | Description |
|--------|-------------|
| `index` | Index the current project (incremental). Mutating — blocked in read-only mode. |
| `symbols` | Query indexed symbols by name, kind, or path. |
| `references` | Syntactic reference candidates for a name. |
| `graph` | Dependency edges: callers, callees, inheritance. |
| `diagnostics` | Parse errors and indexing diagnostics. |

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

<!-- section:6 -->
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
