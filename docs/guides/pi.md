# Hypa + Pi

Hypa integrates with Pi through the `@hypabolic/pi-hypa` Pi package.

## Install

After the package is published:

```bash
pi install npm:@hypabolic/pi-hypa
```

For local development from this repository:

```bash
pi -e ./packages/pi-hypa/extensions/index.ts
# or
pi install ./packages/pi-hypa
```

Hypa can also add the package to Pi settings:

```bash
hypa init --agent pi
```

## What the package does

- Intercepts Pi `bash` tool calls and asks Hypa for a rewrite via `hypa rewrite --json`.
- Mutates rewritten bash commands before execution.
- Provides `/hypa` diagnostics.
- Registers CLI-backed tools:
  - `hypa_shell`
  - `hypa_read`
  - `hypa_grep`
  - `hypa_find`
  - `hypa_ls`

## Configuration

| Variable | Default | Description |
|---|---|---|
| `HYPA_BIN` | `hypa` | Hypa executable or absolute path. |
| `HYPA_PI_MODE` | `additive` | `additive` keeps Pi builtins; `replace` disables Pi `bash/read/grep/find/ls` after registering `hypa_*` tools. |
| `HYPA_PI_REWRITE_TIMEOUT_MS` | `5000` | Rewrite CLI timeout in milliseconds. |
| `HYPA_PI_ASK_NON_INTERACTIVE` | `deny` | `Ask` fallback when `ctx.hasUI === false`: `deny` or `allow`. |

## Release path

This repository syncs `packages/pi-hypa` and `.github/workflows/pi-package-release.yml` into `Hypabolic/Hypa` through `.github/workflows/sync-public.yml`.
The public repository publishes `@hypabolic/pi-hypa` from tags using GitHub Actions trusted publishing.

Before the first public release, ensure npm trusted publishing is configured for `@hypabolic/pi-hypa`:

- Provider: GitHub Actions
- Owner: `Hypabolic`
- Repository: `Hypa`
- Workflow: `pi-package-release.yml`

If npm requires the package to exist before trusted publishing can be configured, bootstrap `@hypabolic/pi-hypa` once under a non-latest tag, then configure trusted publishing and let release tags publish the real version.
