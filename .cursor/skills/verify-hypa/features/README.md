# Hypa mux verification map

Drive the mux in the GUI container. Read this index, then the journey. Compression commands are not features in this map.

## Baseline preconditions

- Linux x64, with the binaries from the skill's Launch section.
- Docker can run `hypa-verify-mux:local`.
- The journey starts its own container, sessions, sockets, and configs. Do not attach to a mux already running on the host.
- The container config sets `onboarding = true`, `experimental.pane_history = true`, and prefix `ctrl+b`. Splash is off so the first screen is onboarding.

## Driving conventions

- Run `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh` with no arguments.
- Keys are the ones in the journey. Do not add clicks to skip a step.
- Evidence stays under `artifacts/verify-hypa/<run-id>/` after the container is removed.

## Proof and skip reporting

- Capture one screenshot per step, plus the xterm logs.
- Detach requires `server.alive=true` in the detached terminal.
- Reattach requires the shell marker from before detach.
- Live-handoff requires `"ok": true` while the attached client is still up.
- Session stop requires `Stopped mux session=` and the client exiting.
- If a path cannot be reached, record the command and the unmet precondition. Do not mark a different path as a substitute.

## Features

- [Mux journey](./journey.md) — onboarding through session stop, with a screenshot of each step.
- [Remote cube](./remote-cube.md) — a second container is the host. The client pairs, drives that shell, and recovers a dropped link.
