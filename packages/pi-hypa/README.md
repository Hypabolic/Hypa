# pi-hypa

**The Pi extension for Hypa — a local context runtime for coding agents.**

[![npm](https://img.shields.io/npm/v/@hypabolic/pi-hypa?color=cb3837&logo=npm)](https://www.npmjs.com/package/@hypabolic/pi-hypa)
[![CI](https://github.com/Hypabolic/Hypa/actions/workflows/ci.yml/badge.svg)](https://github.com/Hypabolic/Hypa/actions/workflows/ci.yml)
[![GitHub](https://img.shields.io/github/stars/Hypabolic/Hypa?style=flat&logo=github)](https://github.com/Hypabolic/Hypa)
[![License](https://img.shields.io/badge/license-FSL--1.1--ALv2-blue)](https://github.com/Hypabolic/Hypa/blob/main/license.md)

`@hypabolic/pi-hypa` wires [Hypa](https://github.com/Hypabolic/Hypa) into the [Pi coding agent](https://www.npmjs.com/package/@earendil-works/pi-coding-agent). Hypa reduces the noisy tool output that reaches an agent's context window: it runs locally, compresses shell output with deterministic reducers, exposes context-aware file and code tools, and records enough evidence to recover the details that matter.

```text
Pi bash / tool call
        ↓
      Hypa
        ↓
errors · warnings · changed files · failing tests · exit codes
```

Hypa is not an LLM summarizer. The default reduction path is local, deterministic, and testable — your source code and command output do not need to be sent to a separate cloud service.

## What this extension adds

Installing this package through Pi also installs `@hypabolic/hypa` as a package dependency and creates a best-effort user-level `hypa` shim when no `hypa` command is already on `PATH`. The shim delegates to a later global/system `hypa` install if one appears earlier on `PATH`, and otherwise falls back to the bundled dependency.

The extension provides:

- **Bash rewrite interception** via `hypa rewrite --json` — Pi's `bash` command is mutated before execution when Hypa returns `Rewritten` or `GenericWrapper`, so command output is compressed in place.
- **`/hypa` diagnostics** — inspect extension mode, binary resolution, MCP proxy setting, and the last rewrite status.
- **CLI-backed tools** — `hypa_shell`, `hypa_read`, `hypa_grep`, `hypa_find`, `hypa_ls`.
- **Optional Hypa MCP proxy** — the `hypa_mcp_proxy` discovery tool for upstream MCP servers.

## Install

This extension is installed automatically when you run `hypa init --agent pi` (or `hypa init` with Pi detected). To install or test it directly:

```bash
pi -e ./packages/pi-hypa/extensions/index.ts
# or
pi install ./packages/pi-hypa
# from npm
pi install npm:@hypabolic/pi-hypa
```

### Requirements

- Pi with the `@earendil-works/pi-coding-agent` peer dependency.
- Node.js 18 or newer.
- Linux, macOS, or Windows on x64 or arm64 (the bundled `@hypabolic/hypa` selects the matching native binary).

## Configuration

| Variable | Default | Description |
|---|---|---|
| `HYPA_BIN` | bundled `@hypabolic/hypa`, then `hypa` | Hypa executable or absolute path. |
| `HYPA_PI_MODE` | `additive` | `additive` keeps Pi builtins; `replace` disables Pi `bash/read/grep/find/ls` after registering `hypa_*` tools. |
| `HYPA_PI_REWRITE_TIMEOUT_MS` | `5000` | Rewrite CLI timeout in milliseconds. |
| `HYPA_PI_ASK_NON_INTERACTIVE` | `deny` | `Ask` fallback when `ctx.hasUI === false`: `deny` or `allow`. |
| `HYPA_PI_ENABLE_MCP_PROXY` | `0` | Enable `hypa_mcp_proxy`, a lazy discovery/invocation bridge for upstream MCP servers configured in Hypa. |
| `HYPA_PI_ENABLE_MCP` | unset | Legacy alias for `HYPA_PI_ENABLE_MCP_PROXY` if needed. |
| `HYPA_PI_MCP_PROXY_TIMEOUT_MS` | `10000` | Timeout for `hypa mcp ...` proxy calls. |
| `HYPA_PI_MCP_CONFIG` | `~/.pi/agent/mcp.json` | Pi MCP config path used to deduplicate Hypa upstream servers already configured directly in Pi. |

## CLI-backed tools

When registered, the extension exposes Hypa-backed equivalents of Pi's file and shell builtins. In `additive` mode they sit alongside Pi's own tools; in `replace` mode they take over.

| Tool | Purpose |
|---|---|
| `hypa_shell` | Run shell commands with rewrite rules, compression, and evidence recording. |
| `hypa_read` | Read files with full, outline, signatures, pruned, or smart selection. |
| `hypa_grep` | Search file contents with safe ripgrep options. |
| `hypa_find` | Find files with an optional result limit. |
| `hypa_ls` | List directory contents. |

`hypa_*` tool outputs are capped at 50KB / 2000 lines; truncated full output is saved to a temp file for recovery.

## MCP proxy discovery

When `HYPA_PI_ENABLE_MCP_PROXY=1`, the extension registers one compact tool, `hypa_mcp_proxy`, instead of dumping every upstream MCP server/tool into Pi context.

Supported actions:

- `list` — compact list of upstream MCP servers configured in Hypa
- `search` — search upstream tools by query
- `schema` — fetch details/schema on demand for a selected server
- `invoke` — invoke a selected upstream tool through Hypa's proxy/passthrough service
- `auth_check` — validate auth for a selected upstream server

Servers already configured directly in Pi are filtered by default. Pass `includeDuplicates=true` to inspect/invoke through Hypa anyway.

## Diagnostics

Run `/hypa` in Pi to show extension mode, binary resolution, MCP proxy setting, and the last rewrite status/error.

## Safety

- Commands already starting with `hypa` are not rewritten.
- Parse, timeout, or process errors fail open by passing the original command through and recording diagnostics.
- `Deny` blocks the tool call.
- `Ask` confirms in UI mode and uses a deterministic non-UI fallback.
- `hypa_*` tool outputs are capped at 50KB / 2000 lines; truncated full output is saved to a temp file.

## Documentation

- [Pi integration guide](https://github.com/Hypabolic/Hypa/blob/main/docs/guides/pi.md)
- [Hypa documentation](https://hypabolic.dev/products/hypa/docs)
- [GitHub repository](https://github.com/Hypabolic/Hypa)
- [`@hypabolic/hypa` on npm](https://www.npmjs.com/package/@hypabolic/hypa)
- [Issue tracker](https://github.com/Hypabolic/Hypa/issues)

## License

Hypa is licensed under the [Functional Source License 1.1 with an Apache License 2.0 future license](https://github.com/Hypabolic/Hypa/blob/main/license.md).
