# Mux journey

One sitting at the mux: onboarding, the shell, help, a right split, a pane switch, a named tab, a second session, detach, reattach, live-handoff, and session stop.

## Sub-features

- `onboarding-open` shows the overlay on a new session.
- `workspace-open` reaches a shell prompt.
- `shell-output` prints a marker in the pane.
- `help-open` shows the binding list.
- `split-right` opens a pane on the right.
- `pane-switch` focuses that pane and prints a second marker.
- `tab-rename` names the tab `keptab`.
- `second-session` opens another mux in its own xterm.
- `detach-client` leaves the client and leaves the server up.
- `reattach-output` shows the first marker again.
- `live-handoff` returns ok without dropping the client.
- `session-stop` stops that server and the client exits.

## How to get to it (user POV)

- Run `hypa` in a terminal when onboarding has not been completed for that config.
- Press Enter, then Escape if the integrations page is up.
- Type into the shell. Press `ctrl+b`, then the next key: `?` for help, `v` to split right, `h` and `l` to move between panes, `shift+t` to rename the tab, `q` to detach.
- Start `hypa` again with the same `HYPA_SESSION` and socket to attach.
- From another terminal, `hypa mux live-handoff`, then `hypa session stop`.

## Driving it with verify-mux-gui

Preconditions:

- Launch binaries from the skill exist.
- No container named for this run is already up.

Each step writes one screenshot. The xterm log has to contain the text that step shows: the onboarding line, `~#`, `HYPAKEEP7F3A`, the help line, `RIGHTPANE9`, `keptab`, `server.alive=true`, `"ok": true`, and `Stopped mux session=`.

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh
```

## Gotchas

- A missing `HYPA_CONFIG_PATH` file is an error when the variable is set. The container writes the file before starting hypa.
- Redirected stdin makes bare `hypa` print a snapshot and exit. The drive uses xterm so hypa has a terminal.
- Continue writes `onboarding = false` into that session's config and opens the integrations page. Escape leaves the page. The second session has its own config, so it still shows onboarding.
- What's new does not open while onboarding is showing.
- `?` is `shift+slash` on a US keyboard. `shift+t` arrives as an uppercase T, which the decoder treats as shift+t.
- `q` without the prefix is input for the pane shell, not detach.
- Detach does not stop the server. The mux server is in its own session, so closing the xterm does not take it down. Reattach paints the live panes. The line typed in the focused pane is still there. A resize can push an older line off the visible screen. `experimental.pane_history = true` also stores the screen in `session-history.json` for a later server start.
- `hypa mux live-handoff` and `hypa session stop` use `HYPA_SESSION` and `HYPA_RUNTIME_SOCKET`. Passing `--session` with a socket from a different path fails.
- The exit trap runs `hypa mux stop` after the session-stop screenshot. Read `session-stop.txt` from the step itself.
- Do not prove these steps by killing a process. The keys and the two commands have to be the ones a person runs.
