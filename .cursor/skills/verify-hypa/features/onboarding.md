# Onboarding

The first attach shows an overlay named hypa. Enter dismisses it and leaves the workspace on screen.

## Sub-features

- `onboarding-open` shows the overlay on a new session.
- `onboarding-continue` dismisses it with Enter.

## How to get to it (user POV)

- Run `hypa` in a terminal when onboarding has not been completed for that config.

## Driving it with verify-mux-gui

Preconditions:

- Launch binaries from the skill exist.
- No container named for this run is already up.

- **Open.** The script starts xterm running `hypa`. `artifacts/verify-hypa/<run-id>/onboarding-before.png` shows the overlay, and `xterm.log` contains the onboarding copy.
- **Continue.** The script presses Enter. `onboarding-after.png` is the workspace after the overlay closes. `xterm.log` gained the frame from that key.

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh onboarding
```

## Gotchas

- A missing `HYPA_CONFIG_PATH` file is an error when the variable is set. The container writes `onboarding = true` before starting hypa.
- Redirected stdin makes bare `hypa` print a snapshot and exit. The drive uses xterm so hypa has a terminal.
- What's new does not open while onboarding is showing. Enter on this screen continues onboarding, not the release notes.
