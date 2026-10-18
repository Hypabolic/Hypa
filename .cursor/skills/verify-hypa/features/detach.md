# Detach

Prefix then `q` leaves the terminal. The mux server keeps the session.

## Sub-features

- `detach-client` closes the attach client.
- `detach-server` leaves the server reachable from `hypa status`.

## How to get to it (user POV)

- Finish onboarding, press `ctrl+b`, then press `q`.

## Driving it with verify-mux-gui

Preconditions:

- A new session shows onboarding first. The script presses Enter, then the prefix chord.

- **Detach.** `ctrl+b`, then `q`. `detach-after.png` is the screen after the client leaves.
- **Server still up.** `status-after-detach.txt` is the output of `hypa status` for this session. It is not an empty failure.

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh detach
```

## Gotchas

- `q` without the prefix is input for the pane shell, not detach.
- Cleanup runs `hypa mux stop` after the screenshot. Read `status-after-detach.txt` from before that stop.
- Do not prove detach by killing the process. The key has to be the one a person presses.
