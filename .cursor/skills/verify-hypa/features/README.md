# Hypa mux verification map

Drive the mux in the GUI container. Read this index, then one feature file. Compression commands are not features in this map.

## Baseline preconditions

- Linux x64, with the binaries from the skill's Launch section.
- Docker can run `hypa-verify-mux:local`.
- Each recipe starts its own container, session, socket, and config. Do not attach to a mux already running on the host.
- The container config sets `onboarding = true` and prefix `ctrl+b`.

## Driving conventions

- Run `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh <feature>`.
- Keys are the ones in the feature file. Do not add clicks to skip a step.
- Evidence stays under `artifacts/verify-hypa/<run-id>/` after the container is removed.

## Proof and skip reporting

- Capture the screen before the key and after it, plus `xterm.log`.
- Detach also requires `status-after-detach.txt`.
- If a path cannot be reached, record the command and the unmet precondition. Do not mark a different path as a substitute.

## Features

- [Onboarding](./onboarding.md) — first attach shows the overlay; Enter opens the workspace.
- [Key bindings](./keybindings.md) — prefix then `?` lists bindings.
- [Detach](./detach.md) — prefix then `q` leaves the client; the server is still there.
