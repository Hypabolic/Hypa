---
name: verify-hypa
description: Drive the Hypa mux in a Linux container with a virtual display, real keystrokes, and screenshots. Use when changing attach, onboarding, keybindings, detach, or any user-visible mux behavior. Do not use it to prove hypa -c compression.
---

# Verify Hypa

The product under test is the workspace mux. Compression (`hypa -c`) is being phased out. Do not treat a compression command as proof of the mux.

Two drives must not share a runtime directory. Each run sets its own `HYPA_SESSION`, socket, and config. Refusing to attach to a mux you did not start beats typing into someone else's session.

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

Then drive one feature:

```bash
bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh onboarding
```

The script builds `hypa-verify-mux:local` (Xvfb, Openbox, xterm, xdotool) and starts a container named `hypa-verify-<run-id>`. Inside, xterm runs `hypa` on a new session. The onboarding overlay is ready when the xterm log contains `hypa`, `continue`, or `workspace`. There is no long-lived server to keep: each feature gets its own container.

The framework-dependent build needs `DOTNET_ROOT` mounted at `/opt/dotnet`. A published native binary does not. This skill's proof mount is the extra step a native pack will not do.

## Doctor

The container writes `doctor.txt` before any key. It must show a version line, the Ghostty library, and the pty host. If `doctor failed` is in the container output, do not send keys. Fix the missing binary and start a new run.

## Drive

Harness: `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh` with `onboarding`, `keybindings`, or `detach`. It sends keys to the xterm window with xdotool. Prefer those keys over clicking coordinates.

Enter continues onboarding. `ctrl+b` then `?` opens key bindings. `ctrl+b` then `q` detaches. The recipes are in `features/`.

## Evidence

Each run writes `artifacts/verify-hypa/<run-id>/`. That directory is gitignored. Keep it. Proof is the before screenshot, the after screenshot, `xterm.log`, and `doctor.txt`. A screenshot alone is not proof: the log has to show the text the key was supposed to reveal, and detach also writes `status-after-detach.txt` from `hypa status` after the client exits.

## Cleanup

The script removes only the container it created (`docker rm -f hypa-verify-<run-id>`). It does not delete the evidence directory. Inside the container, the exit trap kills the xterm and Xvfb it started and runs `hypa mux stop` for that session. Do not `killall hypa` or `killall xterm`.

## Helpers

- `bash .cursor/skills/verify-hypa/scripts/verify-mux-gui.sh <feature>` builds the image, drives the feature, copies nothing (the evidence directory is a mount), removes the container, and exits non-zero if either screenshot is missing.
- `.cursor/skills/verify-hypa/container/drive-inside.sh` is the process inside the container. The Dockerfile copies it to `/usr/local/bin/drive-inside.sh`.

After the suite changes, run `/maintain-verification-skill` so the feature map stays honest.
