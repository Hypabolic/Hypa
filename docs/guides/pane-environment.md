# Pane environment

This guide lists the environment keys on a pane child.
The user-facing binary is `hypa`.
The mux sets these keys when it starts the child.

A popup child does not receive pane identity keys.
A popup child still receives `HYPA_ENV`, `HYPA_BIN_PATH`, and `HYPA_RUNTIME_SOCKET` when those keys apply.

## Keys

| Key | Secret | Meaning |
| --- | --- | --- |
| `HYPA_ENV` | no | The value is `1` on a Hypa child. Report state only when this value is `1`. |
| `HYPA_BIN_PATH` | no | Path of the `hypa` CLI. Call this file to run Hypa commands. |
| `HYPA_RUNTIME_SOCKET` | no | Path of the mux socket. The mux sets this key when it listens. |
| `HYPA_PANE_ID` | no | Public pane id. It is not a credential. |
| `HYPA_TAB_ID` | no | Public tab id. |
| `HYPA_WORKSPACE_ID` | no | Public workspace id. |
| `HYPA_PANE_TOKEN` | yes | Occupant credential. Do not print this value. |

## Popup children

A popup child does not see `HYPA_PANE_ID`.
A popup child does not see `HYPA_TAB_ID`.
A popup child does not see `HYPA_WORKSPACE_ID`.
A popup child does not see `HYPA_PANE_TOKEN`.

## CLI path

The mux process can be `hypa` or `hypa-runtime`.
`HYPA_BIN_PATH` names the `hypa` CLI.
When the mux file is not named `hypa`, the mux selects the `hypa` file in the same directory.
The mux also checks the directory of the final link target.

## Socket

`HYPA_RUNTIME_SOCKET` is the mux listen path.
A pane child can open that path when the key is set.
The key is absent when the mux has no socket.

## Related documents

- Custom agent integration: [`custom-agent-integration.md`](custom-agent-integration.md)
- Background panes: [`background-panes.md`](background-panes.md)
