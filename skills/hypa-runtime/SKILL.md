---
name: hypa-runtime
description: "Control Hypa, a terminal mux for coding agents. Use only when the user explicitly mentions Hypa or asks to use Hypa to inspect or control panes, tabs, workspaces, commands, or another agent. Do not use merely because a task could benefit from a background terminal, delegation, or parallel work. Requires HYPA_ENV=1."
---

# Hypa runtime

Hypa organizes terminals into workspaces, tabs, and panes.
It detects coding agents in panes and gives you the `hypa` CLI to control the current session.

Before you send any control command, make sure this agent runs in a Hypa pane:

```bash
test "${HYPA_ENV:-}" = 1
```

If the check fails, say that you are not in Hypa and stop.
Do not inspect or control a Hypa session from outside Hypa.

When the check passes, the `hypa` in `PATH` talks to the current session.

## Learn the current CLI

The installed binary is the authority for command syntax. Start with:

```bash
hypa --help
```

Then print each command group. Run the group with no subcommand:

```bash
hypa workspace
hypa tab
hypa pane
hypa agent
hypa layout
hypa integration
hypa notification
hypa terminal
hypa events
```

Do not run bare `hypa` for discovery. Bare `hypa` starts an attach.
Do not probe a command that changes state by leaving out its arguments.

Most commands print JSON. Read IDs and state from that JSON. Do not guess them.
Use `jq -r` to read one field. Do not `eval` JSON.

## Layout, panes, and agents

Choose the primitive that fits the job:

- Workspace, tab, and pane commands set where terminals live.
- Pane commands control raw terminals: shells, tests, servers, input, and output.
- Agent commands control the coding agent that occupies a pane.

A pane exists with or without an agent.
`agent start` needs an existing pane. It does not create or move layout.
Use pane commands for plain processes.
Use agent commands when Hypa must check the agent and read its state.

| State | Meaning |
| --- | --- |
| `working` | The agent is producing output or running tools. |
| `blocked` | The agent needs a person or a peer to answer. |
| `done` | The agent finished. Nobody looked at it yet. |
| `idle` | The agent waits at a prompt. |
| `unknown` | Hypa cannot classify the agent yet. This is not `idle`. |

`idle` and `done` both mean that the agent can take input.

## IDs and caller context

An ID is an opaque 12-character token. Do not build or predict one.
Hypa sets these variables in each pane:

```bash
printf '%s\n' "$HYPA_WORKSPACE_ID" "$HYPA_TAB_ID" "$HYPA_PANE_ID"
```

`HYPA_PANE_ID` is your own pane. Pass `--current` to target that pane.
`hypa pane current` prints that pane when the variable is set.
`--current` fails when `HYPA_PANE_ID` is absent.
Each create command prints JSON. Read the new IDs here:

| Command | New IDs |
| --- | --- |
| `workspace create` | `.workspace_id`, and `.pane.pane_id` for the first pane |
| `tab create` | `.tab_id`, and `.pane.pane_id` for the first pane |
| `pane split` | `.pane_id` |

Find live state with:

```bash
hypa snapshot
hypa workspace list
hypa tab list --workspace "$HYPA_WORKSPACE_ID"
hypa pane list
hypa agent list
```

## Start and coordinate an agent

Split your own pane. Use the same working directory unless the user asks for another one.

```bash
hypa pane split --current --direction right --no-focus
```

`--no-focus` keeps the user's focus in your pane. Read the new pane ID from `.pane_id`. Use `right` for a wide pane and `down` for a narrow one.
Do not stack many splits in one direction. The panes become too small to read.

Start an agent in the new pane:

```bash
hypa agent start <pane_id> --kind codex
```

Run `hypa agent` to see the kinds and options. `agent start` waits until the pane holds the agent.

Send work and wait for the result:

```bash
hypa agent prompt <pane_id> "Review the current diff. Report only actions to take." --wait --timeout 120000
```

`agent prompt` needs an input lease. The CLI claims it for you.
`--wait` waits for the state in `--until`. The default is `blocked`.
To wait for `idle` or `done`, pass `--until idle`, or use `agent wait`:

```bash
hypa agent wait <pane_id> --until blocked --timeout 120000
```

Send keys for an agent screen that needs them:

```bash
hypa agent send-keys <pane_id> esc
hypa agent send-keys <pane_id> ctrl+c
```

Read the result:

```bash
hypa agent status <pane_id>
hypa agent read <pane_id>
```

## Blocked agents and errors

When an agent is `blocked`, read `agent status` and `agent read` first.
Then decide what to send. If the agent asks for an approval, ask the user.
Do not answer an approval for the user.

A timeout does not prove that the prompt was not delivered.
Read the pane before you send the same prompt again.

The CLI exit codes are:

| Exit | Meaning |
| ---: | --- |
| 0 | The call worked. |
| 1 | The runtime returned an error. stderr has `error <code>: <message>`. |
| 2 | The socket path is wrong, or the connect failed. |
| 3 | The connect or the call timed out. |
| 4 | The command line is wrong. |

The runtime error codes you can meet are:

| Code | Meaning |
| ---: | --- |
| -32602 | A parameter is wrong. Example: `pane.create` on a tab that already has a pane. |
| -32004 | The pane, tab, or workspace does not exist. |
| -32005 | The state does not allow this call. Examples: closing the last workspace, or a different source holds the agent. |
| -32006 | The call needs a lease. |
| -32007 | The lease expired. Claim it again. |
| -32008 | A different agent now occupies the pane. |
| -32009 | The placement credential is not valid. `pane create`, `pane show`, and `pane hide` return it. |
| -32010 | The pane process did not start. |

## Read sources

`pane read` takes `--source`:

- `visible`: the viewport that is on screen now.
- `recent`: recent output, with soft wraps kept.
- `recent-unwrapped`: recent output, with soft wraps joined. Use it for logs and transcripts.
- `detection`: the plain-text snapshot that agent detection uses.

`agent read` returns a compressed view. Prefer it over `pane read` for an agent.
Use `pane read` when you need exact raw text.

## Run a command in another pane

Split your pane. Keep the user's focus in your pane.

```bash
hypa pane split --current --direction right --no-focus
```

Read the new pane ID from `.pane_id`. Run the command and wait for the result:

```bash
hypa pane run <pane_id> "just test"
hypa pane wait-output <pane_id> --match "test result" --timeout 120000
hypa pane read <pane_id> --source recent-unwrapped --lines 120
```

`pane run` writes the text and Enter as one submission.
Words after the pane target are the command. A word that starts with `-` stays in the command.
`pane wait-output` matches output that already exists.
Use `--match` for a literal substring. Use `--regex` for a regular expression.
Hypa checks the regex on each output line.
Omit `--timeout` to wait until the output matches. The client sets no request deadline.
For a process that may need the user, use a hidden pane. See the next section.

## Background panes

A hidden pane keeps its PTY and VT but takes no space in the mux.
Create one with `hypa pane create --placement hidden`.

When to use one:

- Use a hidden pane for a terminal process or a helper agent that the user does not need to watch. Prefer it over `pane split`.
- Use a visible pane only when the user asked to watch the work.
- Show a hidden pane when it needs the user. Use `pane show --mode tiled` for a tab. Use `--mode overlay` with one `terminal` attach client for a modal.
- `blocked` is attention. It does not give permission to show or hide.
- Hide keeps the PTY. Close ends it. Close only a pane that you created.

How to observe and act:

- Wait with one blocking command, and branch on its exit status. Do not poll in a loop. Do not use `sleep`.
- Read JSON with `jq -r`. Do not run `eval` on JSON.
- Keep `parent_capability` in a shell variable. Do not print it or `HYPA_PANE_TOKEN`.
- Keep one `seq` per actor and credential. Increase it only after a call exits `0`.

Recipe A runs a command in a hidden pane. It shows the pane only when the wait fails:

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

Recipe B runs a helper agent in a hidden pane. A wait that times out exits `0` with `timed_out` true, so the recipe reads the state. It shows the pane only when the agent is `blocked`, and it never closes a helper that still works:

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

After the user answers a shown pane, hide it with `pane hide --seq 2`, or close it.
The steps, the `seq` rules, and the overlay mode are in [`docs/guides/background-panes.md`](../../docs/guides/background-panes.md).

## Safety rules

- Use a pane ID from JSON, or your own `HYPA_PANE_ID`. Do not use a pane ID from sidebar order.
- Do not close a workspace, tab, or pane that you did not create, unless the user asks.
- Do not print `HYPA_PANE_TOKEN` or a parent capability.
- Do not run `hypa session stop` or `hypa mux stop` from a live session, unless the user wants the server and all pane processes to stop.
- Do not kill the Hypa process. Use a named test session for experiments: `hypa --session NAME`.
- Server errors exit `1`. Usage errors exit `4`.

## More detail

- Wire protocol and method tables: [`docs/reference/agent-runtime-protocol.md`](../../docs/reference/agent-runtime-protocol.md)
- Background panes: [`docs/guides/background-panes.md`](../../docs/guides/background-panes.md)
- Pane environment: [`docs/guides/pane-environment.md`](../../docs/guides/pane-environment.md)
- Custom agent integration: [`docs/guides/custom-agent-integration.md`](../../docs/guides/custom-agent-integration.md)
