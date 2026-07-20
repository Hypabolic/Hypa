# Hypa + OpenCode

OpenCode integrates with Hypa through the community plugin [`opencode-hypa`](https://github.com/kipyin/opencode-hypa) (npm: [`opencode-hypa`](https://www.npmjs.com/package/opencode-hypa)).

Unlike Claude Code and Codex, OpenCode does not yet have a built-in `hypa init --agent opencode` adapter. The plugin uses OpenCode's native plugin API to hardwire Hypa into bash/shell tool calls — the same rewrite pattern Pi uses — without requiring the model to choose MCP tools.

## Install

```bash
opencode plugin opencode-hypa --global
```

Or add to `~/.config/opencode/opencode.json` / `opencode.json`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "plugin": ["opencode-hypa"]
}
```

### Requirements

- [OpenCode](https://opencode.ai) with plugin support
- Node.js 18+ (or Bun) for the plugin runtime
- Hypa available via `PATH`, or installed as the plugin's dependency (`@hypabolic/hypa`)

## What the plugin does

- Intercepts OpenCode `bash` / `shell` tool calls and asks Hypa for a rewrite via `hypa rewrite --json`.
- Replaces rewritten commands before execution (`Rewritten` / `GenericWrapper`).
- Leaves passthrough commands alone; blocks `Deny` outcomes.
- Fails open on rewrite errors or timeouts so agent workflows keep working.
- Skips commands that already start with `hypa` to avoid double-wrapping.

## Configuration

| Variable | Default | Description |
|---|---|---|
| `OPENCODE_HYPA_ENABLED` | `true` | Set `0`/`false` to disable the plugin. |
| `HYPA_BIN` | `hypa` (PATH / bundled) | Hypa executable or absolute path. |
| `OPENCODE_HYPA_REWRITE_TIMEOUT_MS` | `5000` | Rewrite CLI timeout in milliseconds. |
| `OPENCODE_HYPA_ASK_NON_INTERACTIVE` | `deny` | `Ask` fallback: `deny` or `allow`. |

## Why not MCP alone?

`hypa serve` works as an MCP server, but the model has to choose those tools. The OpenCode plugin rewrites every bash call automatically — the integration that actually saves context.

## Related

- Plugin repository: https://github.com/kipyin/opencode-hypa
- npm package: https://www.npmjs.com/package/opencode-hypa
- OpenCode plugins docs: https://opencode.ai/docs/plugins/
- Pi integration (official package): [docs/guides/pi.md](./pi.md)
