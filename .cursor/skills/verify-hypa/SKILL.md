---
name: verify-hypa
description: Drive the Hypa mux in a Linux container with a virtual display, real keystrokes, and a screenshot of each step. Use when changing attach, onboarding, keys, splits, tabs, sessions, detach, live-handoff, or session stop. Do not use it to prove hypa -c compression.
---

# Verify Hypa

The product under test is the workspace mux. Compression (`hypa -c`) is being phased out. Do not treat a compression command as proof of the mux.

Two drives must not share a runtime directory. The journey gives each session its own `HYPA_SESSION`, socket, state directory, and config. Refusing to attach to a mux you did not start beats typing into someone else's session.

## Launch

From a clean checkout, on Linux x64:

```bash
bash scripts/build-libghostty-vt.sh
bash scripts/build-hypa-pty-host.sh
bash scripts/build-tree-sitter-markdown.sh
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build src/Hypa.Cli/Hypa.Cli.csproj -c Release
```

Ready means `src/Hypa.Cli/bin/Release/net10.0/hypa --version` prints a version, and both of these exist:

- `native/runtimes/linux-x64/native/libghostty-vt.so`
- `native/runtimes/linux-x64/native/hypa-pty-host`

Then:

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh
```

The script builds `hypa-verify-mux:local` (Xvfb, Openbox, xterm, xdotool) and starts a container named `hypa-verify-<run-id>`. Inside, xterm runs `hypa`. There is no long-lived server to keep: the journey stops the sessions it started.

The framework-dependent build needs `DOTNET_ROOT` mounted at `/opt/dotnet`. A published native binary does not. This skill's proof mount is the extra step a native pack will not do.

## Doctor

The container writes `doctor.txt` before any key. It must show a version line, the Ghostty library, and the pty host. If `doctor failed` is in the container output, do not send keys. Fix the missing binary and start a new run.

## Drive

Harness: `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh`. It sends keys to the xterm window with xdotool. Prefer those keys over clicking coordinates.

The journey is one sitting. Enter leaves onboarding. Escape leaves the integrations page that follows it. The shell then prints a marker. `ctrl+b` then `?` opens help. `ctrl+b` then `v` splits right. `ctrl+b` then `h`, then `ctrl+b` then `l`, moves into the right pane. `ctrl+b` then `shift+t` names the tab. A second xterm starts another session. `ctrl+b` then `q` detaches. A new xterm attaches again and the marker is still on screen. `hypa mux live-handoff` returns ok while that client stays up. `hypa session stop` ends the server. The recipes are in `features/`.

## Evidence

Each run writes `artifacts/verify-hypa/<run-id>/`. That directory is gitignored. Keep it. Proof is the twelve screenshots, the xterm logs, `doctor.txt`, `live-handoff.txt`, and `session-stop.txt`. A screenshot alone is not proof: the log has to contain the text that step was supposed to show.

## Cleanup

The script removes only the container it created (`docker rm -f hypa-verify-<run-id>`). It does not delete the evidence directory. Inside the container, the exit trap kills the xterms and Xvfb it started and runs `hypa mux stop` for both sessions. Do not `killall hypa` or `killall xterm`.

## Helpers

- `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh` builds the image, drives the journey, leaves evidence on the mounted directory, removes the container, and exits non-zero if any step screenshot is missing.
- `.cursor/skills/verify-hypa/container/drive-inside.sh` is the process inside the container. The Dockerfile copies it to `/usr/local/bin/drive-inside.sh`.

After the suite changes, run `/maintain-verification-skill` so the feature map stays honest.
