#!/usr/bin/env bash
# Launch a GUI container and drive one mux feature. Evidence stays on the host.
# Usage: verify-mux-gui.sh <onboarding|keybindings|detach>
set -euo pipefail

FEATURE="${1:-}"
if [[ "$FEATURE" != "onboarding" && "$FEATURE" != "keybindings" && "$FEATURE" != "detach" ]]; then
  echo "Usage: $0 onboarding|keybindings|detach" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILL_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ROOT="$(cd "$SKILL_DIR/../../.." && pwd)"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
EVIDENCE="${HYPA_VERIFY_EVIDENCE:-$ROOT/artifacts/verify-hypa/$RUN_ID}"
mkdir -p "$EVIDENCE"
BIN_DIR="${HYPA_BIN_DIR:-$ROOT/src/Hypa.Cli/bin/Release/net10.0}"
GHOSTTY="${HYPA_GHOSTTY_VT:-$ROOT/native/runtimes/linux-x64/native/libghostty-vt.so}"
PTY_HOST="${HYPA_PTY_HOST:-$ROOT/native/runtimes/linux-x64/native/hypa-pty-host}"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

for path in "$BIN_DIR/hypa" "$GHOSTTY" "$PTY_HOST" "$DOTNET_ROOT/dotnet"; do
  if [[ ! -e "$path" ]]; then
    echo "Missing $path. Follow Launch in .cursor/skills/verify-hypa/SKILL.md." >&2
    exit 1
  fi
done

if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
else
  DOCKER=(sudo docker)
fi

IMAGE="hypa-verify-mux:local"
"${DOCKER[@]}" build -t "$IMAGE" "$SKILL_DIR/container" >"$EVIDENCE/docker-build.log"

NAME="hypa-verify-$RUN_ID"
echo "$NAME" > "$EVIDENCE/container.name"
set +e
"${DOCKER[@]}" run --name "$NAME" \
  -e FEATURE="$FEATURE" \
  -e RUN_ID="$RUN_ID" \
  -e EVIDENCE=/evidence \
  -e DOTNET_ROOT=/opt/dotnet \
  -e HYPA_GHOSTTY_VT=/opt/native/libghostty-vt.so \
  -e HYPA_PTY_HOST=/opt/native/hypa-pty-host \
  -v "$BIN_DIR:/opt/hypa:ro" \
  -v "$DOTNET_ROOT:/opt/dotnet:ro" \
  -v "$GHOSTTY:/opt/native/libghostty-vt.so:ro" \
  -v "$PTY_HOST:/opt/native/hypa-pty-host:ro" \
  -v "$EVIDENCE:/evidence" \
  "$IMAGE"
STATUS=$?
set -e

"${DOCKER[@]}" rm -f "$NAME" >/dev/null 2>&1 || true
echo "feature=$FEATURE status=$STATUS evidence=$EVIDENCE" | tee "$EVIDENCE/run.txt"
if [[ ! -f "$EVIDENCE/${FEATURE}-before.png" || ! -f "$EVIDENCE/${FEATURE}-after.png" ]]; then
  echo "Evidence screenshots missing in $EVIDENCE" >&2
  exit 1
fi
exit "$STATUS"
