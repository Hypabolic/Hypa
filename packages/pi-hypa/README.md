# pi-hypa

Pi extension package for Hypa. Installing this package through Pi also installs `@hypabolic/hypa` as a package dependency and creates a best-effort user-level `hypa` shim when no `hypa` command is already on `PATH`.

The current package provides:

- bash rewrite interception via `hypa rewrite --json`
- `/hypa` diagnostics
- CLI-backed tools: `hypa_shell`, `hypa_read`, `hypa_grep`, `hypa_find`, `hypa_ls`
- optional Hypa MCP proxy discovery tool: `hypa_mcp_proxy`

Bash interception mutates the Pi `bash` command before execution when Hypa returns `Rewritten` or `GenericWrapper`.

## Install / smoke

```bash
pi -e ./packages/pi-hypa/extensions/index.ts
# or
pi install ./packages/pi-hypa
# after release
pi install npm:@hypabolic/pi-hypa
```

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
- `Ask` confirms in UI mode and uses deterministic non-UI fallback.
- `hypa_*` tool outputs are capped at 50KB / 2000 lines; truncated full output is saved to a temp file.
