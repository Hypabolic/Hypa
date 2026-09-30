# Background panes

This guide is for an agent that creates or reveals a hidden pane.
The user-facing binary is `hypa`.

A hidden pane has a live PTY and Ghostty VT. It has no tiled leaf.
Hide removes the leaf. Close ends the runtime.

This is a Hypa product slice.

Wire fields and secret lifetime:
[`../architecture/hidden-authority-contract.md`](../architecture/hidden-authority-contract.md).

## Identity

(`src/pane.rs` 138-156, `src/integration/env.rs` 8).
Hypa writes `HYPA_PANE_ID`. That id is public. It is not a credential.

| Key | Secret | Use |
| --- | --- | --- |
| `HYPA_PANE_ID` | no | Public pane id. CLI `--pane-id` when the flag is absent. |
| `HYPA_PANE_TOKEN` | yes | Occupant credential. CLI `--occupant-token` when the flag is absent. |

Create proof uses `--parent-pane-id` plus the parent occupant token.
Show and hide proof uses `--occupant-token` or `--parent-capability`.
`--mode` is `tiled` or `overlay`. Default is `tiled`.
Overlay also needs `--attach-client-id`.

The CLI copies `HYPA_PANE_ID` and `HYPA_PANE_TOKEN` when those flags
are absent. An explicit `--parent-capability` stops that token copy.

Do not print `HYPA_PANE_TOKEN` or a parent capability.

`blocked` is attention. It does not grant show or hide.

## Sequence

Occupant and parent each keep one `seq` per pane and credential.
Start at `1`. Bind the counter to that actor.

```bash
SEQ=1
```

Pass `--seq "$SEQ"`. Increment only after the call exits `0`.
A failed persist keeps the same `seq`. Retry that number.

A stale `seq` exits `0` with `changed` false. The mux does not
mutate. The next call still needs a higher `seq`.

## Create a hidden child

Run this from the parent pane.

```bash
created=$(hypa pane create --placement hidden --command /bin/cat \
  --parent-pane-id "$HYPA_PANE_ID" --occupant-token "$HYPA_PANE_TOKEN")
CHILD=$(jq -r .pane_id <<<"$created")
PARENT_CAP=$(jq -r .parent_capability <<<"$created")
```

Read the JSON with `jq -r`.
Do not run `eval` on the JSON.
If `jq` is not installed, `python3` can read the same fields.

`parent_capability` appears on that create result only.
`hypa pane get`, `hypa pane list`, and `hypa snapshot` omit it.
Keep `$PARENT_CAP` in the shell. Do not write it to the terminal.

## Occupant tiled show and hide

The occupant already has `HYPA_PANE_ID` and `HYPA_PANE_TOKEN`.

```bash
SEQ=1
hypa pane show --mode tiled --seq "$SEQ" && SEQ=$((SEQ + 1))
```

That call opens a new tab when the pane is hidden and the owner
tab already has leaves. A later show of the same tiled pane
returns `changed` false. The tab stays the same.

Split beside a neighbour.

```bash
hypa pane show --mode tiled --seq "$SEQ" \
  --target-pane-id "$NEIGHBOUR" --direction down --ratio 0.3 \
  && SEQ=$((SEQ + 1))
```

`--direction` is `right` or `down`. `--ratio` is in `(0,1)`.
`--tab-id` selects the dest tab. `--no-focus` keeps session focus.

Hide. The PTY stays alive.

```bash
hypa pane hide --seq "$SEQ" && SEQ=$((SEQ + 1))
```

## Parent tiled show and hide

The parent uses `$CHILD` and `$PARENT_CAP`.
It starts its own `seq` for that child.

```bash
PSEQ=1
hypa pane show --pane-id "$CHILD" --parent-capability "$PARENT_CAP" \
  --mode tiled --seq "$PSEQ" && PSEQ=$((PSEQ + 1))
hypa pane hide --pane-id "$CHILD" --parent-capability "$PARENT_CAP" \
  --seq "$PSEQ" && PSEQ=$((PSEQ + 1))
```

## Questions and permission requests

A question or permission request is a normal hidden pane.
Use tiled show for a tab. Use overlay show for a modal on one
attach client.

Pick the overlay owner from `hypa snapshot` `attach_clients`.
Each row has `attach_client_id`, `mode`, and `client_mode`.
Use the row whose `client_mode` is `terminal`.
Require exactly one such row. Discovery must exit `0` before
`pane show`. Pass `--attach-client-id` yourself when more than
one client is ready. Do not take an id from `attach_client_ids`
when `client_mode` is missing or not `terminal`.

```bash
if CLIENT=$(hypa snapshot | jq -er '
  [ .attach_clients[]?
    | select((.client_mode // "" | ascii_downcase) == "terminal")
    | .attach_client_id
    | select(type == "string" and length > 0)
  ]
  | if length == 1 then .[0]
    else error("need exactly one terminal attach client")
    end
'); then
  hypa pane show --mode overlay --seq "$SEQ" --attach-client-id "$CLIENT" \
    && SEQ=$((SEQ + 1))
fi
```

If `jq` is not installed, `python3` can read the same snapshot.
Do not run `eval` on the JSON.

The parent uses the same client rule and its own `seq`.

```bash
hypa pane show --pane-id "$CHILD" --parent-capability "$PARENT_CAP" \
  --mode overlay --seq "$PSEQ" --attach-client-id "$CLIENT" \
  && PSEQ=$((PSEQ + 1))
```

A missing `--attach-client-id` is `-32602`.
An unknown id is `-32602`.
A busy modal surface is `-32005` with message `ui_busy`.

`client_mode` is ready only when it is `terminal`.
Settings, copy, overlay, and other chrome modes are busy.
A second overlay on that client is busy.
A live command popup on that client is busy.

On `ui_busy`, wait. Read snapshot again. Need one `terminal` row.
Retry the same `seq` if the call did not exit `0`.

Click outside the overlay rect does not dismiss.
Escape or `hypa pane hide --seq "$SEQ"` returns the pane to hidden.
Then increment `seq` when that hide exits `0`.

## Hide keeps the PTY. Close ends it.

(`src/layout.rs` 233-252). Hypa hide copies that leaf removal.
Hypa does not kill the PTY (`AppState.TryHideTiled`).

```bash
hypa pane hide --seq "$SEQ" && SEQ=$((SEQ + 1))
```

The pane stays in the session. Placement is `hidden`.
VT and scrollback stay.

```bash
hypa pane close "$CHILD"
```

Close disposes the runtime. The occupant token dies.
The parent capability dies.

## Human UI

The Agents sidebar shows hidden children below their validated parent.
A missing parent puts the child in its workspace background group.
Right-click the child for Show in new tab, Split right, Split below,
Show modal, or Close. Use Hide to return a revealed pane to the background.
The global hidden-pane menu provides the same actions on narrow displays.

Those actions use the attach socket. The attach client claims its own
lease on that socket.

A `hypa` CLI process is a different connection. Do not copy a
`--lease-id` from the UI into the CLI. A long-lived control
client must claim and use the lease on the same socket.

Status on a row is attention. It is not placement proof.

## Capability lifetime

The mux holds secrets in process memory only.

- Spawn issues one occupant token for that generation.
- Successful create issues one parent capability for that child.
- Occupant replace issues a new occupant token.
- Parent capability survives occupant replace on the same pane.
- Pane close revokes both secrets.
- Process exit revokes the occupant token.
- Parent capability stays until pane close.

Reconnect restore persists `parent_pane_id` and placement.
It does not persist secrets. After restart, parent capability
is gone. The new occupant uses the new token.

Do not write secrets to `layout.export`, the journal, logs,
`pane.get`, `pane.list`, or `session.snapshot`.

Restart restore keeps last `hidden` or `tiled` placement.
Zoom of another pane does not reveal a hidden pane.
Resize while hidden keeps the last valid size. It is not zero.

Attach settings (`prefix+s`) do not grant show or hide.

## Errors

CLI exit `1` is a protocol error. Read stderr.

| Code | When |
| ---: | --- |
| `-32009` | Occupant or parent credential is invalid. |
| `-32602` | Unproven parent on create. Missing or unknown overlay owner. |
| `-32005` | Overlay busy. Message is `ui_busy`. |
| `-32011` | Persist failed. Graph rolls back. Retry the same `seq`. |

## One blocking command

A recipe does not poll with a shell loop.
A recipe does not use `sleep`.
The agent runs one blocking command.

Recipe A waits with `pane wait-output`.
The exit status of that call selects the next step.
A timeout exits non-zero.
The message is `timed out waiting for output match`.

Recipe B waits with `agent prompt --wait --until blocked`.
Then read `.state` with `agent status` and `jq -r`.
Show the pane when `.state` is `blocked`.
A timeout of that prompt can still exit `0`.
The JSON field `timed_out` is then true.
Use `.state` for the branch in Recipe B.

`pane run` writes the command text and then Enter.
Enter is the byte CR.
The PTY line discipline maps that CR to NL.
The shell reads the line after that map.
Do not add LF to the command text.

## Recipe A: hidden terminal process

Create a hidden pane.
Run one command with `pane run`.
Wait with one `pane wait-output` call.
When that call exits `0`, read the pane.
Then close the pane.
Do not show the pane on that path.

When that call exits non-zero, show the pane tiled.
The show uses `--seq 1`.
After the user answers, hide the pane with `--seq 2`.
You can close the pane instead of that hide.
Follow the sequence rules above for a failed call.
A stale `seq` does not change the pane.

```bash
created=$(hypa pane create --placement hidden)
CHILD=$(jq -r .pane_id <<<"$created")
CAP=$(jq -r .parent_capability <<<"$created")
hypa pane run "$CHILD" "just test"
if hypa pane wait-output "$CHILD" --match "test result" --timeout 120000; then
  hypa pane read "$CHILD" --source recent-unwrapped --lines 120
  hypa pane close "$CHILD"
else
  hypa pane show --pane-id "$CHILD" --parent-capability "$CAP" --mode tiled --seq 1
fi
```

After the user answers a shown pane, hide it or close it.
Hide keeps the PTY.
Close ends the PTY.
Do not print `$CAP`.

```bash
hypa pane hide --pane-id "$CHILD" --parent-capability "$CAP" --seq 2
```

Close the pane when the work is done.

```bash
hypa pane close "$CHILD"
```

If `jq` is not installed, `python3` can read the same fields.
Do not run `eval` on the JSON.

## Recipe B: hidden helper agent

Create a hidden pane.
Start the agent with `--kind codex`.
Do not pass `--kind` and `--command` together.
Prompt with `--wait` and `--until blocked`.
That prompt is the one blocking wait.
Read `.state` from `agent status` with `jq -r`.

When `.state` is `blocked`, show the pane tiled.
The show uses `--seq 1`.
After the user answers, hide the pane with `--seq 2`.
You can close the pane instead of that hide.
When `.state` is `idle` or `done`, read the agent. Then close the pane.
When the agent still works, for example after a timed-out wait, leave the pane open.

`blocked` is attention.
It does not grant show or hide by itself.
For a modal, use the overlay steps above.

```bash
created=$(hypa pane create --placement hidden)
CHILD=$(jq -r .pane_id <<<"$created")
CAP=$(jq -r .parent_capability <<<"$created")
hypa agent start "$CHILD" --kind codex
hypa agent prompt "$CHILD" "Review the diff. Report actions only." --wait --until blocked --timeout 120000
case "$(hypa agent status "$CHILD" | jq -r .state)" in
  blocked)
    hypa pane show --pane-id "$CHILD" --parent-capability "$CAP" --mode tiled --seq 1 ;;
  idle|done)
    hypa agent read "$CHILD"
    hypa pane close "$CHILD" ;;
  *) echo "helper is still working; leave the pane open" ;;
esac
```

After the user answers a shown pane, hide it or close it.
Hide keeps the PTY.
Close ends the PTY.

```bash
hypa pane hide --pane-id "$CHILD" --parent-capability "$CAP" --seq 2
```

Close the pane when the work is done.

```bash
hypa pane close "$CHILD"
```

If `jq` is not installed, `python3` can read the same fields.
Do not run `eval` on the JSON.

## Evidence

- [`../architecture/hidden-authority-contract.md`](../architecture/hidden-authority-contract.md)
- [`../issues/2026-08-31-h-138-hidden-pane-show-tiled.md`](../issues/2026-08-31-h-138-hidden-pane-show-tiled.md)
- [`../issues/2026-08-31-h-139-modal-agent-pane.md`](../issues/2026-08-31-h-139-modal-agent-pane.md)
- [`../issues/2026-08-31-h-140-hidden-pane-human-menu.md`](../issues/2026-08-31-h-140-hidden-pane-human-menu.md)
- [`../issues/2026-08-31-h-141-hidden-pane-agent-show.md`](../issues/2026-08-31-h-141-hidden-pane-agent-show.md)
- [`../issues/2026-08-31-h-142-hidden-pane-capture-gate.md`](../issues/2026-08-31-h-142-hidden-pane-capture-gate.md)
- [`../issues/2026-08-31-h-143-hidden-pane-lifecycle.md`](../issues/2026-08-31-h-143-hidden-pane-lifecycle.md)
