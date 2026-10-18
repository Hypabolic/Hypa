#!/usr/bin/env bash
# Launch a GUI container and drive the mux journey. Evidence stays on the host.
# Usage: verify-mux-gui.sh
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  echo "Usage: $0" >&2
  echo "Drives onboarding, help, a right split, a pane switch, a named tab," >&2
  echo "a second session, detach, reattach, live-handoff, and session stop." >&2
  exit 2
fi
if [[ $# -gt 0 ]]; then
  echo "This harness drives one journey. Run it with no arguments." >&2
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

SHOTS=(
  01-onboarding.png
  02-workspace.png
  03-output.png
  04-help.png
  05-split.png
  06-pane-switch.png
  07-named-tab.png
  08-second-session.png
  09-detach.png
  10-reattach.png
  11-live-handoff.png
  12-session-stop.png
)

NAME="hypa-verify-$RUN_ID"
echo "$NAME" > "$EVIDENCE/container.name"
START=$(date +%s)
set +e
"${DOCKER[@]}" run --name "$NAME" \
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
END=$(date +%s)

"${DOCKER[@]}" rm -f "$NAME" >/dev/null 2>&1 || true
ELAPSED=$((END - START))
echo "status=$STATUS seconds=$ELAPSED evidence=$EVIDENCE" | tee "$EVIDENCE/run.txt"

MISSING=0
for shot in "${SHOTS[@]}"; do
  if [[ ! -s "$EVIDENCE/$shot" ]]; then
    echo "Missing screenshot $EVIDENCE/$shot" >&2
    MISSING=1
  fi
done
if [[ "$MISSING" -ne 0 ]]; then
  exit 1
fi
exit "$STATUS"
