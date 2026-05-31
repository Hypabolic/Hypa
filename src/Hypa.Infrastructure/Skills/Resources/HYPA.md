# Hypa Rules

When the `hypa_shell` MCP tool is available, use it for all shell commands — it applies
Hypa compression transparently and tracks sessions without requiring hook support.

When calling CLI tools directly, use the Hypa wrappers:
- `hypa git <args>` instead of `git <args>`
- `hypa dotnet <args>` instead of `dotnet <args>`
- `hypa docker <args>` instead of `docker <args>`
- `hypa kubectl <args>` instead of `kubectl <args>`

When MCP is not available, wrap any other shell command with `hypa -c "<command>"`.

## Code intelligence

Use `hypa_code` (MCP) or `hypa code` (CLI) to index and query code structure. Indexing
is incremental by default — only changed files are re-parsed.

```bash
hypa code index                  # incremental index (default)
hypa code index --full           # force complete re-index
hypa code symbols --query Foo    # find symbols
hypa code graph --callers <id>   # dependency graph
```

## Markdown queries

Large Markdown files read via the `Read` tool are automatically compressed to a heading
outline. To read a specific section, use `hypa md` instead of reading the raw file:

```bash
hypa md README.md --toc                    # table of contents
hypa md README.md --toc --depth 2          # limit heading depth
hypa md docs/guide.md --section "Install"  # section body by heading path or text
hypa md docs/guide.md --frontmatter        # frontmatter YAML
hypa md README.md --json                   # all output as JSON
```

`hypa md` auto-indexes the file if it has changed — no manual `hypa code index` needed.

## Setup
Run `hypa init --global` once to wire hooks and MCP into your agent harness.
Run `hypa doctor` to verify installation health.
